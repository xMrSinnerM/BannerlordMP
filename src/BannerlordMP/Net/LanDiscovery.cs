using System;
using System.Collections.Generic;
using System.Net;
using BannerlordMP.Core.Protocol;
using LiteNetLib;
using LiteNetLib.Utils;

namespace BannerlordMP.Net
{
    internal sealed class LanServer
    {
        public ServerInfoMessage Info;
        public string Address;
    }

    /// <summary>Finds BannerlordMP servers on the local network by UDP broadcast.</summary>
    internal sealed class LanDiscovery : IDisposable
    {
        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private readonly NetManager _manager;
        private readonly Dictionary<string, LanServer> _found = new Dictionary<string, LanServer>();

        public LanDiscovery()
        {
            _manager = new NetManager(_listener) { UnconnectedMessagesEnabled = true };
            _listener.NetworkReceiveUnconnectedEvent += OnReply;
        }

        public IReadOnlyCollection<LanServer> Found => _found.Values;

        public void Start(int port)
        {
            if (!_manager.IsRunning && !_manager.Start())
                return;
            var writer = new NetDataWriter();
            writer.Put(NetTransport.DiscoveryMagic);
            _manager.SendBroadcast(writer, port);
        }

        public void Poll()
        {
            if (_manager.IsRunning)
                _manager.PollEvents();
        }

        public void Dispose() => _manager.Stop();

        private void OnReply(IPEndPoint endPoint, NetPacketReader reader, UnconnectedMessageType type)
        {
            if (type != UnconnectedMessageType.BasicMessage)
                return;
            try
            {
                if (MessageCodec.Decode(reader.GetRemainingBytes()) is ServerInfoMessage info)
                    _found[endPoint.ToString()] = new LanServer { Info = info, Address = endPoint.Address.ToString() };
            }
            catch (Exception e)
            {
                Log.Error("Bad discovery reply from " + endPoint, e);
            }
        }
    }
}
