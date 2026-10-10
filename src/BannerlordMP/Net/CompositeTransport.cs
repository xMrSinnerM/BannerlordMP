using System;
using System.Collections.Generic;
using BannerlordMP.Core.Protocol;

namespace BannerlordMP.Net
{
    /// <summary>
    /// A server listening on several transports at once (direct UDP and Steam relay). Peer ids are made unique
    /// by giving each inner transport its own id range.
    /// </summary>
    internal sealed class CompositeTransport : ITransport
    {
        private const int Stride = 1 << 20;
        private readonly List<ITransport> _inner = new List<ITransport>();

        public event Action<int> PeerConnected;
        public event Action<int, string> PeerDisconnected;
        public event Action<int, INetMessage> MessageReceived;

        public void Add(ITransport transport)
        {
            var offset = _inner.Count * Stride;
            _inner.Add(transport);
            transport.PeerConnected += id => PeerConnected?.Invoke(offset + id);
            transport.PeerDisconnected += (id, reason) => PeerDisconnected?.Invoke(offset + id, reason);
            transport.MessageReceived += (id, message) => MessageReceived?.Invoke(offset + id, message);
        }

        public int ConnectedPeers
        {
            get
            {
                var total = 0;
                foreach (var t in _inner)
                    total += t.ConnectedPeers;
                return total;
            }
        }

        public void Poll()
        {
            foreach (var t in _inner)
                t.Poll();
        }

        public bool Send(int peerId, INetMessage message, bool reliable = true)
        {
            var index = peerId / Stride;
            return index >= _inner.Count || _inner[index].Send(peerId % Stride, message, reliable);
        }

        public void SendToAll(INetMessage message, bool reliable = true, int exceptPeerId = -1)
        {
            for (var i = 0; i < _inner.Count; i++)
            {
                var except = exceptPeerId >= i * Stride && exceptPeerId < (i + 1) * Stride ? exceptPeerId % Stride : -1;
                _inner[i].SendToAll(message, reliable, except);
            }
        }

        public void Disconnect(int peerId)
        {
            var index = peerId / Stride;
            if (index < _inner.Count)
                _inner[index].Disconnect(peerId % Stride);
        }

        public void Dispose()
        {
            foreach (var t in _inner)
                t.Dispose();
            _inner.Clear();
        }
    }
}
