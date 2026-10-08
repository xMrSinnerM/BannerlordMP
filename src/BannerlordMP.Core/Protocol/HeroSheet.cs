using System.Collections.Generic;
using System.IO;

namespace BannerlordMP.Core.Protocol
{
    /// <summary>
    /// A character made in the single-player character creator on the player's machine: everything the server
    /// needs to build the same hero in its world.
    /// </summary>
    public sealed class HeroSheet
    {
        public string Name = string.Empty;
        public string ClanName = string.Empty;
        public string CultureId = string.Empty;
        public bool IsFemale;
        public float Age = 20f;
        /// <summary>The game's BodyProperties string (face, hair, build, weight).</summary>
        public string BodyProperties = string.Empty;
        public string BannerCode = string.Empty;
        public int Gold;
        public int UnspentAttributePoints;
        public int UnspentFocusPoints;
        public int Level = 1;
        public Dictionary<string, int> Attributes = new Dictionary<string, int>();
        public Dictionary<string, int> Skills = new Dictionary<string, int>();
        public Dictionary<string, int> Focus = new Dictionary<string, int>();
        public Dictionary<string, int> Traits = new Dictionary<string, int>();
        /// <summary>One entry per equipment slot: "itemId|modifierId", or empty for an empty slot.</summary>
        public List<string> BattleEquipment = new List<string>();
        public List<string> CivilianEquipment = new List<string>();
        public List<TroopCount> Troops = new List<TroopCount>();
        public List<string> Perks = new List<string>();
        /// <summary>The party's starting inventory (food and the like): "itemId|modifierId" to amount.</summary>
        public Dictionary<string, int> Inventory = new Dictionary<string, int>();
        public float ClanRenown;
        public float ClanInfluence;

        /// <summary>For keeping the sheet with the player's slot, so a lost hero can be rebuilt the same way.</summary>
        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                    Write(writer);
                return stream.ToArray();
            }
        }

        public static HeroSheet FromBytes(byte[] data)
        {
            using (var reader = new BinaryReader(new MemoryStream(data)))
                return Read(reader);
        }

        internal void Write(BinaryWriter w)
        {
            w.WriteNullable(Name);
            w.WriteNullable(ClanName);
            w.WriteNullable(CultureId);
            w.Write(IsFemale);
            w.Write(Age);
            w.WriteNullable(BodyProperties);
            w.WriteNullable(BannerCode);
            w.Write(Gold);
            w.Write(UnspentAttributePoints);
            w.Write(UnspentFocusPoints);
            w.Write(Level);
            w.WriteCounters(Attributes);
            w.WriteCounters(Skills);
            w.WriteCounters(Focus);
            w.WriteCounters(Traits);
            w.WriteList(BattleEquipment, (bw, s) => bw.WriteNullable(s));
            w.WriteList(CivilianEquipment, (bw, s) => bw.WriteNullable(s));
            w.WriteList(Troops, (bw, t) => t.Write(bw));
            w.WriteList(Perks, (bw, s) => bw.WriteNullable(s));
            w.WriteCounters(Inventory);
            w.Write(ClanRenown);
            w.Write(ClanInfluence);
        }

        internal static HeroSheet Read(BinaryReader r)
        {
            var sheet = new HeroSheet
            {
                Name = r.ReadString(),
                ClanName = r.ReadString(),
                CultureId = r.ReadString(),
                IsFemale = r.ReadBoolean(),
                Age = r.ReadSingle(),
                BodyProperties = r.ReadString(),
                BannerCode = r.ReadString(),
                Gold = r.ReadInt32(),
                UnspentAttributePoints = r.ReadInt32(),
                UnspentFocusPoints = r.ReadInt32(),
                Level = r.ReadInt32(),
                Attributes = r.ReadCounters(),
                Skills = r.ReadCounters(),
                Focus = r.ReadCounters(),
                Traits = r.ReadCounters(),
                BattleEquipment = r.ReadList(br => br.ReadString()),
                CivilianEquipment = r.ReadList(br => br.ReadString()),
                Troops = r.ReadList(TroopCount.Read),
                Perks = r.ReadList(br => br.ReadString()),
            };
            sheet.Inventory = r.ReadCounters();
            sheet.ClanRenown = r.ReadSingle();
            sheet.ClanInfluence = r.ReadSingle();
            return sheet;
        }
    }
}
