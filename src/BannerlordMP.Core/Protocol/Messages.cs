using System.Collections.Generic;
using System.IO;
using BannerlordMP.Core.Time;

namespace BannerlordMP.Core.Protocol
{
    /// <summary>
    /// Client → host, in answer to <see cref="AuthChallengeMessage"/>. Without a resume token the client is
    /// browsing slots from the main menu; with one it has loaded the world and is entering the game.
    /// </summary>
    public sealed class HelloMessage : INetMessage
    {
        public MessageType Type => MessageType.Hello;
        public ushort ProtocolVersion = MessageCodec.ProtocolVersion;
        public string ModVersion = string.Empty;
        public string PlayerName = string.Empty;
        /// <summary>HMAC of the challenge nonce with the server password key; empty if the server has no password.</summary>
        public byte[] ServerProof = new byte[0];
        public string ResumeToken = string.Empty;
        /// <summary>Campaign.UniqueGameId of the loaded world (resume only).</summary>
        public string CampaignId = string.Empty;
        public double LocalHours;

        public void Write(BinaryWriter w)
        {
            w.Write(ProtocolVersion);
            w.WriteNullable(ModVersion);
            w.WriteNullable(PlayerName);
            w.WriteByteArray(ServerProof);
            w.WriteNullable(ResumeToken);
            w.WriteNullable(CampaignId);
            w.Write(LocalHours);
        }

        public void Read(BinaryReader r)
        {
            ProtocolVersion = r.ReadUInt16();
            ModVersion = r.ReadString();
            PlayerName = r.ReadString();
            ServerProof = r.ReadByteArray(64);
            ResumeToken = r.ReadString();
            CampaignId = r.ReadString();
            LocalHours = r.ReadDouble();
        }
    }

    /// <summary>Host → client, right after connecting.</summary>
    public sealed class AuthChallengeMessage : INetMessage
    {
        public MessageType Type => MessageType.AuthChallenge;
        public ushort ProtocolVersion = MessageCodec.ProtocolVersion;
        public string ServerName = string.Empty;
        public bool PasswordRequired;
        public byte[] ServerSalt = new byte[0];
        /// <summary>Single-use random value; every proof in this connection is computed over it.</summary>
        public byte[] Nonce = new byte[0];

        public void Write(BinaryWriter w)
        {
            w.Write(ProtocolVersion);
            w.WriteNullable(ServerName);
            w.Write(PasswordRequired);
            w.WriteByteArray(ServerSalt);
            w.WriteByteArray(Nonce);
        }

        public void Read(BinaryReader r)
        {
            ProtocolVersion = r.ReadUInt16();
            ServerName = r.ReadString();
            PasswordRequired = r.ReadBoolean();
            ServerSalt = r.ReadByteArray(64);
            Nonce = r.ReadByteArray(64);
        }
    }

    public sealed class SlotInfo
    {
        public int SlotId;
        public string HeroName = string.Empty;
        public string CultureName = string.Empty;
        /// <summary>True if the owner is playing right now.</summary>
        public bool InUse;
        public byte[] Salt = new byte[0];

        internal void Write(BinaryWriter w)
        {
            w.Write(SlotId);
            w.WriteNullable(HeroName);
            w.WriteNullable(CultureName);
            w.Write(InUse);
            w.WriteByteArray(Salt);
        }

        internal static SlotInfo Read(BinaryReader r)
        {
            return new SlotInfo
            {
                SlotId = r.ReadInt32(),
                HeroName = r.ReadString(),
                CultureName = r.ReadString(),
                InUse = r.ReadBoolean(),
                Salt = r.ReadByteArray(64),
            };
        }
    }

    public struct CultureChoice
    {
        public string Id;
        public string Name;

        public CultureChoice(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }

    /// <summary>Host → client: the player heroes on this server, and what a new hero can be.</summary>
    public sealed class SlotListMessage : INetMessage
    {
        public MessageType Type => MessageType.SlotList;
        public int MaxSlots;
        public List<SlotInfo> Slots = new List<SlotInfo>();
        public List<CultureChoice> Cultures = new List<CultureChoice>();

        public void Write(BinaryWriter w)
        {
            w.Write(MaxSlots);
            w.WriteList(Slots, (bw, s) => s.Write(bw));
            w.WriteList(Cultures, (bw, c) =>
            {
                bw.WriteNullable(c.Id);
                bw.WriteNullable(c.Name);
            });
        }

