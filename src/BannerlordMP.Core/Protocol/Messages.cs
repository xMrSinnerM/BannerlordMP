using System.Collections.Generic;
using System.IO;
using BannerlordMP.Core.Time;

namespace BannerlordMP.Core.Protocol
{
    /// <summary>Client → host: first message after connecting.</summary>
    public sealed class HelloMessage : INetMessage
    {
        public MessageType Type => MessageType.Hello;
        public ushort ProtocolVersion = MessageCodec.ProtocolVersion;
        public string ModVersion = string.Empty;
        public string PlayerName = string.Empty;
        /// <summary>StringId of the hero this player wants to control.</summary>
        public string HeroId = string.Empty;
        /// <summary>Campaign.UniqueGameId of the loaded save, to make sure both sides loaded the same campaign.</summary>
        public string CampaignId = string.Empty;
        public double LocalHours;

        public void Write(BinaryWriter w)
        {
            w.Write(ProtocolVersion);
            w.WriteNullable(ModVersion);
            w.WriteNullable(PlayerName);
            w.WriteNullable(HeroId);
            w.WriteNullable(CampaignId);
            w.Write(LocalHours);
        }

        public void Read(BinaryReader r)
        {
            ProtocolVersion = r.ReadUInt16();
            ModVersion = r.ReadString();
            PlayerName = r.ReadString();
            HeroId = r.ReadString();
            CampaignId = r.ReadString();
            LocalHours = r.ReadDouble();
        }
    }

    /// <summary>Host → client: join accepted.</summary>
    public sealed class WelcomeMessage : INetMessage
    {
        public MessageType Type => MessageType.Welcome;
        public int PlayerId;
        public int HostPlayerId;
        public TimeArbitrationMode ArbitrationMode;
        public bool DetachDuringConversations;

        public void Write(BinaryWriter w)
        {
            w.Write(PlayerId);
            w.Write(HostPlayerId);
            w.Write((byte)ArbitrationMode);
            w.Write(DetachDuringConversations);
        }

        public void Read(BinaryReader r)
        {
            PlayerId = r.ReadInt32();
            HostPlayerId = r.ReadInt32();
            ArbitrationMode = (TimeArbitrationMode)r.ReadByte();
            DetachDuringConversations = r.ReadBoolean();
        }
    }

    /// <summary>Host → client: join refused. The connection is closed afterwards.</summary>
    public sealed class RejectMessage : INetMessage
    {
        public MessageType Type => MessageType.Reject;
        public string Reason = string.Empty;

        public void Write(BinaryWriter w) => w.WriteNullable(Reason);
        public void Read(BinaryReader r) => Reason = r.ReadString();
    }

    public sealed class PlayerInfo
    {
        public int Id;
        public string Name = string.Empty;
        public string HeroId = string.Empty;
        public string PartyId = string.Empty;
        public PlayerActivity Activity;
        public TimeSpeed RequestedSpeed;

        internal void Write(BinaryWriter w)
        {
            w.Write(Id);
            w.WriteNullable(Name);
            w.WriteNullable(HeroId);
            w.WriteNullable(PartyId);
            w.Write((byte)Activity);
            w.Write((byte)RequestedSpeed);
        }

        internal static PlayerInfo Read(BinaryReader r)
        {
            return new PlayerInfo
            {
                Id = r.ReadInt32(),
                Name = r.ReadString(),
                HeroId = r.ReadString(),
                PartyId = r.ReadString(),
                Activity = (PlayerActivity)r.ReadByte(),
                RequestedSpeed = (TimeSpeed)r.ReadByte(),
            };
        }
    }

    /// <summary>Host → clients: everyone in the session, including the host.</summary>
    public sealed class PlayerListMessage : INetMessage
    {
        public MessageType Type => MessageType.PlayerList;
        public List<PlayerInfo> Players = new List<PlayerInfo>();

        public void Write(BinaryWriter w) => w.WriteList(Players, (bw, p) => p.Write(bw));
        public void Read(BinaryReader r) => Players = r.ReadList(PlayerInfo.Read);
    }

    /// <summary>Client → host: the player pressed pause / play / fast-forward.</summary>
    public sealed class TimeRequestMessage : INetMessage
    {
        public MessageType Type => MessageType.TimeRequest;
        public TimeSpeed Speed;

        public void Write(BinaryWriter w) => w.Write((byte)Speed);
        public void Read(BinaryReader r) => Speed = (TimeSpeed)r.ReadByte();
    }

    /// <summary>Client → host: the player's activity changed (entered a battle, opened a menu...).</summary>
    public sealed class ActivityChangedMessage : INetMessage
    {
        public MessageType Type => MessageType.ActivityChanged;
        public PlayerActivity Activity;

        public void Write(BinaryWriter w) => w.Write((byte)Activity);
        public void Read(BinaryReader r) => Activity = (PlayerActivity)r.ReadByte();
    }

    /// <summary>Host → clients: the authoritative world clock.</summary>
    public sealed class TimeStateMessage : INetMessage
    {
        public MessageType Type => MessageType.TimeState;
        public TimeSpeed EffectiveSpeed;
        public double HostHours;
        /// <summary>True while the host is in a mission; the world is frozen until they return.</summary>
        public bool HostDetached;

        public void Write(BinaryWriter w)
        {
            w.Write((byte)EffectiveSpeed);
            w.Write(HostHours);
            w.Write(HostDetached);
        }

        public void Read(BinaryReader r)
        {
            EffectiveSpeed = (TimeSpeed)r.ReadByte();
            HostHours = r.ReadDouble();
            HostDetached = r.ReadBoolean();
        }
    }

    public struct PartyPosition
    {
        public string PartyId;
        public float X;
        public float Y;
        public bool IsOnLand;

