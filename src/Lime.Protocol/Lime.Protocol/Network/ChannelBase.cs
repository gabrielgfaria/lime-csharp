﻿using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Lime.Protocol.Util;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Lime.Protocol.Network
{
    /// <summary>
    /// Base class for the protocol communication channels.
    /// </summary>
    public abstract class ChannelBase : IChannel, IDisposable
    {        
        public const string PING_MEDIA_TYPE = "application/vnd.lime.ping+json";
        public const string PING_URI = "/ping";

        private readonly static Document PingDocument = new JsonDocument(MediaType.Parse(PING_MEDIA_TYPE));
        private readonly static TimeSpan OnRemoteIdleTimeout = TimeSpan.FromSeconds(30);

        private readonly TimeSpan _sendTimeout;
        private readonly bool _fillEnvelopeRecipients;
        private readonly bool _autoReplyPings;

        // Ping remote task
        private readonly TimeSpan _remotePingInterval;
        private readonly TimeSpan _remoteIdleTimeout;

        // Resend unotified messages task
        private readonly int _resendMessageTryCount;
        private readonly TimeSpan _resendMessageInterval;
        private ConcurrentDictionary<Guid, SentMessage> _sentMessageDictionary;

        private readonly IAsyncQueue<Message> _messageBuffer;
        private readonly IAsyncQueue<Command> _commandBuffer;
        private readonly IAsyncQueue<Notification> _notificationBuffer;
        private readonly IAsyncQueue<Session> _sessionBuffer;
        private readonly CancellationTokenSource _channelCancellationTokenSource;
        private readonly object _syncRoot;
        private SessionState _state;

        private Task _consumeTransportTask;
        private Task _pingRemoteTask;
        private Task _resendMessagesTask;

        private bool _isConsumeTransportTaskFaulting;
        private bool _isDisposing;
        
        protected DateTimeOffset LastReceivedEnvelope;

        #region Constructors

        /// <summary>
        /// Creates a new instance of ChannelBase
        /// </summary>
        /// <param name="transport"></param>
        /// <param name="sendTimeout"></param>
        /// <param name="buffersLimit"></param>
        /// <param name="fillEnvelopeRecipients">Indicates if the from and to properties of sent and received envelopes should be filled with the session information if not defined.</param>
        /// <param name="autoReplyPings">Indicates if the channel should reply automatically to ping request commands. In this case, the ping command are not returned by the ReceiveCommandAsync method.</param>
        /// <param name="remotePingInterval">The interval to ping the remote party.</param>
        /// <param name="remoteIdleTimeout">The timeout to close the channel due to inactivity.</param>
        /// <param name="resendMessageTryCount">Indicates the number of attemps to resend messages that were not notified as received by the destination.</param>
        /// <param name="resendMessageInterval">The interval to resend the messages.</param>
        protected ChannelBase(ITransport transport, TimeSpan sendTimeout, int buffersLimit, bool fillEnvelopeRecipients, bool autoReplyPings, TimeSpan? remotePingInterval, TimeSpan? remoteIdleTimeout, int resendMessageTryCount, TimeSpan? resendMessageInterval)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            Transport = transport;
            Transport.Closing += Transport_Closing;

            _sendTimeout = sendTimeout;
            _fillEnvelopeRecipients = fillEnvelopeRecipients;
            _autoReplyPings = autoReplyPings;
            _remotePingInterval = remotePingInterval ?? TimeSpan.Zero;
            _remoteIdleTimeout = remoteIdleTimeout ?? TimeSpan.Zero;

            _resendMessageTryCount = resendMessageTryCount;
            _resendMessageInterval = resendMessageInterval ?? TimeSpan.Zero;

            _channelCancellationTokenSource = new CancellationTokenSource();
            _syncRoot = new object();
            State = SessionState.New;

            _messageBuffer = new BufferBlockAsyncQueue<Message>(buffersLimit);
            _commandBuffer = new BufferBlockAsyncQueue<Command>(buffersLimit);
            _notificationBuffer = new BufferBlockAsyncQueue<Notification>(buffersLimit);
            _sessionBuffer = new BufferBlockAsyncQueue<Session>(1);
        }

        ~ChannelBase()
        {
            Dispose(false);
        }

        #endregion

        #region IChannel Members

        /// <summary>
        /// The current session transport
        /// </summary>
        public ITransport Transport { get; }

        /// <summary>
        /// Remote node identifier
        /// </summary>
        public Node RemoteNode { get; protected set; }

        /// <summary>
        /// Remote node identifier
        /// </summary>
        public Node LocalNode { get; protected set; }

        /// <summary>
        /// The session Id
        /// </summary>
        public Guid SessionId { get; protected set; }        

        /// <summary>
        /// Current session state
        /// </summary>
        public SessionState State
        {
            get { return _state; }
            protected set
            {
                _state = value;

                if (_state == SessionState.Established)
                {
                    StartChannelTasks();
                }
            }
        }

        #endregion

        #region IMessageChannel Members

        /// <summary>
        /// Sends a message to the remote node.
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">message</exception>
        /// <exception cref="System.InvalidOperationException"></exception>
        public virtual async Task SendMessageAsync(Message message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (State != SessionState.Established)
            {
                throw new InvalidOperationException($"Cannot send a message in the '{State}' session state");
            }

            await SendAsync(message).ConfigureAwait(false);

            if (message.Id != Guid.Empty &&
                _sentMessageDictionary != null &&
                _resendMessagesTask?.Status == TaskStatus.Running)
            {
                _sentMessageDictionary.TryAdd(message.Id, new SentMessage(message));                
            }
        }

        /// <summary>
        /// Receives a message from the remote node.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public virtual Task<Message> ReceiveMessageAsync(CancellationToken cancellationToken)
        {
            return ReceiveEnvelopeAsync(_messageBuffer, cancellationToken);
        }

        #endregion

        #region ICommandChannel Members

        /// <summary>
        /// Sends a command envelope to the remote node.
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">message</exception>
        /// <exception cref="System.InvalidOperationException"></exception>
        public virtual Task SendCommandAsync(Command command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (State != SessionState.Established)
            {
                throw new InvalidOperationException($"Cannot send a command in the '{State}' session state");
            }

            return SendAsync(command);
        }

        /// <summary>
        /// Receives a command from the remote node.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        /// <exception cref="System.NotImplementedException"></exception>
        public virtual Task<Command> ReceiveCommandAsync(CancellationToken cancellationToken)
        {
            return ReceiveEnvelopeAsync(_commandBuffer, cancellationToken);
        }

        #endregion

        #region INotificationChannel Members

        /// <summary>
        /// Sends a notification to the remote node.
        /// </summary>
        /// <param name="notification"></param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">notification</exception>
        /// <exception cref="System.InvalidOperationException"></exception>
        public virtual Task SendNotificationAsync(Notification notification)
        {
            if (notification == null) throw new ArgumentNullException(nameof(notification));
            if (State != SessionState.Established)
            {
                throw new InvalidOperationException($"Cannot send a notification in the '{State}' session state");
            }

            return SendAsync(notification);
        }

        /// <summary>
        /// Receives a notification from the remote node.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        /// <exception cref="System.NotImplementedException"></exception>
        public virtual Task<Notification> ReceiveNotificationAsync(CancellationToken cancellationToken)
        {
            return ReceiveEnvelopeAsync(_notificationBuffer, cancellationToken);
        }

        #endregion

        #region ISessionChannel Members

        /// <summary>
        /// Sends a session change message to the remote node. 
        /// Avoid to use this method directly. Instead, use the Server or Client channel methods.
        /// </summary>
        /// <param name="session"></param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">session</exception>
        public virtual Task SendSessionAsync(Session session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (State == SessionState.Finished || State == SessionState.Failed)
            {
                throw new InvalidOperationException($"Cannot send a session in the '{State}' session state");
            }

            return SendAsync(session);
        }

        /// <summary>
        /// Receives a session from the remote node.
        /// Avoid to use this method directly. Instead, use the Server or Client channel methods.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public virtual async Task<Session> ReceiveSessionAsync(CancellationToken cancellationToken)
        {
            switch (State)
            {
                case SessionState.Finished:
                    throw new InvalidOperationException($"Cannot receive a session in the '{State}' session state");
                case SessionState.Established:
                    using (var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                        _channelCancellationTokenSource.Token, cancellationToken))
                    {
                        return await ReceiveEnvelopeAsync(_sessionBuffer, linkedCancellationTokenSource.Token).ConfigureAwait(false);
                    }
            }

            var result = await ReceiveAsync(cancellationToken).ConfigureAwait(false);

            var session = result as Session;
            if (session != null) return session;
            await Transport.CloseAsync(_channelCancellationTokenSource.Token).ConfigureAwait(false);
            throw new InvalidOperationException("An unexpected envelope type was received from the transport.");
        }

        #endregion

        #region Private Methods

        private void StartChannelTasks()
        {
            lock (_syncRoot)
            {
                if (_consumeTransportTask == null)
                {
                    _consumeTransportTask =
                        Task.Factory.StartNew(ConsumeTransportAsync, TaskCreationOptions.LongRunning)
                        .Unwrap();
                }

                if (_pingRemoteTask == null &&
                    _remotePingInterval > TimeSpan.Zero)
                {
                    _pingRemoteTask =
                        Task.Factory.StartNew(PingRemoteAsync)
                        .Unwrap();
                }

                if (_resendMessagesTask == null &&
                    _resendMessageTryCount > 0)
                {
                    _resendMessagesTask =
                        Task.Factory.StartNew(ResendMessagesAsync)
                        .Unwrap();
                }
            }
        }


        private async Task ConsumeTransportAsync()
        {
            try
            {
                while (IsChannelEstablished())
                {
                    Exception exception = null;

                    try
                    {
                        var envelope = await ReceiveAsync(_channelCancellationTokenSource.Token).ConfigureAwait(false);
                        if (envelope == null) continue;

                        await ConsumeEnvelopeAsync(envelope);
                    }
                    catch (OperationCanceledException ex)
                    {
                        if (!_channelCancellationTokenSource.IsCancellationRequested) exception = ex;
                    }
                    catch (ObjectDisposedException ex)
                    {
                        if (!_isDisposing) exception = ex;
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }

                    if (exception != null)
                    {
                        _isConsumeTransportTaskFaulting = true;
                        if (Transport.IsConnected)
                        {
                            using (var cts = new CancellationTokenSource(_sendTimeout))
                            {
                                await Transport.CloseAsync(cts.Token).ConfigureAwait(false);
                            }
                        }
                        ExceptionDispatchInfo.Capture(exception).Throw();
                    }
                }
            }
            finally
            {
                if (!_channelCancellationTokenSource.IsCancellationRequested)
                {
                    _channelCancellationTokenSource.Cancel();
                }
            }
        }

        private async Task PingRemoteAsync()
        {
            LastReceivedEnvelope = DateTime.UtcNow;
            while (IsChannelEstablished())
            {
                try
                {
                    await Task.Delay(_remotePingInterval, _channelCancellationTokenSource.Token).ConfigureAwait(false);
                    if (IsChannelEstablished())
                    {
                        var idleTime = DateTimeOffset.UtcNow - LastReceivedEnvelope;

                        if (_remoteIdleTimeout > TimeSpan.Zero &&
                            idleTime >= _remoteIdleTimeout)
                        {
                            using (var cts = new CancellationTokenSource(OnRemoteIdleTimeout))
                            {
                                await OnRemoteIdleAsync(cts.Token).ConfigureAwait(false);
                            }
                        }
                        else if (idleTime >= _remotePingInterval)
                        {
                            // Send a ping command to the remote party
                            var pingCommandRequest = new Command(Guid.NewGuid())
                            {
                                Method = CommandMethod.Get,
                                Uri = new LimeUri(PING_URI)
                            };

                            await SendCommandAsync(pingCommandRequest).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!_channelCancellationTokenSource.IsCancellationRequested) throw;
                    break;
                }
            }
        }


        private async Task ResendMessagesAsync()
        {
            _sentMessageDictionary = new ConcurrentDictionary<Guid, SentMessage>();

            while (IsChannelEstablished())
            {
                try
                {
                    await Task.Delay(_resendMessageInterval, _channelCancellationTokenSource.Token);

                    var referenceDate = DateTimeOffset.UtcNow - _resendMessageInterval;
                    var messageIds = _sentMessageDictionary
                        .Where(s => s.Value.SentDate <= referenceDate)
                        .Select(s => s.Key)
                        .ToArray();

                    foreach (var messageId in messageIds)
                    {
                        _channelCancellationTokenSource.Token.ThrowIfCancellationRequested();

                        SentMessage sentMessage;
                        if (_sentMessageDictionary.TryGetValue(messageId, out sentMessage))
                        {
                            await SendMessageAsync(sentMessage.Message);

                            if (sentMessage.SentCount > _resendMessageTryCount)
                            {
                                _sentMessageDictionary.TryRemove(messageId, out sentMessage);
                            }
                            else
                            {
                                sentMessage.IncrementSentCount();
                            }
                        }                        
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!_channelCancellationTokenSource.IsCancellationRequested) throw;
                    break;
                }
            }
        }

        private bool IsChannelEstablished()
        {
            return
                !_channelCancellationTokenSource.IsCancellationRequested &&
                State == SessionState.Established &&
                Transport.IsConnected;
        }

        /// <summary>
        /// Cancels the token that is associated to the channel send and receive tasks.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Transport_Closing(object sender, DeferralEventArgs e)
        {
            using (e.GetDeferral())
            {
                if (!_channelCancellationTokenSource.IsCancellationRequested)
                {
                    _channelCancellationTokenSource.Cancel();
                }
            }
        }

        private async Task ConsumeEnvelopeAsync(Envelope envelope)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            
            if (envelope is Notification)
            {
                await ConsumeNotificationAsync((Notification)envelope).ConfigureAwait(false);
            }
            else if (envelope is Message)
            {
                await ConsumeMessageAsync((Message)envelope).ConfigureAwait(false);
            }
            else if (envelope is Command)
            {
                await ConsumeCommandAsync((Command)envelope).ConfigureAwait(false);
            }
            else if (envelope is Session)
            {
                await ConsumeSessionAsync((Session)envelope).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException("Invalid or unknown envelope received by the transport.");
            }
        }

        private Task ConsumeMessageAsync(Message message) => SendEnvelopeToBufferAsync(_messageBuffer, message);

        private async Task ConsumeCommandAsync(Command command)
        {
            if (_autoReplyPings &&
                command.IsPingRequest())
            {
                var pingCommandResponse = new Command
                {
                    Id = command.Id,
                    To = command.From,
                    Status = CommandStatus.Success,
                    Method = CommandMethod.Get,
                    Resource = PingDocument
                };

                await SendCommandAsync(pingCommandResponse).ConfigureAwait(false);
            }
            else
            {
                await SendEnvelopeToBufferAsync(_commandBuffer, command);
            }
        }

        private Task ConsumeNotificationAsync(Notification notification)
        {
            if (notification.Event == Event.Received ||
                notification.Event == Event.Failed)
            {
                SentMessage sentMessage;
                if (_sentMessageDictionary != null)
                {
                    _sentMessageDictionary.TryRemove(notification.Id, out sentMessage);
                }
            }            

            return SendEnvelopeToBufferAsync(_notificationBuffer, notification);
        }

        private Task ConsumeSessionAsync(Session session)
        {
            if (!_sessionBuffer.Post(session))
            {
                throw new InvalidOperationException("Session buffer limit reached");
            }

            return Task.FromResult<object>(null);
        }

        private async Task SendEnvelopeToBufferAsync<T>(IAsyncQueue<T> buffer, T envelope) where T : Envelope, new()
        {
            if (!await buffer.SendAsync(envelope, _channelCancellationTokenSource.Token))
            {
                throw new InvalidOperationException($"{typeof(T).Name} buffer limit reached");
            }
        }

        /// <summary>
        /// Sends the envelope to the transport.
        /// </summary>
        /// <param name="envelope">The envelope.</param>
        /// <returns></returns>
        private async Task SendAsync(Envelope envelope)
        {
            if (!Transport.IsConnected)
            {
                throw new InvalidOperationException("The transport is not connected");
            }

            if (_fillEnvelopeRecipients)
            {
                FillEnvelope(envelope, true);
            }

            using (var timeoutCancellationTokenSource = new CancellationTokenSource(_sendTimeout))
            {
                using (var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                        _channelCancellationTokenSource.Token, timeoutCancellationTokenSource.Token))
                {
                    await Transport.SendAsync(
                        envelope,
                        linkedCancellationTokenSource.Token)
                        .ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Receives an envelope from the transport.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        private async Task<Envelope> ReceiveAsync(CancellationToken cancellationToken)
        {
            var envelope = await Transport.ReceiveAsync(cancellationToken);

            LastReceivedEnvelope = DateTimeOffset.UtcNow;

            if (envelope != null &&
                _fillEnvelopeRecipients)
            {
                FillEnvelope(envelope, false);
            }

            return envelope;
        }

        /// <summary>
        /// Receives an envelope
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="buffer"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<T> ReceiveEnvelopeAsync<T>(IAsyncQueue<T> buffer, CancellationToken cancellationToken) where T : Envelope
        {
            if (State != SessionState.Established ||                 
                _isConsumeTransportTaskFaulting ||
                _consumeTransportTask.IsFaulted)
            {
                T envelope;
                if (buffer.TryTake(out envelope))
                {
                    return envelope;
                }

                if (State != SessionState.Established)
                {
                    throw new InvalidOperationException($"Cannot receive more envelopes in the '{State}' session state");

                }
                await _consumeTransportTask;
            }

            try
            {
                return await ReceiveFromBufferAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (_isConsumeTransportTaskFaulting) await _consumeTransportTask;
                throw;
            }
        }

        /// <summary>
        /// Receives an envelope from the buffer.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="buffer"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<T> ReceiveFromBufferAsync<T>(IAsyncQueue<T> buffer, CancellationToken cancellationToken) where T : Envelope
        {
            using (var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                _channelCancellationTokenSource.Token, cancellationToken))
            {
                return await buffer.ReceiveAsync(linkedCancellationTokenSource.Token).ConfigureAwait(false);
            }
        }

        #endregion

        #region Protected Methods

        /// <summary>
        /// Fills the envelope recipients using the session information.
        /// </summary>
        /// <param name="envelope"></param>
        /// <param name="isSending"></param>
        protected virtual void FillEnvelope(Envelope envelope, bool isSending)
        {
            if (!isSending)
            {
                // Receiving
                var from = RemoteNode;
                var to = LocalNode;

                if (from != null)
                {
                    if (envelope.From == null)
                    {
                        envelope.From = from.Copy();
                    }
                    else if (string.IsNullOrEmpty(envelope.From.Domain))
                    {
                        envelope.From.Domain = from.Domain;
                    }
                }

                if (to != null)
                {
                    if (envelope.To == null)
                    {
                        envelope.To = to.Copy();
                    }
                    else if (string.IsNullOrEmpty(envelope.To.Domain))
                    {
                        envelope.To.Domain = to.Domain;
                    }
                }
            }
        }

        /// <summary>
        /// Handles a remote idle event.
        /// </summary>
        /// <param name="cancellationToken">The operation cancellation token.</param>
        /// <returns></returns>
        protected abstract Task OnRemoteIdleAsync(CancellationToken cancellationToken);

        #endregion

        #region IDisposable Members

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _isDisposing = true;

                if (!_channelCancellationTokenSource.IsCancellationRequested)
                {
                    _channelCancellationTokenSource.Cancel();
                }

                _channelCancellationTokenSource.Dispose();
                Transport.DisposeIfDisposable();
                if (_consumeTransportTask?.IsCompleted ?? false)
                {
                    _consumeTransportTask?.Dispose();
                }

                if (_pingRemoteTask?.IsCompleted ?? false)
                {
                    _pingRemoteTask?.Dispose();
                }
            }
        }

        #endregion

        private class SentMessage
        {
            private Message _message;

            private const string SENT_COUNT_KEY = "#sendCount";

            public SentMessage(Message message)
                : this(message, DateTimeOffset.UtcNow, 1)
            {

            }

            private SentMessage(Message message, DateTimeOffset sentDate, int sentCount)
            {
                if (message == null) throw new ArgumentNullException(nameof(Message));
                _message = message;
                SentDate = sentDate;
                SentCount = sentCount;
            }

            public Message Message
            {
                get
                {
                    if (_message.Metadata == null) _message.Metadata = new Dictionary<string, string>();
                    _message.Metadata.Remove(SENT_COUNT_KEY);
                    _message.Metadata.Add(SENT_COUNT_KEY, SentCount.ToString());
                    return _message;
                }
            }

            public DateTimeOffset SentDate { get; private set; }

            public int SentCount { get; private set; }

            public void IncrementSentCount()
            {
                SentCount++;
                SentDate = DateTimeOffset.UtcNow;
            }
        }

    }
}
