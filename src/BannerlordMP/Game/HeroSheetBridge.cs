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
        private const int MaxStartingItemValue = 15000;
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
            void Step(string what, Action action)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log.Error($"Applying character sheet ({what}) failed", e);
                }
            }

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
            for (var i = 0; i < Equipment.EquipmentSlotLength && i < slots.Count; i++)
            {
                var entry = slots[i] ?? string.Empty;
                if (entry.Length == 0)
                {
                    equipment[i] = EquipmentElement.Invalid;
                    continue;
                }
                var bar = entry.IndexOf('|');
                var item = MBObjectManager.Instance.GetObject<ItemObject>(bar < 0 ? entry : entry.Substring(0, bar));
                if (item == null || item.Value > MaxStartingItemValue)
                {
                    // Empty rather than whatever the server's lord template wore in that slot.
                    Log.Info($"Character sheet: skipped starting item {entry}");
                    equipment[i] = EquipmentElement.Invalid;
                    continue;
                }
                var modifierId = bar < 0 ? string.Empty : entry.Substring(bar + 1);
                var modifier = modifierId.Length > 0 ? MBObjectManager.Instance.GetObject<ItemModifier>(modifierId) : null;
                equipment[i] = new EquipmentElement(item, modifier, null, false);
            }
        }
    }
}
