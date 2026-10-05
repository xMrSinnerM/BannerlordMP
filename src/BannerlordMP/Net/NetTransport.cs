using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using BannerlordMP.Core.Protocol;
using LiteNetLib;

namespace BannerlordMP.Net
{
    /// <summary>
    /// Thin LiteNetLib wrapper. All callbacks are raised from <see cref="Poll"/>, which is called from the
    /// game's main thread, so handlers can touch game state directly.
    /// </summary>
    internal sealed class NetTransport : IDisposable
    {
        private const string ConnectionKey = "BannerlordMP/" + "1";
        private const int MaxPlayers = 8;

        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private readonly NetManager _manager;
        private readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();

        public event Action<int> PeerConnected;
        public event Action<int, string> PeerDisconnected;
        public event Action<int, INetMessage> MessageReceived;

        public NetTransport()
        {
            _manager = new NetManager(_listener)
            {
                AutoRecycle = true,
                DisconnectTimeout = 15000,
                UnconnectedMessagesEnabled = false,
            };

            _listener.ConnectionRequestEvent += request =>
            {
                if (IsServer && _manager.ConnectedPeersCount < MaxPlayers)
                    request.AcceptIfKey(ConnectionKey);
                else
                    request.Reject();
            };
            _listener.PeerConnectedEvent += peer =>
            {
                _peers[peer.Id] = peer;
                PeerConnected?.Invoke(peer.Id);
            };
            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                _peers.Remove(peer.Id);
                PeerDisconnected?.Invoke(peer.Id, info.Reason.ToString());
            };
            _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                INetMessage message;
                try
                {
                    var data = reader.GetRemainingBytes();
                    message = MessageCodec.Decode(data);
                }
                catch (Exception e)
                {
                    Log.Error($"Dropping malformed packet from peer {peer.Id}", e);
                    return;
                }
                MessageReceived?.Invoke(peer.Id, message);
            };
            _listener.NetworkErrorEvent += (endPoint, error) => Log.Error($"Network error {error} ({endPoint})");
        }

        public bool IsServer { get; private set; }
        public bool IsRunning => _manager.IsRunning;
        public int ConnectedPeers => _manager.ConnectedPeersCount;

        public void StartServer(int port)
        {
            IsServer = true;
            if (!_manager.Start(port))
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
        }

        public void Connect(string address, int port)
        {
            IsServer = false;
            if (!_manager.Start())
                throw new SocketException((int)SocketError.SocketError);
            _manager.Connect(address, port, ConnectionKey);
        }

        public void Poll() => _manager.PollEvents();

        public void Send(int peerId, INetMessage message, bool reliable = true)
        {
            if (_peers.TryGetValue(peerId, out var peer))
                peer.Send(MessageCodec.Encode(message), reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced);
        }

        /// <summary>Client: send to the host. Host: send to every client.</summary>
        public void SendToAll(INetMessage message, bool reliable = true, int exceptPeerId = -1)
        {
            if (_peers.Count == 0)
                return;
            var data = MessageCodec.Encode(message);
            var method = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced;
            foreach (var pair in _peers)
            {
                if (pair.Key != exceptPeerId)
                    pair.Value.Send(data, method);
            }
        }

        public void Disconnect(int peerId)
        {
            if (_peers.TryGetValue(peerId, out var peer))
                _manager.DisconnectPeer(peer);
        }

        public void Dispose()
        {
            _manager.Stop(true);
            _peers.Clear();
        }
    }
}
