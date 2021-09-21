using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using CometD.NetCore.Bayeux;
using CometD.NetCore.Bayeux.Client;

namespace CometD.NetCore.Common
{
    /// <summary> <p>Partial implementation of {@link ClientSession}.</p>
    /// <p>It handles extensions and batching, and provides utility methods to be used by subclasses.</p>
    /// </summary>
    public abstract class AbstractClientSession : IClientSession
    {
        private readonly ConcurrentDictionary<string, object> _attributes = new ConcurrentDictionary<string, object>();
        private readonly ConcurrentBag<IExtension> _extensions = new ();
        private int _batch;
        private int _idGen;

        public void AddExtension(IExtension extension)
        {
            _extensions.Add(extension);
        }

        public abstract void Handshake();

        public abstract void Handshake(IDictionary<string, object> fields);

        public IClientSessionChannel GetChannel(string channelId)
        {
            // default
            return GetChannel(channelId, -1);
        }

        public IClientSessionChannel GetChannel(string channelId, long replayId)
        {
            Channels.TryGetValue(channelId, out var channel);

            if (channel == null)
            {
                var id = NewChannelId(channelId);
                var new_channel = NewChannel(id, replayId);

                if (Channels.ContainsKey(channelId))
                {
                    channel = Channels[channelId];
                }
                else
                {
                    Channels[channelId] = new_channel;
                }

                if (channel == null)
                {
                    channel = new_channel;
                }
            }

            return channel;
        }

        public ICollection<string> AttributeNames => _attributes.Keys;

        public abstract bool Connected { get; }

        public abstract bool Handshook { get; }

        public abstract string Id { get; }

        public void Batch(Action batch)
        {
            StartBatch();
            try
            {
                batch();
            }
            finally
            {
                EndBatch();
            }
        }

        public abstract void Disconnect();

        public bool EndBatch()
        {
            if (Interlocked.Decrement(ref _batch) == 0)
            {
                SendBatch();
                return true;
            }

            return false;
        }

        public object GetAttribute(string name)
        {
            _attributes.TryGetValue(name, out var obj);
            return obj;
        }

        public object RemoveAttribute(string name)
        {
            try
            {
                var old = _attributes[name];

                if (_attributes.TryRemove(name, out var va))
                {
                    return va;
                }

                return old;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void SetAttribute(string name, object val)
        {
            _attributes[name] = val;
        }

        public void StartBatch()
        {
            Interlocked.Increment(ref _batch);
        }

        protected bool Batching => _batch > 0;

        protected Dictionary<string, AbstractSessionChannel> Channels { get; } = new Dictionary<string, AbstractSessionChannel>();

        /// <summary> <p>Receives a message (from the server) and process it.</p>
        /// <p>Processing the message involves calling the receive {@link ClientSession.Extension extensions}
        /// and the channel {@link ClientSessionChannel.ClientSessionChannelListener listeners}.</p>
        /// </summary>
        /// <param name="message">the message received.
        /// </param>
        public void Receive(IMutableMessage message)
        {
            var id = message.Channel;
            if (id == null)
            {
                throw new ArgumentException("Bayeux messages must have a channel, " + message);
            }

            if (!ExtendReceive(message))
            {
                return;
            }

            var channel = (AbstractSessionChannel)GetChannel(id);
            var channelId = channel.ChannelId;

            channel.NotifyMessageListeners(message);

            foreach (var channelPattern in channelId.Wilds)
            {
                var channelIdPattern = NewChannelId(channelPattern);
                if (channelIdPattern.Matches(channelId))
                {
                    var wildChannel = (AbstractSessionChannel)GetChannel(channelPattern);
                    wildChannel.NotifyMessageListeners(message);
                }
            }
        }

        public void ResetSubscriptions()
        {
            foreach (var channel in Channels)
            {
                channel.Value.ResetSubscriptions();
            }
        }

        protected bool ExtendReceive(IMutableMessage message)
        {
            if (message.Meta)
            {
                foreach (var extension in _extensions)
                {
                    if (!extension.ReceiveMeta(this, message))
                    {
                        return false;
                    }
                }
            }
            else
            {
                foreach (var extension in _extensions)
                {
                    if (!extension.Receive(this, message))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        protected bool ExtendSend(IMutableMessage message)
        {
            if (message.Meta)
            {
                foreach (var extension in _extensions)
                {
                    if (!extension.SendMeta(this, message))
                    {
                        return false;
                    }
                }
            }
            else
            {
                foreach (var extension in _extensions)
                {
                    if (!extension.Send(this, message))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        protected abstract AbstractSessionChannel NewChannel(ChannelId channelId, long replayId);

        protected abstract ChannelId NewChannelId(string channelId);

        protected string NewMessageId()
        {
            return Convert.ToString(Interlocked.Increment(ref _idGen));
        }

        protected abstract void SendBatch();
    }
}
