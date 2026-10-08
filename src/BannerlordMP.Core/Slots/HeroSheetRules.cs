using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;

namespace BannerlordMP.Core.Slots
{
    /// <summary>
    /// Keeps a character from a player's machine within what single-player character creation can produce,
    /// so a modified client cannot send a hero with maxed-out skills. Friends' servers, so the limits are generous.
    /// </summary>
    public static class HeroSheetRules
    {
        public const int MaxAttribute = 10;
        public const int MaxFocusPerSkill = 5;
        public const int MaxStartingSkill = 150;
        public const int MaxStartingGold = 20000;
        public const int MaxStartingTroops = 60;
        public const int MaxTrait = 2;
        public const int MaxStartingLevel = 10;
        public const int MaxStartingPerks = 20;
        public const float MinAge = 18f;
        public const float MaxAge = 60f;

        /// <returns>A list of what was changed (empty if the sheet was already within limits).</returns>
        public static List<string> Clamp(HeroSheet sheet)
        {
            var changes = new List<string>();
            sheet.Name = Trim(sheet.Name, 32, "name", changes);
            sheet.ClanName = Trim(sheet.ClanName, 40, "clan name", changes);
            sheet.Age = ClampValue(sheet.Age, MinAge, MaxAge, "age", changes);
            sheet.Gold = ClampValue(sheet.Gold, 0, MaxStartingGold, "gold", changes);
            sheet.UnspentAttributePoints = ClampValue(sheet.UnspentAttributePoints, 0, 5, "unspent attribute points", changes);
            sheet.UnspentFocusPoints = ClampValue(sheet.UnspentFocusPoints, 0, 10, "unspent focus points", changes);
            sheet.Level = ClampValue(sheet.Level, 1, MaxStartingLevel, "level", changes);
            // The game checks each perk against its skill requirement; this only bounds the list.
            var perks = sheet.Perks.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            if (perks.Count > MaxStartingPerks)
            {
                changes.Add($"perks capped at {MaxStartingPerks}");
                perks = perks.Take(MaxStartingPerks).ToList();
            }
            sheet.Perks = perks;
            ClampAll(sheet.Attributes, 1, MaxAttribute, "attribute", changes);
            ClampAll(sheet.Skills, 0, MaxStartingSkill, "skill", changes);
            ClampAll(sheet.Focus, 0, MaxFocusPerSkill, "focus", changes);
            ClampAll(sheet.Traits, -MaxTrait, MaxTrait, "trait", changes);

            var troops = 0;
            var kept = new List<TroopCount>();
            foreach (var troop in sheet.Troops.Where(t => t.Count > 0))
            {
                var count = Math.Min(troop.Count, MaxStartingTroops - troops);
                if (count <= 0)
                    break;
                kept.Add(new TroopCount(troop.CharacterId, count, Math.Max(0, Math.Min(count, troop.Wounded))));
                troops += count;
            }
            if (kept.Sum(t => t.Count) != sheet.Troops.Where(t => t.Count > 0).Sum(t => t.Count))
                changes.Add($"troops capped at {MaxStartingTroops}");
            sheet.Troops = kept;
            return changes;
        }

        private static string Trim(string value, int max, string what, List<string> changes)
        {
            value = (value ?? string.Empty).Replace("|", "").Replace("\n", " ").Replace("\r", " ").Trim();
            if (value.Length <= max)
                return value;
            changes.Add(what + " shortened");
            return value.Substring(0, max);
        }

        private static int ClampValue(int value, int min, int max, string what, List<string> changes)
        {
            var clamped = Math.Max(min, Math.Min(max, value));
            if (clamped != value)
                changes.Add($"{what} {value} -> {clamped}");
            return clamped;
        }

        private static float ClampValue(float value, float min, float max, string what, List<string> changes)
        {
            var clamped = float.IsNaN(value) ? min : Math.Max(min, Math.Min(max, value));
            if (Math.Abs(clamped - value) > 0.001f)
                changes.Add($"{what} {value} -> {clamped}");
            return clamped;
        }

        private static void ClampAll(Dictionary<string, int> values, int min, int max, string what, List<string> changes)
        {
            foreach (var key in values.Keys.ToList())
                values[key] = ClampValue(values[key], min, max, what + " " + key, changes);
        }
    }
}
