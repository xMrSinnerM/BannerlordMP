using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BannerlordMP.Core.Protocol;
using Steamworks;

namespace BannerlordMP.Net
{
    /// <summary>
    /// Steam Networking Sockets over Valve's relay (SDR). Nobody needs to open ports, the connection is encrypted,
    /// and players' IP addresses stay hidden. Uses the Steamworks.NET the game ships and the game's own Steam login.
    /// </summary>
    internal sealed class SteamTransport : ITransport
    {
        private const int MaxMessagesPerPoll = 64;

        private readonly Dictionary<int, HSteamNetConnection> _connections = new Dictionary<int, HSteamNetConnection>();
        private readonly Dictionary<uint, int> _peerIds = new Dictionary<uint, int>();
        private readonly IntPtr[] _messages = new IntPtr[MaxMessagesPerPoll];
        private readonly Callback<SteamNetConnectionStatusChangedCallback_t> _statusCallback;
        private readonly Queue<Action> _pending = new Queue<Action>();
        private HSteamListenSocket _listenSocket = HSteamListenSocket.Invalid;
        private int _nextPeerId;

        public event Action<int> PeerConnected;
        public event Action<int, string> PeerDisconnected;
        public event Action<int, INetMessage> MessageReceived;

        private SteamTransport()
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
        }

        public bool IsServer { get; private set; }
        public int ConnectedPeers => _connections.Count;

        public static SteamTransport Listen()
        {
            var transport = new SteamTransport { IsServer = true };
            transport._listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
            if (transport._listenSocket == HSteamListenSocket.Invalid)
                throw new InvalidOperationException("Steam refused to open a relay listen socket.");
            return transport;
        }

        public static SteamTransport Connect(ulong hostSteamId)
        {
            var transport = new SteamTransport();
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(hostSteamId));
            var connection = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
            if (connection == HSteamNetConnection.Invalid)
                throw new InvalidOperationException("Steam could not start a relay connection.");
            transport.Track(connection);
            return transport;
        }

        public void Poll()
        {
            // Connection callbacks arrive while the game pumps Steam; handle them here, on our schedule.
            while (_pending.Count > 0)
                _pending.Dequeue()();

            foreach (var pair in new List<KeyValuePair<int, HSteamNetConnection>>(_connections))
            {
                int count;
                while ((count = SteamNetworkingSockets.ReceiveMessagesOnConnection(pair.Value, _messages, MaxMessagesPerPoll)) > 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var raw = SteamNetworkingMessage_t.FromIntPtr(_messages[i]);
                        var data = new byte[raw.m_cbSize];
                        Marshal.Copy(raw.m_pData, data, 0, raw.m_cbSize);
                        SteamNetworkingMessage_t.Release(_messages[i]);

                        INetMessage message;
                        try
                        {
                            message = MessageCodec.Decode(data);
                        }
                        catch (Exception e)
                        {
                            Log.Error($"Dropping malformed Steam packet from peer {pair.Key}", e);
                            continue;
                        }
                        MessageReceived?.Invoke(pair.Key, message);
                    }
                }
            }
        }

        public bool Send(int peerId, INetMessage message, bool reliable = true)
        {
            return !_connections.TryGetValue(peerId, out var connection) || SendRaw(connection, MessageCodec.Encode(message), reliable);
        }

        public void SendToAll(INetMessage message, bool reliable = true, int exceptPeerId = -1)
        {
            if (_connections.Count == 0)
                return;
            var data = MessageCodec.Encode(message);
            foreach (var pair in _connections)
            {
                if (pair.Key != exceptPeerId)
                    SendRaw(pair.Value, data, reliable);
            }
        }

        public void Disconnect(int peerId)
        {
            if (!_connections.TryGetValue(peerId, out var connection))
                return;
            Forget(connection);
            SteamNetworkingSockets.CloseConnection(connection, 0, "closed", true);
            PeerDisconnected?.Invoke(peerId, "closed");
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                SteamNetworkingSockets.CloseConnection(connection, 0, "shutdown", true);
            _connections.Clear();
            _peerIds.Clear();
            if (_listenSocket != HSteamListenSocket.Invalid)
                SteamNetworkingSockets.CloseListenSocket(_listenSocket);
            _listenSocket = HSteamListenSocket.Invalid;
            _statusCallback.Dispose();
        }

        private static bool SendRaw(HSteamNetConnection connection, byte[] data, bool reliable)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var flags = reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_Unreliable;
                var result = SteamNetworkingSockets.SendMessageToConnection(connection, handle.AddrOfPinnedObject(), (uint)data.Length, flags, out _);
                return result != EResult.k_EResultLimitExceeded;
            }
            finally
            {
                handle.Free();
            }
        }

        private int Track(HSteamNetConnection connection)
        {
            if (_peerIds.TryGetValue(connection.m_HSteamNetConnection, out var existing))
                return existing;
            var id = _nextPeerId++;
            _peerIds[connection.m_HSteamNetConnection] = id;
            return id;
        }

        private void Forget(HSteamNetConnection connection)
        {
            if (_peerIds.TryGetValue(connection.m_HSteamNetConnection, out var id))
            {
                _peerIds.Remove(connection.m_HSteamNetConnection);
                _connections.Remove(id);
            }
        }

        private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t status)
        {
            _pending.Enqueue(() => HandleStatus(status));
        }

        private void HandleStatus(SteamNetConnectionStatusChangedCallback_t status)
        {
            var connection = status.m_hConn;
            switch (status.m_info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    // Incoming connection on our listen socket.
                    if (IsServer && status.m_info.m_hListenSocket == _listenSocket)
                    {
                        if (SteamNetworkingSockets.AcceptConnection(connection) != EResult.k_EResultOK)
                            SteamNetworkingSockets.CloseConnection(connection, 0, "accept failed", false);
                        else
                            Track(connection);
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (!_peerIds.ContainsKey(connection.m_HSteamNetConnection))
                        break;
                    var id = _peerIds[connection.m_HSteamNetConnection];
                    _connections[id] = connection;
                    PeerConnected?.Invoke(id);
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    if (_peerIds.TryGetValue(connection.m_HSteamNetConnection, out var closedId))
                    {
                        var wasConnected = _connections.ContainsKey(closedId);
                        Forget(connection);
                        PeerDisconnected?.Invoke(closedId, status.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                            ? "closed by peer"
                            : wasConnected ? "connection lost" : "could not reach the host over Steam");
                    }
                    SteamNetworkingSockets.CloseConnection(connection, 0, null, false);
                    break;
            }
        }
    }
}