        public void Read(BinaryReader r)
        {
            MaxSlots = r.ReadInt32();
            Slots = r.ReadList(SlotInfo.Read);
            Cultures = r.ReadList(br => new CultureChoice(br.ReadString(), br.ReadString()));
        }
    }

    /// <summary>Client → host: play an existing hero, proving its password.</summary>
    public sealed class ClaimSlotMessage : INetMessage
    {
        public MessageType Type => MessageType.ClaimSlot;
        public int SlotId;
        public byte[] Proof = new byte[0];

        public void Write(BinaryWriter w)
        {
            w.Write(SlotId);
            w.WriteByteArray(Proof);
        }

        public void Read(BinaryReader r)
        {
            SlotId = r.ReadInt32();
            Proof = r.ReadByteArray(64);
        }
    }

    /// <summary>Client → host: create a new hero in a free slot, protected by a password (sent as a derived key, never in clear).</summary>
    public sealed class CreateHeroMessage : INetMessage
    {
        public MessageType Type => MessageType.CreateHero;
        public string HeroName = string.Empty;
        public string CultureId = string.Empty;
        public bool IsFemale;
        public byte[] Salt = new byte[0];
        public byte[] Key = new byte[0];

        public void Write(BinaryWriter w)
        {
            w.WriteNullable(HeroName);
            w.WriteNullable(CultureId);
            w.Write(IsFemale);
            w.WriteByteArray(Salt);
            w.WriteByteArray(Key);
        }

        public void Read(BinaryReader r)
        {
            HeroName = r.ReadString();
            CultureId = r.ReadString();
            IsFemale = r.ReadBoolean();
            Salt = r.ReadByteArray(64);
            Key = r.ReadByteArray(64);
        }
    }

    /// <summary>Host → client: slot granted. The world follows as <see cref="SaveChunkMessage"/>s.</summary>
    public sealed class JoinAcceptedMessage : INetMessage
    {
        public MessageType Type => MessageType.JoinAccepted;
        public string HeroId = string.Empty;
        public string HeroName = string.Empty;
        public string ResumeToken = string.Empty;
        public long SaveSize;
        public byte[] SaveHash = new byte[0];

        public void Write(BinaryWriter w)
        {
            w.WriteNullable(HeroId);
            w.WriteNullable(HeroName);
            w.WriteNullable(ResumeToken);
            w.Write(SaveSize);
            w.WriteByteArray(SaveHash);
        }

        public void Read(BinaryReader r)
        {
            HeroId = r.ReadString();
            HeroName = r.ReadString();
            ResumeToken = r.ReadString();
            SaveSize = r.ReadInt64();
            SaveHash = r.ReadByteArray(64);
        }
    }

    public sealed class SaveChunkMessage : INetMessage
    {
        public MessageType Type => MessageType.SaveChunk;
        public long Offset;
        public byte[] Data = new byte[0];

        public void Write(BinaryWriter w)
        {
            w.Write(Offset);
            w.WriteByteArray(Data);
        }

        public void Read(BinaryReader r)
        {
            Offset = r.ReadInt64();
            Data = r.ReadByteArray(1024 * 1024);
        }
    }

    /// <summary>Host → anyone on the LAN who asked: what this server is. Sent unconnected, in reply to a discovery broadcast.</summary>
    public sealed class ServerInfoMessage : INetMessage
    {
        public MessageType Type => MessageType.ServerInfo;
        public ushort ProtocolVersion = MessageCodec.ProtocolVersion;
        public string ServerName = string.Empty;
        public bool PasswordRequired;
        public int PlayersOnline;
        public int UsedSlots;
        public int MaxSlots;
        public int Port;

        public void Write(BinaryWriter w)
        {
            w.Write(ProtocolVersion);
            w.WriteNullable(ServerName);
            w.Write(PasswordRequired);
            w.Write(PlayersOnline);
            w.Write(UsedSlots);
            w.Write(MaxSlots);
            w.Write(Port);
        }

        public void Read(BinaryReader r)
        {
            ProtocolVersion = r.ReadUInt16();
            ServerName = r.ReadString();
            PasswordRequired = r.ReadBoolean();
            PlayersOnline = r.ReadInt32();
            UsedSlots = r.ReadInt32();
            MaxSlots = r.ReadInt32();
            Port = r.ReadInt32();
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
