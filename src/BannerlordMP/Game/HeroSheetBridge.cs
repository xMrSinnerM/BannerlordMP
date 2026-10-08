using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Extensions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace BannerlordMP.Game
{
    /// <summary>
    /// Capture a hero made in the single-player character creator (on the player's machine), and rebuild it in
    /// the server's world.
    /// </summary>
    internal static class HeroSheetBridge
    {
        /// <summary>Starting gear is cheap; this keeps a modified client from sending endgame armour.</summary>
        private const int MaxStartingItemValue = 10000;
        /// <summary>Quick create: a plain start, about what a new single-player character has.</summary>
        private const int BasicStartGold = 1000;
        private const int BasicStartAttribute = 2;
        private const int BasicStartGrain = 5;
        /// <summary>Starting troops are recruits; no knights from the character creator.</summary>
        private const int MaxStartingTroopTier = 3;

        /// <summary>The main hero of the throwaway local campaign, right after character creation.</summary>
        public static HeroSheet CaptureMainHero()
        {
            var hero = Hero.MainHero;
            var sheet = new HeroSheet
            {
                Name = hero.FirstName?.ToString() ?? hero.Name.ToString(),
                ClanName = Clan.PlayerClan?.Name?.ToString() ?? string.Empty,
                CultureId = hero.Culture?.StringId ?? string.Empty,
                IsFemale = hero.IsFemale,
                Age = hero.Age,
                BodyProperties = hero.BodyProperties.ToString(),
                BannerCode = Clan.PlayerClan?.Banner?.Serialize() ?? string.Empty,
                Gold = hero.Gold,
                UnspentAttributePoints = hero.HeroDeveloper.UnspentAttributePoints,
                UnspentFocusPoints = hero.HeroDeveloper.UnspentFocusPoints,
                Level = hero.Level,
                ClanRenown = Clan.PlayerClan?.Renown ?? 0f,
                ClanInfluence = Clan.PlayerClan?.Influence ?? 0f,
            };
            foreach (var attribute in Attributes.All)
                sheet.Attributes[attribute.StringId] = hero.GetAttributeValue(attribute);
            foreach (var skill in Skills.All)
            {
                var value = hero.GetSkillValue(skill);
                if (value > 0)
                    sheet.Skills[skill.StringId] = value;
                var focus = hero.HeroDeveloper.GetFocus(skill);
                if (focus > 0)
                    sheet.Focus[skill.StringId] = focus;
            }
            foreach (var trait in DefaultTraits.Personality)
            {
                var level = hero.GetTraitLevel(trait);
                if (level != 0)
                    sheet.Traits[trait.StringId] = level;
            }
            foreach (var perk in PerkObject.All)
            {
                if (hero.GetPerkValue(perk))
                    sheet.Perks.Add(perk.StringId);
            }
            sheet.BattleEquipment = CaptureEquipment(hero.BattleEquipment);
            sheet.CivilianEquipment = CaptureEquipment(hero.CivilianEquipment);
            var party = MobileParty.MainParty;
            if (party != null)
            {
                for (var i = 0; i < party.ItemRoster.Count; i++)
                {
                    var element = party.ItemRoster.GetElementCopyAtIndex(i);
                    var item = element.EquipmentElement.Item;
                    if (item == null || element.Amount <= 0)
                        continue;
                    var key = item.StringId + "|" + (element.EquipmentElement.ItemModifier?.StringId ?? string.Empty);
                    sheet.Inventory.TryGetValue(key, out var amount);
                    sheet.Inventory[key] = amount + element.Amount;
                }
                foreach (var element in party.MemberRoster.GetTroopRoster())
                {
                    if (element.Character != null && !element.Character.IsHero && element.Number > 0)
                        sheet.Troops.Add(new TroopCount(element.Character.StringId, element.Number, element.WoundedNumber));
                }
            }
            return sheet;
        }

        /// <summary>Server: make a freshly created player hero match the sheet (already clamped to sane limits).</summary>
        public static void Apply(Hero hero, HeroSheet sheet)
        {
            Step("name", () =>
            {
                var name = new TextObject(sheet.Name);
                hero.SetName(name, name);
            });
            Step("appearance", () =>
            {
                hero.IsFemale = sheet.IsFemale;
                if (BodyProperties.FromString(sheet.BodyProperties, out var body))
                {
                    hero.StaticBodyProperties = body.StaticProperties;
                    hero.Weight = body.Weight;
                    hero.Build = body.Build;
                }
            });
            Step("age", () => hero.SetBirthDay(CampaignTime.YearsFromNow(-sheet.Age)));
            Step("attributes", () =>
            {
                foreach (var attribute in Attributes.All)
                {
                    if (!sheet.Attributes.TryGetValue(attribute.StringId, out var target))
                        continue;
                    var change = target - hero.GetAttributeValue(attribute);
                    if (change > 0)
                        hero.HeroDeveloper.AddAttribute(attribute, change, false);
                    else if (change < 0)
                        hero.HeroDeveloper.RemoveAttribute(attribute, -change);
                }
            });
            Step("skills", () =>
            {
                foreach (var skill in Skills.All)
                {
                    sheet.Skills.TryGetValue(skill.StringId, out var level);
                    hero.HeroDeveloper.SetInitialSkillLevel(skill, level);
                }
            });
            Step("level", () => hero.HeroDeveloper.SetInitialLevel(sheet.Level));
            Step("perks", () =>
            {
                // The server's lord template may have come with perks a new single-player hero does not have.
                hero.ClearPerks();
                foreach (var id in sheet.Perks)
                {
                    var perk = PerkObject.All.FirstOrDefault(p => p.StringId == id);
                    if (perk?.Skill != null && hero.GetSkillValue(perk.Skill) >= perk.RequiredSkillValue)
                        hero.HeroDeveloper.AddPerk(perk);
                }
            });
            Step("focus", () =>
            {
                foreach (var skill in Skills.All)
                {
                    sheet.Focus.TryGetValue(skill.StringId, out var target);
                    var change = target - hero.HeroDeveloper.GetFocus(skill);
                    if (change > 0)
                        hero.HeroDeveloper.AddFocus(skill, change, false);
                    else if (change < 0)
                        hero.HeroDeveloper.RemoveFocus(skill, -change);
                }
                hero.HeroDeveloper.UnspentAttributePoints = sheet.UnspentAttributePoints;
                hero.HeroDeveloper.UnspentFocusPoints = sheet.UnspentFocusPoints;
            });
            Step("traits", () =>
            {
                foreach (var trait in DefaultTraits.Personality)
                {
                    sheet.Traits.TryGetValue(trait.StringId, out var level);
                    hero.SetTraitLevel(trait, level);
                }
            });
            Step("equipment", () =>
            {
                ApplyEquipment(hero.BattleEquipment, sheet.BattleEquipment);
                ApplyEquipment(hero.CivilianEquipment, sheet.CivilianEquipment);
            });
            Step("clan", () =>
            {
                var clan = hero.Clan;
                if (clan == null)
                    return;
                if (sheet.ClanName.Length >= 2)
                {
                    var clanName = new TextObject(sheet.ClanName);
                    clan.ChangeClanName(clanName, clanName);
                }
                if (!string.IsNullOrEmpty(sheet.BannerCode))
                {
                    var banner = new Banner(sheet.BannerCode);
                    clan.Banner = banner;
                    clan.UpdateBannerColor(banner.GetPrimaryColor(), banner.GetFirstIconColor());
                }
            });
            Step("gold", () => hero.Gold = sheet.Gold);
            Step("inventory", () =>
            {
                var party = hero.PartyBelongedTo;
                if (party == null)
                    return;
                party.ItemRoster.Clear();
                foreach (var pair in sheet.Inventory)
                {
                    var element = ParseItem(pair.Key);
                    if (element.IsEmpty)
                        Log.Info($"Character sheet: skipped inventory item {pair.Key}");
                    else
                        party.ItemRoster.AddToCounts(element, pair.Value);
                }
            });
            Step("clan standing", () =>
            {
                if (hero.Clan == null)
                    return;
                hero.Clan.Renown = sheet.ClanRenown;
                hero.Clan.Influence = sheet.ClanInfluence;
            });
            Step("troops", () =>
            {
                var party = hero.PartyBelongedTo;
                if (party == null)
                    return;
                // Start with what single player gave, instead of the quick-create recruits.
                var troops = sheet.Troops.Where(t =>
                {
                    var character = MBObjectManager.Instance.GetObject<CharacterObject>(t.CharacterId);
                    if (character != null && !character.IsHero && character.Tier <= MaxStartingTroopTier)
                        return true;
                    Log.Info($"Character sheet: skipped starting troop {t.CharacterId}");
                    return false;
                }).ToList();
                GameBridge.ApplyRoster(party.MemberRoster, troops);
            });
            Log.Info($"Applied single-player character {sheet.Name} ({sheet.CultureId}, age {sheet.Age:0}) to {hero.StringId}");
        }

        private static List<string> CaptureEquipment(Equipment equipment)
        {
            var slots = new List<string>();
            for (var i = 0; i < Equipment.EquipmentSlotLength; i++)
            {
                var element = equipment[i];
                slots.Add(element.IsEmpty || element.Item == null ? string.Empty : element.Item.StringId + "|" + (element.ItemModifier?.StringId ?? string.Empty));
            }
            return slots;
        }

        private static void ApplyEquipment(Equipment equipment, List<string> slots)
        {
            for (var i = 0; i < Equipment.EquipmentSlotLength; i++)
            {
                var entry = i < slots.Count ? slots[i] ?? string.Empty : string.Empty;
                // Unknown or too valuable: empty rather than whatever the server's lord template wore there.
                var element = ParseItem(entry);
                if (element.IsEmpty && entry.Length > 0)
                    Log.Info($"Character sheet: skipped starting item {entry}");
                equipment[i] = element;
            }
        }

        /// <summary>"itemId|modifierId" to an item, or <see cref="EquipmentElement.Invalid"/> if unknown or too valuable to start with.</summary>
        private static EquipmentElement ParseItem(string entry)
        {
            if (string.IsNullOrEmpty(entry))
                return EquipmentElement.Invalid;
            var bar = entry.IndexOf('|');
            var item = MBObjectManager.Instance.GetObject<ItemObject>(bar < 0 ? entry : entry.Substring(0, bar));
            if (item == null || item.Value > MaxStartingItemValue)
                return EquipmentElement.Invalid;
            var modifierId = bar < 0 ? string.Empty : entry.Substring(bar + 1);
            var modifier = modifierId.Length > 0 ? MBObjectManager.Instance.GetObject<ItemModifier>(modifierId) : null;
            return new EquipmentElement(item, modifier, null, false);
        }

        /// <summary>
        /// Quick create (no character creator): a plain new character instead of the AI lord the hero was built
        /// from. Attributes 2, a recruit's skills and gear, no focus, perks or traits, 1000 gold, a little food.
        /// </summary>
        public static void ApplyBasicStart(Hero hero)
        {
            var recruit = hero.Culture?.BasicTroop;
            Step("attributes", () =>
            {
                foreach (var attribute in Attributes.All)
                {
                    var change = BasicStartAttribute - hero.GetAttributeValue(attribute);
                    if (change > 0)
                        hero.HeroDeveloper.AddAttribute(attribute, change, false);
                    else if (change < 0)
                        hero.HeroDeveloper.RemoveAttribute(attribute, -change);
                }
            });
            Step("skills", () =>
            {
                foreach (var skill in Skills.All)
                    hero.HeroDeveloper.SetInitialSkillLevel(skill, recruit?.GetSkillValue(skill) ?? 0);
            });
            Step("level", () => hero.HeroDeveloper.SetInitialLevel(1));
            Step("perks", () => hero.ClearPerks());
            Step("focus", () =>
            {
                foreach (var skill in Skills.All)
                {
                    var focus = hero.HeroDeveloper.GetFocus(skill);
                    if (focus > 0)
                        hero.HeroDeveloper.RemoveFocus(skill, focus);
                }
                hero.HeroDeveloper.UnspentAttributePoints = 0;
                hero.HeroDeveloper.UnspentFocusPoints = 0;
            });
            Step("traits", () =>
            {
                foreach (var trait in DefaultTraits.Personality)
                    hero.SetTraitLevel(trait, 0);
            });
            Step("equipment", () =>
            {
                var battle = recruit?.FirstBattleEquipment;
                var civilian = recruit?.FirstCivilianEquipment;
                for (var i = 0; i < Equipment.EquipmentSlotLength; i++)
                {
                    hero.BattleEquipment[i] = battle?[i] ?? EquipmentElement.Invalid;
                    // Recruits often have no civilian set: then the battle armour without the weapons.
                    hero.CivilianEquipment[i] = civilian != null ? civilian[i]
                        : i >= (int)EquipmentIndex.NumAllWeaponSlots && battle != null ? battle[i] : EquipmentElement.Invalid;
                }
            });
            Step("gold", () => hero.Gold = BasicStartGold);
            Step("food", () =>
            {
                var grain = MBObjectManager.Instance.GetObject<ItemObject>("grain");
                if (grain != null && hero.PartyBelongedTo != null)
                    hero.PartyBelongedTo.ItemRoster.AddToCounts(grain, BasicStartGrain);
            });
            Log.Info($"Gave {hero.StringId} a basic start (quick create)");
        }

        private static void Step(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.Error($"Setting up the new hero ({what}) failed", e);
            }
        }
    }
}
