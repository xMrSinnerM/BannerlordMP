using System.Collections.Generic;
using System.IO;

namespace BannerlordMP.Core.Protocol
{
    /// <summary>Host → clients: a party now exists in the world. Clients create a matching party to mirror it.</summary>
    public sealed class PartySpawnedMessage : INetMessage
    {
        public MessageType Type => MessageType.PartySpawned;
        public double HostHours;
        public string PartyId = string.Empty;
        public string Name = string.Empty;
        public string ClanId = string.Empty;
        public string LeaderHeroId = string.Empty;
        public string HomeSettlementId = string.Empty;
        public bool IsLordParty;
        public float X;
        public float Y;
        public bool IsOnLand = true;
        public List<TroopCount> Members = new List<TroopCount>();
        public List<TroopCount> Prisoners = new List<TroopCount>();

        public void Write(BinaryWriter w)
        {
            w.Write(HostHours);
            w.WriteNullable(PartyId);
            w.WriteNullable(Name);
            w.WriteNullable(ClanId);
            w.WriteNullable(LeaderHeroId);
            w.WriteNullable(HomeSettlementId);
            w.Write(IsLordParty);
            w.Write(X);
            w.Write(Y);
            w.Write(IsOnLand);
            w.WriteList(Members, (bw, t) => t.Write(bw));
            w.WriteList(Prisoners, (bw, t) => t.Write(bw));
        }

        public void Read(BinaryReader r)
        {
            HostHours = r.ReadDouble();
            PartyId = r.ReadString();
            Name = r.ReadString();
            ClanId = r.ReadString();
            LeaderHeroId = r.ReadString();
            HomeSettlementId = r.ReadString();
            IsLordParty = r.ReadBoolean();
            X = r.ReadSingle();
            Y = r.ReadSingle();
            IsOnLand = r.ReadBoolean();
            Members = r.ReadList(TroopCount.Read);
            Prisoners = r.ReadList(TroopCount.Read);
        }
    }

    /// <summary>Host → a client: current troops of a party near that client's hero, so encounters see the real army.</summary>
    public sealed class PartyRosterMessage : INetMessage
    {
        public MessageType Type => MessageType.PartyRoster;
        public string PartyId = string.Empty;
        public List<TroopCount> Members = new List<TroopCount>();
        public List<TroopCount> Prisoners = new List<TroopCount>();

        public void Write(BinaryWriter w)
        {
            w.WriteNullable(PartyId);
            w.WriteList(Members, (bw, t) => t.Write(bw));
            w.WriteList(Prisoners, (bw, t) => t.Write(bw));
        }

        public void Read(BinaryReader r)
        {
            PartyId = r.ReadString();
            Members = r.ReadList(TroopCount.Read);
            Prisoners = r.ReadList(TroopCount.Read);
        }
    }

    public enum WorldEventKind : byte
    {
        /// <summary>A = settlement, B = new owner hero.</summary>
        SettlementOwner = 1,
        /// <summary>A, B = factions (clan or kingdom ids).</summary>
        War = 2,
        /// <summary>A, B = factions.</summary>
        Peace = 3,
        /// <summary>A = clan, B = new kingdom (empty: left its kingdom).</summary>
        ClanKingdom = 4,
        /// <summary>A = victim hero, B = killer hero (may be empty).</summary>
        HeroKilled = 5,
    }

    /// <summary>
    /// A change to the world's politics or ownership. Host → clients to mirror it; client → host when the
    /// player caused it (taking a castle, killing a lord), after which the host applies and relays it.
    /// </summary>
    public sealed class WorldEventMessage : INetMessage
    {
        public MessageType Type => MessageType.WorldEvent;
        public double HostHours;
        public WorldEventKind Kind;
        public string A = string.Empty;
        public string B = string.Empty;

        public void Write(BinaryWriter w)
        {
            w.Write(HostHours);
            w.Write((byte)Kind);
            w.WriteNullable(A);
            w.WriteNullable(B);
        }

        public void Read(BinaryReader r)
        {
            HostHours = r.ReadDouble();
            Kind = (WorldEventKind)r.ReadByte();
            A = r.ReadString();
            B = r.ReadString();
        }

        public override string ToString() => $"{Kind}({A}, {B})";
    }

    /// <summary>Host → a client: an AI party caught that client's hero on the host's map; start the encounter locally.</summary>
    public sealed class EncounterRequestMessage : INetMessage
    {
        public MessageType Type => MessageType.EncounterRequest;
        public string AttackerPartyId = string.Empty;

        public void Write(BinaryWriter w) => w.WriteNullable(AttackerPartyId);
        public void Read(BinaryReader r) => AttackerPartyId = r.ReadString();
    }

    /// <summary>Client → host: changes the player made to their own party and hero since the last delta.</summary>
    public sealed class LedgerDeltaMessage : INetMessage
    {
        public MessageType Type => MessageType.LedgerDelta;
        public int Seq;
        public Dictionary<string, int> Delta = new Dictionary<string, int>();

        public void Write(BinaryWriter w)
        {
            w.Write(Seq);
            w.WriteCounters(Delta);
        }

        public void Read(BinaryReader r)
        {
            Seq = r.ReadInt32();
            Delta = r.ReadCounters();
        }
    }

    /// <summary>Host → client: the authoritative state of the player's party and hero, including deltas up to <see cref="AckSeq"/>.</summary>
    public sealed class LedgerStateMessage : INetMessage
    {
        public MessageType Type => MessageType.LedgerState;
        public int AckSeq;
        public Dictionary<string, int> State = new Dictionary<string, int>();

        public void Write(BinaryWriter w)
        {
            w.Write(AckSeq);
            w.WriteCounters(State);
        }

        public void Read(BinaryReader r)
        {
            AckSeq = r.ReadInt32();
            State = r.ReadCounters();
        }
    }

    /// <summary>Client → host: "I don't know these parties"; the host answers with <see cref="PartySpawnedMessage"/>s.</summary>
    public sealed class PartyInfoRequestMessage : INetMessage
    {
        public MessageType Type => MessageType.PartyInfoRequest;
        public List<string> PartyIds = new List<string>();

        public void Write(BinaryWriter w) => w.WriteList(PartyIds, (bw, s) => bw.WriteNullable(s));
        public void Read(BinaryReader r) => PartyIds = r.ReadList(br => br.ReadString());
    }
}
