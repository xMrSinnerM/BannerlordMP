using System.IO;

namespace BannerlordMP.Core.Protocol
{
    public interface INetMessage
    {
        MessageType Type { get; }
        void Write(BinaryWriter writer);
        void Read(BinaryReader reader);
    }
}
