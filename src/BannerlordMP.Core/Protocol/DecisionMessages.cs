using System.Collections.Generic;
using System.IO;

namespace BannerlordMP.Core.Protocol
{
    /// <summary>How strongly a clan backs an outcome. Mirrors the game's support weights.</summary>
    public enum VoteWeight : byte
    {
        Abstain = 0,
        SlightlyFavor = 1,
        StronglyFavor = 2,
        FullyPush = 3,
        /// <summary>The ruler picking the final outcome.</summary>
        Choose = 4,
    }

    public sealed class DecisionOption
    {
        public string Title = string.Empty;
        public string Description = string.Empty;

        internal void Write(BinaryWriter w)
        {
            w.WriteNullable(Title);
            w.WriteNullable(Description);
        }

        internal static DecisionOption Read(BinaryReader r) => new DecisionOption { Title = r.ReadString(), Description = r.ReadString() };
    }

    /// <summary>
    /// Host → a player whose clan is in a kingdom: a kingdom decision is pending; how do you vote? On the host the
    /// AI never votes for a player clan; it abstains unless the player answers.
    /// </summary>
    public sealed class DecisionVoteRequestMessage : INetMessage
    {
        public MessageType Type => MessageType.DecisionVoteRequest;
        public int DecisionId;
        public string KingdomName = string.Empty;
        public string Title = string.Empty;
        public string Description = string.Empty;
        /// <summary>True if the player's clan rules the kingdom and picks the final outcome.</summary>
        public bool IsRuler;
        public float DaysLeft;
        public List<DecisionOption> Options = new List<DecisionOption>();
        /// <summary>Influence cost of SlightlyFavor, StronglyFavor and FullyPush, in that order.</summary>
        public List<int> WeightCosts = new List<int>();

        public void Write(BinaryWriter w)
        {
            w.Write(DecisionId);
            w.WriteNullable(KingdomName);
            w.WriteNullable(Title);
            w.WriteNullable(Description);
            w.Write(IsRuler);
            w.Write(DaysLeft);
            w.WriteList(Options, (bw, o) => o.Write(bw));
            w.WriteList(WeightCosts, (bw, c) => bw.Write(c));
        }

        public void Read(BinaryReader r)
        {
            DecisionId = r.ReadInt32();
            KingdomName = r.ReadString();
            Title = r.ReadString();
            Description = r.ReadString();
            IsRuler = r.ReadBoolean();
            DaysLeft = r.ReadSingle();
            Options = r.ReadList(DecisionOption.Read);
            WeightCosts = r.ReadList(br => br.ReadInt32());
        }
    }

    /// <summary>Client → host: the player's vote. <see cref="OptionIndex"/> −1 (or weight Abstain) abstains.</summary>
    public sealed class DecisionVoteMessage : INetMessage
    {
        public MessageType Type => MessageType.DecisionVote;
        public int DecisionId;
        public int OptionIndex = -1;
        public VoteWeight Weight;

        public void Write(BinaryWriter w)
        {
            w.Write(DecisionId);
            w.Write(OptionIndex);
            w.Write((byte)Weight);
        }

        public void Read(BinaryReader r)
        {
            DecisionId = r.ReadInt32();
            OptionIndex = r.ReadInt32();
            Weight = (VoteWeight)r.ReadByte();
        }
    }
}
