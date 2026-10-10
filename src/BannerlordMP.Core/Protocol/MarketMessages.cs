using System.Collections.Generic;
using System.IO;

namespace BannerlordMP.Core.Protocol
{
    /// <summary>One item category's market in a town: what sets its prices.</summary>
    public struct CategoryMarket
    {
        public string CategoryId;
        public float Supply;
        public float Demand;

        public CategoryMarket(string categoryId, float supply, float demand)
        {
            CategoryId = categoryId;
            Supply = supply;
            Demand = demand;
        }

        internal void Write(BinaryWriter w)
        {
            w.WriteNullable(CategoryId);
            w.Write(Supply);
            w.Write(Demand);
        }

        internal static CategoryMarket Read(BinaryReader r) => new CategoryMarket(r.ReadString(), r.ReadSingle(), r.ReadSingle());
    }

    /// <summary>Client → host: the player entered this settlement; send its real market.</summary>
    public sealed class MarketRequestMessage : INetMessage
    {
        public MessageType Type => MessageType.MarketRequest;
        public string SettlementId = string.Empty;

        public void Write(BinaryWriter w) => w.WriteNullable(SettlementId);
        public void Read(BinaryReader r) => SettlementId = r.ReadString();
    }

    /// <summary>Host → client: a settlement's market as it is in the real world.</summary>
    public sealed class MarketStateMessage : INetMessage
    {
        public MessageType Type => MessageType.MarketState;
        public string SettlementId = string.Empty;
        public int Gold;
        /// <summary>Negative when the settlement has no prosperity (villages).</summary>
        public float Prosperity = -1f;
        /// <summary>Stock: "itemId|modifierId" to amount.</summary>
        public Dictionary<string, int> Items = new Dictionary<string, int>();
        /// <summary>Towns only.</summary>
        public List<CategoryMarket> Categories = new List<CategoryMarket>();

        public void Write(BinaryWriter w)
        {
            w.WriteNullable(SettlementId);
            w.Write(Gold);
            w.Write(Prosperity);
            w.WriteCounters(Items);
            w.WriteList(Categories, (bw, c) => c.Write(bw));
        }

        public void Read(BinaryReader r)
        {
            SettlementId = r.ReadString();
            Gold = r.ReadInt32();
            Prosperity = r.ReadSingle();
            Items = r.ReadCounters();
            Categories = r.ReadList(CategoryMarket.Read);
        }
    }

    /// <summary>
    /// Client → host: what the player's trading changed in a settlement's market since they got its state
    /// (sent when they leave). Changes, not a full state, so two players trading in one town both count.
    /// </summary>
    public sealed class MarketChangeMessage : INetMessage
    {
        public MessageType Type => MessageType.MarketChange;
        public string SettlementId = string.Empty;
        public int GoldChange;
        public Dictionary<string, int> Items = new Dictionary<string, int>();

        public void Write(BinaryWriter w)
        {
            w.WriteNullable(SettlementId);
            w.Write(GoldChange);
            w.WriteCounters(Items);
        }

        public void Read(BinaryReader r)
        {
            SettlementId = r.ReadString();
            GoldChange = r.ReadInt32();
            Items = r.ReadCounters();
        }
    }
}
