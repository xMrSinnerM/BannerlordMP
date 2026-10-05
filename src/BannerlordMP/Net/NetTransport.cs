using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using BannerlordMP.Core.Protocol;
using LiteNetLib;

namespace BannerlordMP.Net
{
    /// <summary>
    /// UDP transport (LiteNetLib) for direct IP and LAN play. A server also answers LAN discovery broadcasts
    /// with a <see cref="ServerInfoMessage"/>.
    /// </summary>
    internal sealed class NetTransport : ITransport
    {
        public const string DiscoveryMagic = "BMP?";
        private const string ConnectionKey = "BannerlordMP/2";
        private const int MaxPeers = 64;

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
                DisconnectTimeout = 20000,
            };

            _listener.ConnectionRequestEvent += request =>
            {
                if (IsServer && _manager.ConnectedPeersCount < MaxPeers)
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
                    message = MessageCodec.Decode(reader.GetRemainingBytes());
                }
                catch (Exception e)
                {
                    Log.Error($"Dropping malformed packet from peer {peer.Id}", e);
                    return;
                }
                MessageReceived?.Invoke(peer.Id, message);
            };
            _listener.NetworkReceiveUnconnectedEvent += (endPoint, reader, type) =>
            {
                if (!IsServer || ServerInfoProvider == null || type != UnconnectedMessageType.Broadcast)
                    return;
                if (reader.GetString() != DiscoveryMagic)
                    return;
                _manager.SendUnconnectedMessage(MessageCodec.Encode(ServerInfoProvider()), endPoint);
            };
            _listener.NetworkErrorEvent += (endPoint, error) => Log.Error($"Network error {error} ({endPoint})");
        }

        public bool IsServer { get; private set; }
        public int ConnectedPeers => _manager.ConnectedPeersCount;

        /// <summary>Server only: what to answer LAN discovery with.</summary>
        public Func<ServerInfoMessage> ServerInfoProvider { get; set; }

        public void StartServer(int port)
        {
            IsServer = true;
            _manager.UnconnectedMessagesEnabled = true;
            _manager.BroadcastReceiveEnabled = true;
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

        public bool Send(int peerId, INetMessage message, bool reliable = true)
        {
            if (!_peers.TryGetValue(peerId, out var peer))
                return true; // Gone; nothing to retry.
            peer.Send(MessageCodec.Encode(message), reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced);
            return true;
        }

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
