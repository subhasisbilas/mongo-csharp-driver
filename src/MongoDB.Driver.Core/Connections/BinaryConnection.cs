﻿/* Copyright 2013-2014 MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver.Core.Async;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Core.Servers;
using MongoDB.Driver.Core.WireProtocol.Messages;
using MongoDB.Driver.Core.WireProtocol.Messages.Encoders.BinaryEncoders;

namespace MongoDB.Driver.Core.Connections
{
    /// <summary>
    /// Represents a connection using the binary wire protocol over a binary stream.
    /// </summary>
    public class BinaryConnection : IRootConnection
    {
        // fields
        private readonly CancellationToken _backgroundTaskCancellationToken;
        private readonly CancellationTokenSource _backgroundTaskCancellationTokenSource;
        private ConnectionId _connectionId;
        private bool _disposed;
        private DnsEndPoint _endPoint;
        private readonly AsyncDropbox<int, InboundDropboxEntry> _inboundDropbox = new AsyncDropbox<int, InboundDropboxEntry>();
        private ConnectionDescription _description;
        private readonly IMessageListener _listener;
        private readonly AsyncQueue<OutboundQueueEntry> _outboundQueue = new AsyncQueue<OutboundQueueEntry>();
        private int _pendingResponseCount;
        private readonly ConnectionSettings _settings;
        private Stream _stream;
        private readonly IStreamFactory _streamFactory;

        // constructors
        public BinaryConnection(ServerId serverId, DnsEndPoint endPoint, ConnectionSettings settings, IStreamFactory streamFactory, IMessageListener listener)
        {
            _endPoint = Ensure.IsNotNull(endPoint, "endPoint");
            _settings = Ensure.IsNotNull(settings, "settings");
            _streamFactory = Ensure.IsNotNull(streamFactory, "streamFactory");
            _listener = listener;

            _backgroundTaskCancellationTokenSource = new CancellationTokenSource();
            _backgroundTaskCancellationToken = _backgroundTaskCancellationTokenSource.Token;
            _connectionId = new ConnectionId(serverId);
            // postpone creating the Stream until OpenAsync because some Streams block or throw in the constructor
        }

        // properties
        public ConnectionId ConnectionId
        {
            get { return _connectionId; }
        }

        public DnsEndPoint EndPoint
        {
            get { return _endPoint; }
        }

        public ConnectionDescription Description
        {
            get { return _description; }
        }

        public int PendingResponseCount
        {
            get
            {
                return Interlocked.CompareExchange(ref _pendingResponseCount, 0, 0);
            }
        }

        public ConnectionSettings Settings
        {
            get { return _settings; }
        }

        // methods
        public void ConnectionFailed(Exception ex)
        {
            // TODO: what?
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_disposed)
                {
                    _backgroundTaskCancellationTokenSource.Cancel();
                    _backgroundTaskCancellationTokenSource.Dispose();
                }
            }
            _disposed = true;
        }

        public IConnection Fork()
        {
            throw new NotSupportedException();
        }

        private async Task OnSentMessagesAsync(List<RequestMessage> messages, Exception ex, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (_listener != null)
            {
                var slidingTimeout = new SlidingTimeout(timeout);
                foreach (var message in messages)
                {
                    var args = new SentMessageEventArgs(_endPoint, _connectionId, message, ex);
                    await _listener.SentMessageAsync(args, slidingTimeout, cancellationToken);
                }
            }
        }

        public async Task OpenAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            _stream = await _streamFactory.CreateStreamAsync(_endPoint, timeout, cancellationToken);
            StartBackgroundTasks();
        }

        private async Task ReceiveBackgroundTask()
        {
            try
            {
                while (true)
                {
                    _backgroundTaskCancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var messageSizeBytes = new byte[4];
                        await _stream.FillBufferAsync(messageSizeBytes, 0, 4, _backgroundTaskCancellationToken);
                        var messageSize = BitConverter.ToInt32(messageSizeBytes, 0);
                        var buffer = ByteBufferFactory.Create(BsonChunkPool.Default, messageSize);
                        buffer.WriteBytes(0, messageSizeBytes, 0, 4);
                        await _stream.FillBufferAsync(buffer, 4, messageSize - 4, _backgroundTaskCancellationToken);
                        var responseToBytes = new byte[4];
                        buffer.ReadBytes(8, responseToBytes, 0, 4);
                        var responseTo = BitConverter.ToInt32(responseToBytes, 0);
                        _inboundDropbox.Post(responseTo, new InboundDropboxEntry(buffer));
                        Interlocked.Decrement(ref _pendingResponseCount);
                    }
                    catch (Exception ex)
                    {
                        ConnectionFailed(ex);
                        throw;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // ignore TaskCanceledException
            }
        }

        public async Task<ReplyMessage<TDocument>> ReceiveMessageAsync<TDocument>(int responseTo, IBsonSerializer<TDocument> serializer, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var slidingTimeout = new SlidingTimeout(timeout);
            var entry = await _inboundDropbox.ReceiveAsync(responseTo, slidingTimeout, cancellationToken);

            ReplyMessage<TDocument> reply;
            if (entry.Buffer != null)
            {
                using (var buffer = entry.Buffer)
                using (var stream = new ByteBufferStream(buffer, ownsByteBuffer: false))
                {
                    var readerSettings = BsonBinaryReaderSettings.Defaults; // TODO: where are reader settings supposed to come from?
                    var binaryReader = new BsonBinaryReader(stream, readerSettings);
                    var encoderFactory = new BinaryMessageEncoderFactory(binaryReader);
                    var encoder = encoderFactory.GetReplyMessageEncoder<TDocument>(serializer);
                    reply = encoder.ReadMessage();
                }
            }
            else
            {
                reply = (ReplyMessage<TDocument>)entry.Reply;
            }

            ReplyMessage<TDocument> substituteReply = null;
            if (_listener != null)
            {
                var args = new ReceivedMessageEventArgs(_endPoint, _connectionId, reply);
                await _listener.ReceivedMessageAsync(args, slidingTimeout, cancellationToken);
                substituteReply = (ReplyMessage<TDocument>)args.SubstituteReply;
            }

            return substituteReply ?? reply;
        }

        private async Task SendBackgroundTask()
        {
            try
            {
                while (true)
                {
                    _backgroundTaskCancellationToken.ThrowIfCancellationRequested();

                    var entry = await _outboundQueue.DequeueAsync();
                    using (var buffer = entry.Buffer)
                    {
                        try
                        {
                            await _stream.WriteBufferAsync(buffer, 0, buffer.Length, _backgroundTaskCancellationToken);
                            entry.TaskCompletionSource.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            entry.TaskCompletionSource.TrySetException(ex);
                            ConnectionFailed(ex);
                            throw;
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // ignore TaskCanceledException
            }
        }

        public async Task SendMessagesAsync(IEnumerable<RequestMessage> messages, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var slidingTimeout = new SlidingTimeout(timeout);
            var sentMessages = new List<RequestMessage>();
            var substituteReplies = new Dictionary<int, ReplyMessage>();

            Exception exception = null;
            using (var buffer = new MultiChunkBuffer(BsonChunkPool.Default))
            {
                using (var stream = new ByteBufferStream(buffer, ownsByteBuffer: false))
                {
                    var writerSettings = BsonBinaryWriterSettings.Defaults;
                    var binaryWriter = new BsonBinaryWriter(stream, writerSettings);
                    var encoderFactory = new BinaryMessageEncoderFactory(binaryWriter);
                    foreach (var message in messages)
                    {
                        RequestMessage substituteMessage = null;
                        ReplyMessage substituteReply = null;
                        if (_listener != null)
                        {
                            var args = new SendingMessageEventArgs(_endPoint, _connectionId, message);
                            await _listener.SendingMessageAsync(args, slidingTimeout, cancellationToken);
                            substituteMessage = args.SubstituteMessage;
                            substituteReply = args.SubstituteReply;
                        }

                        var actualMessage = substituteMessage ?? message;
                        sentMessages.Add(actualMessage);

                        if (substituteReply == null)
                        {
                            var encoder = actualMessage.GetEncoder(encoderFactory);
                            encoder.WriteMessage(actualMessage);
                        }
                        else
                        {
                            substituteReplies.Add(message.RequestId, substituteReply);
                        }
                    }
                    buffer.Length = (int)stream.Length;
                }

                if (buffer.Length > 0)
                {
                    try
                    {
                        var entry = new OutboundQueueEntry(buffer, cancellationToken);
                        _outboundQueue.Enqueue(entry);
                        Interlocked.Increment(ref _pendingResponseCount);
                        await entry.Task;
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }
                }
            }

            await OnSentMessagesAsync(sentMessages, exception, slidingTimeout, cancellationToken);
            if (exception != null)
            {
                throw exception;
            }

            foreach (var requestId in substituteReplies.Keys)
            {
                _inboundDropbox.Post(requestId, new InboundDropboxEntry(substituteReplies[requestId]));
            }
        }

        public void SetConnectionId(ConnectionId connectionId)
        {
            _connectionId = Ensure.IsNotNull(connectionId, "connectionId");
        }

        public void SetDescription(ConnectionDescription description)
        {
            _description = Ensure.IsNotNull(description, "description");
        }

        private void StartBackgroundTasks()
        {
            SendBackgroundTask().LogUnobservedExceptions();
            ReceiveBackgroundTask().LogUnobservedExceptions();
        }

        // nested classes
        private class InboundDropboxEntry
        {
            // fields
            private readonly IByteBuffer _buffer;
            private readonly ReplyMessage _reply;

            // constructors
            public InboundDropboxEntry(IByteBuffer buffer)
            {
                _buffer = buffer;
            }

            public InboundDropboxEntry(ReplyMessage reply)
            {
                _reply = reply;
            }

            // properties
            public IByteBuffer Buffer
            {
                get { return _buffer; }
            }

            public ReplyMessage Reply
            {
                get { return _reply; }
            }
        }

        private class OutboundQueueEntry
        {
            // fields
            private readonly IByteBuffer _buffer;
            private readonly CancellationToken _cancellationToken;
            private readonly TaskCompletionSource<bool> _taskCompletionSource;

            // constructors
            public OutboundQueueEntry(IByteBuffer buffer, CancellationToken cancellationToken)
            {
                _buffer = buffer;
                _cancellationToken = cancellationToken;
                _taskCompletionSource = new TaskCompletionSource<bool>();
            }

            // properties
            public CancellationToken CancellationToken
            {
                get { return _cancellationToken; }
            }

            public IByteBuffer Buffer
            {
                get { return _buffer; }
            }

            public Task Task
            {
                get { return _taskCompletionSource.Task; }
            }

            public TaskCompletionSource<bool> TaskCompletionSource
            {
                get { return _taskCompletionSource; }
            }
        }
    }
}