        public PartyPosition(string partyId, float x, float y, bool isOnLand)
        {
            PartyId = partyId;
            X = x;
            Y = y;
            IsOnLand = isOnLand;
        }

        internal void Write(BinaryWriter w)
        {
            w.WriteNullable(PartyId);
            w.Write(X);
            w.Write(Y);
            w.Write(IsOnLand);
        }

        internal static PartyPosition Read(BinaryReader r) => new PartyPosition(r.ReadString(), r.ReadSingle(), r.ReadSingle(), r.ReadBoolean());
    }

    /// <summary>Host → clients: positions of parties on the map at a given host time.</summary>
    public sealed class WorldSnapshotMessage : INetMessage
    {
        public MessageType Type => MessageType.WorldSnapshot;
        public double HostHours;
        /// <summary>False for a delta snapshot that only lists parties that moved since the previous one.</summary>
        public bool IsFull;
        public List<PartyPosition> Parties = new List<PartyPosition>();

        public void Write(BinaryWriter w)
        {
            w.Write(HostHours);
            w.Write(IsFull);
            w.WriteList(Parties, (bw, p) => p.Write(bw));
        }

        public void Read(BinaryReader r)
        {
            HostHours = r.ReadDouble();
            IsFull = r.ReadBoolean();
            Parties = r.ReadList(PartyPosition.Read);
        }
    }

    /// <summary>Client → host: where the client's own party is. Each client is authoritative for its own movement.</summary>
    public sealed class PartyStateMessage : INetMessage
    {
        public MessageType Type => MessageType.PartyState;
        public float X;
        public float Y;
        public bool IsOnLand;

        public void Write(BinaryWriter w)
        {
            w.Write(X);
            w.Write(Y);
            w.Write(IsOnLand);
        }

        public void Read(BinaryReader r)
        {
            X = r.ReadSingle();
            Y = r.ReadSingle();
            IsOnLand = r.ReadBoolean();
        }
    }

    /// <summary>Client → host: the client started a battle; the host freezes the involved parties until the result arrives.</summary>
    public sealed class BattleStartedMessage : INetMessage
    {
        public MessageType Type => MessageType.BattleStarted;
        public List<string> PartyIds = new List<string>();

        public void Write(BinaryWriter w) => w.WriteList(PartyIds, (bw, s) => bw.WriteNullable(s));
        public void Read(BinaryReader r) => PartyIds = r.ReadList(br => br.ReadString());
    }

    public struct TroopCount
    {
        public string CharacterId;
        public int Count;
        public int Wounded;

        public TroopCount(string characterId, int count, int wounded)
        {
            CharacterId = characterId;
            Count = count;
            Wounded = wounded;
        }

        internal void Write(BinaryWriter w)
        {
            w.WriteNullable(CharacterId);
            w.Write(Count);
            w.Write(Wounded);
        }

        internal static TroopCount Read(BinaryReader r) => new TroopCount(r.ReadString(), r.ReadInt32(), r.ReadInt32());
    }

    public sealed class PartyOutcome
    {
        public string PartyId = string.Empty;
        public bool Destroyed;
        /// <summary>Gold of the party leader after the battle, or -1 if the party has no leader hero.</summary>
        public int LeaderGold = -1;
        public List<TroopCount> Members = new List<TroopCount>();
        public List<TroopCount> Prisoners = new List<TroopCount>();

        internal void Write(BinaryWriter w)
        {
            w.WriteNullable(PartyId);
            w.Write(Destroyed);
            w.Write(LeaderGold);
            w.WriteList(Members, (bw, t) => t.Write(bw));
            w.WriteList(Prisoners, (bw, t) => t.Write(bw));
        }

        internal static PartyOutcome Read(BinaryReader r)
        {
            return new PartyOutcome
            {
                PartyId = r.ReadString(),
                Destroyed = r.ReadBoolean(),
                LeaderGold = r.ReadInt32(),
                Members = r.ReadList(TroopCount.Read),
                Prisoners = r.ReadList(TroopCount.Read),
            };
        }
    }

    /// <summary>Client → host: the outcome of a battle the client fought. The host applies it to the real world.</summary>
    public sealed class BattleResultMessage : INetMessage
    {
        public MessageType Type => MessageType.BattleResult;
        /// <summary>Raw TaleWorlds BattleSideEnum value of the winning side (-1 for none).</summary>
        public int WinningSide = -1;
        public List<PartyOutcome> Parties = new List<PartyOutcome>();

        public void Write(BinaryWriter w)
        {
            w.Write(WinningSide);
            w.WriteList(Parties, (bw, p) => p.Write(bw));
        }

        public void Read(BinaryReader r)
        {
            WinningSide = r.ReadInt32();
            Parties = r.ReadList(PartyOutcome.Read);
        }
    }

    /// <summary>Host → clients: a party no longer exists in the world.</summary>
    public sealed class PartyDestroyedMessage : INetMessage
    {
        public MessageType Type => MessageType.PartyDestroyed;
        public double HostHours;
        public string PartyId = string.Empty;

        public void Write(BinaryWriter w)
        {
            w.Write(HostHours);
            w.WriteNullable(PartyId);
        }

        public void Read(BinaryReader r)
        {
            HostHours = r.ReadDouble();
            PartyId = r.ReadString();
        }
    }

    /// <summary>Both directions. The host fills in <see cref="PlayerId"/> when relaying.</summary>
    public sealed class ChatMessage : INetMessage
    {
        public MessageType Type => MessageType.Chat;
        public int PlayerId;
        public string Text = string.Empty;

        public void Write(BinaryWriter w)
        {
            w.Write(PlayerId);
            w.WriteNullable(Text);
        }

        public void Read(BinaryReader r)
        {
            PlayerId = r.ReadInt32();
            Text = r.ReadString();
        }
    }
}
