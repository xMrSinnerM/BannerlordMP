using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BannerlordMP.Core.Security;

namespace BannerlordMP.Core.Slots
{
    public sealed class PlayerSlot
    {
        public int SlotId;
        public string HeroId = string.Empty;
        public string HeroName = string.Empty;
        public string CultureName = string.Empty;
        public byte[] Salt = Array.Empty<byte>();
        public byte[] Key = Array.Empty<byte>();
    }

    /// <summary>
    /// The heroes that belong to players on a server. Each slot is a hero plus the key derived from its owner's
    /// password; claiming a slot requires proving that password. Stored by the server next to the mod, never in
    /// the campaign save (the save is sent to every player).
    /// </summary>
    public sealed class SlotRegistry
    {
        private const string Header = "BannerlordMP slots v1";
        private readonly List<PlayerSlot> _slots = new List<PlayerSlot>();

        public SlotRegistry(int maxSlots)
        {
            MaxSlots = Math.Max(1, maxSlots);
        }

        /// <summary>How many player heroes the server allows. Lowering it never deletes existing slots.</summary>
        public int MaxSlots { get; set; }

        public IReadOnlyList<PlayerSlot> Slots => _slots;

        public bool CanCreate => _slots.Count < MaxSlots;

        public PlayerSlot Find(int slotId) => _slots.FirstOrDefault(s => s.SlotId == slotId);

        public PlayerSlot FindByHero(string heroId) => _slots.FirstOrDefault(s => s.HeroId == heroId);

        public bool IsNameTaken(string heroName) =>
            _slots.Any(s => string.Equals(s.HeroName, heroName?.Trim(), StringComparison.OrdinalIgnoreCase));

        public PlayerSlot Add(string heroId, string heroName, string cultureName, byte[] salt, byte[] key)
        {
            if (!CanCreate)
                throw new InvalidOperationException("All slots are taken.");
            if (salt == null || salt.Length < 8 || key == null || key.Length != PasswordProof.KeySize)
                throw new ArgumentException("Invalid hero password data.");
            var slot = new PlayerSlot
            {
                SlotId = _slots.Count == 0 ? 1 : _slots.Max(s => s.SlotId) + 1,
                HeroId = heroId,
                HeroName = heroName,
                CultureName = cultureName ?? string.Empty,
                Salt = salt,
                Key = key,
            };
            _slots.Add(slot);
            return slot;
        }

        public bool Remove(int slotId) => _slots.RemoveAll(s => s.SlotId == slotId) > 0;

        public bool VerifyClaim(int slotId, byte[] nonce, byte[] proof)
        {
            var slot = Find(slotId);
            return slot != null && PasswordProof.Verify(slot.Key, nonce, proof);
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Header);
            sb.AppendLine("max=" + MaxSlots.ToString(CultureInfo.InvariantCulture));
            foreach (var s in _slots)
            {
                sb.AppendLine(string.Join("|",
                    s.SlotId.ToString(CultureInfo.InvariantCulture),
                    Encode(s.HeroId),
                    Encode(s.HeroName),
                    Encode(s.CultureName),
                    Convert.ToBase64String(s.Salt),
                    Convert.ToBase64String(s.Key)));
            }
            return sb.ToString();
        }

        public static SlotRegistry Deserialize(string text, int defaultMaxSlots)
        {
            var registry = new SlotRegistry(defaultMaxSlots);
            if (string.IsNullOrWhiteSpace(text))
                return registry;

            var lines = text.Replace("\r", string.Empty).Split('\n');
            if (lines[0] != Header)
                throw new FormatException("Not a BannerlordMP slot file.");
            foreach (var line in lines.Skip(1))
            {
                if (line.Length == 0)
                    continue;
                if (line.StartsWith("max=", StringComparison.Ordinal))
                {
                    registry.MaxSlots = Math.Max(1, int.Parse(line.Substring(4), CultureInfo.InvariantCulture));
                    continue;
                }
                var parts = line.Split('|');
                if (parts.Length != 6)
                    throw new FormatException("Corrupt slot line.");
                registry._slots.Add(new PlayerSlot
                {
                    SlotId = int.Parse(parts[0], CultureInfo.InvariantCulture),
                    HeroId = Decode(parts[1]),
                    HeroName = Decode(parts[2]),
                    CultureName = Decode(parts[3]),
                    Salt = Convert.FromBase64String(parts[4]),
                    Key = Convert.FromBase64String(parts[5]),
                });
            }
            return registry;
        }

        private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

        private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
