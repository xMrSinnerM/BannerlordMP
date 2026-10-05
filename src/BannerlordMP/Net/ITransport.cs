using System;
using BannerlordMP.Core.Protocol;

namespace BannerlordMP.Net
{
    /// <summary>
    /// A connection-oriented message transport. Events are raised from <see cref="Poll"/> on the game's main thread.
    /// On a client there is a single peer: the host.
    /// </summary>
    internal interface ITransport : IDisposable
    {
        event Action<int> PeerConnected;
        event Action<int, string> PeerDisconnected;
        event Action<int, INetMessage> MessageReceived;

        int ConnectedPeers { get; }

        void Poll();

        /// <returns>False if the transport is congested and the message was not queued; try again next frame.</returns>
        bool Send(int peerId, INetMessage message, bool reliable = true);

        void SendToAll(INetMessage message, bool reliable = true, int exceptPeerId = -1);

        void Disconnect(int peerId);
    }

    /// <summary>Where to connect: an IP/host name and port, or a Steam user (through Valve's relay).</summary>
    internal sealed class ConnectTarget
    {
        public string Address;
        public int Port;
        public ulong SteamId;

        public bool IsSteam => SteamId != 0;

        public static ConnectTarget Ip(string address, int port) => new ConnectTarget { Address = address, Port = port };

        public static ConnectTarget Steam(ulong steamId) => new ConnectTarget { SteamId = steamId };

        public ITransport CreateClientTransport()
        {
            if (IsSteam)
                return SteamTransport.Connect(SteamId);
            var transport = new NetTransport();
            transport.Connect(Address, Port);
            return transport;
        }

        public override string ToString() => IsSteam ? "Steam user " + SteamId : $"{Address}:{Port}";
    }
}
