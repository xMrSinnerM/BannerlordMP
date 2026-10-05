using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Extensions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace BannerlordMP.Game
{
    /// <summary>
    /// Game-side half of world replication: capturing and applying the player's ledger, mirroring parties,
    /// and applying world events. Every apply is idempotent, because the player who caused an event also
    /// receives it back from the host.
    /// </summary>
    internal static class WorldBridge
    {
        /// <summary>True while applying something that came from the network; local event listeners must not echo it back.</summary>
        public static bool ApplyingRemote { get; private set; }

        public static void Remote(Action action)
        {
            var previous = ApplyingRemote;
            ApplyingRemote = true;
            try
            {
                action();
            }
            finally
            {
                ApplyingRemote = previous;
            }
        }

        // ----- Ledger -----------------------------------------------------------------------------------------

        public static Dictionary<string, int> CaptureLedger(MobileParty party)
        {
            var ledger = new Dictionary<string, int>();
            if (party == null)
                return ledger;
            var hero = party.LeaderHero;
            if (hero != null)
            {
                ledger["g"] = hero.Gold;
                ledger["hp"] = hero.HitPoints;
                var developer = hero.HeroDeveloper;
                foreach (var skill in Skills.All)
                {
                    var xp = (int)Math.Floor(developer.GetSkillXp(skill));
                    if (xp != 0)
                        ledger["x:" + skill.StringId] = xp;
                    var focus = developer.GetFocus(skill);
                    if (focus != 0)
                        ledger["f:" + skill.StringId] = focus;
                }
                foreach (var attribute in Attributes.All)
                    ledger["a:" + attribute.StringId] = hero.GetAttributeValue(attribute);
            }

            CaptureTroops(party.MemberRoster, "m:", "w:", ledger);
            CaptureTroops(party.PrisonRoster, "p:", "q:", ledger);

            var items = party.ItemRoster;
            for (var i = 0; i < items.Count; i++)
            {
                var element = items.GetElementCopyAtIndex(i);
                var item = element.EquipmentElement.Item;
                if (item == null || element.Amount == 0)
                    continue;
                var key = "i:" + item.StringId + "|" + (element.EquipmentElement.ItemModifier?.StringId ?? string.Empty);
                ledger.TryGetValue(key, out var amount);
                ledger[key] = amount + element.Amount;
            }
            return ledger.Where(p => p.Value != 0).ToDictionary(p => p.Key, p => p.Value);
        }

        /// <param name="onHost">
        /// The host checks attribute and focus spending against the hero's unspent points, so a client cannot
        /// grant itself points; the client applies whatever the host decided.
        /// </param>
        public static void ApplyLedgerDelta(MobileParty party, Dictionary<string, int> delta, bool onHost)
        {
            if (party == null || delta.Count == 0)
                return;
            var hero = party.LeaderHero;
            var troopChanges = new Dictionary<(TroopRoster Roster, string Id), (int Count, int Wounded)>();

            foreach (var pair in delta)
            {
                var key = pair.Key;
                var change = pair.Value;
                try
                {
                    if (key == "g" && hero != null)
                        hero.Gold = Math.Max(0, hero.Gold + change);
                    else if (key == "hp" && hero != null)
                        hero.HitPoints = Math.Max(1, Math.Min(hero.MaxHitPoints, hero.HitPoints + change));
                    else if (key.StartsWith("x:") && hero != null && change > 0)
                        WithSkill(key, skill => hero.HeroDeveloper.AddSkillXp(skill, change, false, false));
                    else if (key.StartsWith("f:") && hero != null)
                        WithSkill(key, skill =>
                        {
                            if (change > 0)
                                hero.HeroDeveloper.AddFocus(skill, change, onHost);
                            else
                                hero.HeroDeveloper.RemoveFocus(skill, -change);
                        });
                    else if (key.StartsWith("a:") && hero != null)
                    {
                        var attribute = Attributes.All.FirstOrDefault(a => a.StringId == key.Substring(2));
                        if (attribute != null && change > 0)
                            hero.HeroDeveloper.AddAttribute(attribute, change, onHost);
                        else if (attribute != null)
                            hero.HeroDeveloper.RemoveAttribute(attribute, -change);
                    }
                    else if (key.StartsWith("m:") || key.StartsWith("w:"))
                        AddTroopChange(troopChanges, party.MemberRoster, key, change);
                    else if (key.StartsWith("p:") || key.StartsWith("q:"))
                        AddTroopChange(troopChanges, party.PrisonRoster, key, change);
                    else if (key.StartsWith("i:"))
                        ApplyItem(party.ItemRoster, key.Substring(2), change);
                }
                catch (Exception e)
                {
                    Log.Error($"Ledger entry {key}{change:+#;-#;0} failed", e);
                }
            }

            foreach (var pair in troopChanges)
            {
                var character = MBObjectManager.Instance.GetObject<CharacterObject>(pair.Key.Id);
                if (character == null || character.IsHero)
                    continue;
                pair.Key.Roster.AddToCounts(character, pair.Value.Count, false, pair.Value.Wounded, 0, true, -1);
            }
        }

        private static void CaptureTroops(TroopRoster roster, string countPrefix, string woundedPrefix, Dictionary<string, int> ledger)
        {
            foreach (var element in roster.GetTroopRoster())
            {
                // Heroes (companions, captured lords) move between parties through their own actions; not counters.
                if (element.Character == null || element.Character.IsHero)
                    continue;
                var id = element.Character.StringId;
                ledger.TryGetValue(countPrefix + id, out var count);
                ledger[countPrefix + id] = count + element.Number;
                ledger.TryGetValue(woundedPrefix + id, out var wounded);
                ledger[woundedPrefix + id] = wounded + element.WoundedNumber;
            }
        }

        private static void AddTroopChange(Dictionary<(TroopRoster, string), (int, int)> changes, TroopRoster roster, string key, int change)
        {
            var id = key.Substring(2);
            changes.TryGetValue((roster, id), out var current);
            changes[(roster, id)] = key[0] == 'm' || key[0] == 'p' ? (current.Item1 + change, current.Item2) : (current.Item1, current.Item2 + change);
        }

        private static void ApplyItem(ItemRoster items, string id, int change)
        {
            var bar = id.IndexOf('|');
            var itemId = bar < 0 ? id : id.Substring(0, bar);
            var modifierId = bar < 0 ? string.Empty : id.Substring(bar + 1);
            var item = MBObjectManager.Instance.GetObject<ItemObject>(itemId);
            if (item == null)
                return;
            var modifier = modifierId.Length > 0 ? MBObjectManager.Instance.GetObject<ItemModifier>(modifierId) : null;
            items.AddToCounts(new EquipmentElement(item, modifier, null, false), change);
        }

        private static void WithSkill(string key, Action<SkillObject> action)
        {
            var skill = Skills.All.FirstOrDefault(s => s.StringId == key.Substring(2));
            if (skill != null)
                action(skill);
        }

        // ----- Parties ----------------------------------------------------------------------------------------

        public static PartySpawnedMessage DescribeParty(MobileParty party, double hostHours)
        {
            var position = GameBridge.GetPosition(party);
            return new PartySpawnedMessage
            {
                HostHours = hostHours,
                PartyId = party.StringId,
                Name = party.Name?.ToString() ?? string.Empty,
                ClanId = party.ActualClan?.StringId ?? string.Empty,
                LeaderHeroId = party.LeaderHero?.StringId ?? string.Empty,
                HomeSettlementId = party.HomeSettlement?.StringId ?? string.Empty,
                IsLordParty = party.IsLordParty,
                X = position.X,
                Y = position.Y,
                IsOnLand = position.IsOnLand,
                Members = GameBridge.CaptureRoster(party.MemberRoster),
                Prisoners = GameBridge.CaptureRoster(party.PrisonRoster),
            };
        }

        /// <summary>
        /// Creates a local stand-in for a party the host spawned: a real lord party when its leader is free, otherwise
        /// a custom party with the same name, clan (so hostility is right), troops and position. Its AI stays off:
        /// the host moves it.
        /// </summary>
        public static MobileParty CreateMirrorParty(PartySpawnedMessage message)
        {
            var existing = GameBridge.FindParty(message.PartyId);
            if (existing != null)
                return existing;

            MobileParty party = null;
            Remote(() =>
            {
                var clan = Find<Clan>(message.ClanId);
                var home = Find<Settlement>(message.HomeSettlementId);
                var leader = GameBridge.FindHero(message.LeaderHeroId);
                var position = new CampaignVec2(new Vec2(message.X, message.Y), message.IsOnLand);

                if (message.IsLordParty && leader != null && leader.IsAlive && leader.PartyBelongedTo == null)
                {
                    party = LordPartyComponent.CreateLordParty(message.PartyId, leader, position, 0f, home, leader);
                    GameBridge.ApplyRoster(party.MemberRoster, message.Members);
                }
                else
                {
                    var members = TroopRoster.CreateDummyTroopRoster();
                    var prisoners = TroopRoster.CreateDummyTroopRoster();
                    GameBridge.ApplyRoster(members, message.Members);
                    GameBridge.ApplyRoster(prisoners, message.Prisoners);
                    party = CustomPartyComponent.CreateCustomPartyWithTroopRoster(position, 0f, home, new TextObject(message.Name), clan,
                        members, prisoners, null, string.Empty, string.Empty, 0f, false);
                    party.StringId = message.PartyId;
                }
                GameBridge.ApplyRoster(party.PrisonRoster, message.Prisoners);
                MakePuppet(party);
            });
            return party;
        }

        /// <summary>A party on a client: no AI, no movement of its own. Positions come from the host.</summary>
        public static void MakePuppet(MobileParty party)
        {
            if (party == null || party == MobileParty.MainParty)
                return;
            party.Ai.DisableAi();
            party.SetMoveModeHold();
        }

        public static void ApplyRosters(MobileParty party, List<TroopCount> members, List<TroopCount> prisoners)
        {
            if (party == null || party == MobileParty.MainParty)
                return;
            Remote(() =>
            {
                GameBridge.ApplyRoster(party.MemberRoster, members);
                GameBridge.ApplyRoster(party.PrisonRoster, prisoners);
            });
        }

        public static int RosterHash(MobileParty party)
        {
            unchecked
            {
                var hash = 17;
                foreach (var element in party.MemberRoster.GetTroopRoster())
                    hash = hash * 31 + (element.Character?.StringId?.GetHashCode() ?? 0) * 7 + element.Number * 3 + element.WoundedNumber;
                foreach (var element in party.PrisonRoster.GetTroopRoster())
                    hash = hash * 37 + (element.Character?.StringId?.GetHashCode() ?? 0) * 5 + element.Number;
                return hash;
            }
        }

        /// <summary>Client: the host says an AI party caught us. Start the encounter here, as if it had happened locally.</summary>
        public static bool StartEncounterWith(MobileParty attacker)
        {
            var main = MobileParty.MainParty;
            if (attacker == null || !attacker.IsActive || main == null || PlayerEncounter.Current != null || !GameBridge.IsOnMapWithoutMenu())
                return false;
            EncounterManager.StartPartyEncounter(attacker.Party, main.Party);
            return true;
        }

        // ----- World events -----------------------------------------------------------------------------------

        public static void ApplyWorldEvent(WorldEventMessage message)
        {
            Remote(() =>
            {
                try
                {
                    switch (message.Kind)
                    {
                        case WorldEventKind.SettlementOwner:
                        {
                            var settlement = Find<Settlement>(message.A);
                            var owner = GameBridge.FindHero(message.B);
                            if (settlement != null && owner != null && settlement.Owner != owner)
                                ChangeOwnerOfSettlementAction.ApplyByDefault(owner, settlement);
                            break;
                        }
                        case WorldEventKind.War:
                        {
                            var a = FindFaction(message.A);
                            var b = FindFaction(message.B);
                            if (a != null && b != null && a != b && !FactionManager.IsAtWarAgainstFaction(a, b))
                                DeclareWarAction.ApplyByDefault(a, b);
                            break;
                        }
                        case WorldEventKind.Peace:
                        {
                            var a = FindFaction(message.A);
                            var b = FindFaction(message.B);
                            if (a != null && b != null && FactionManager.IsAtWarAgainstFaction(a, b))
                                MakePeaceAction.Apply(a, b);
                            break;
                        }
                        case WorldEventKind.ClanKingdom:
                        {
                            var clan = Find<Clan>(message.A);
                            if (clan == null)
                                break;
                            var kingdom = Find<Kingdom>(message.B);
                            if (kingdom == null && clan.Kingdom != null)
                                ChangeKingdomAction.ApplyByLeaveKingdom(clan, false);
                            else if (kingdom != null && clan.Kingdom != kingdom)
                                ChangeKingdomAction.ApplyByJoinToKingdom(clan, kingdom, CampaignTime.Now, false);
                            break;
                        }
                        case WorldEventKind.HeroKilled:
                        {
                            var victim = GameBridge.FindHero(message.A);
                            if (victim != null && victim.IsAlive)
                                KillCharacterAction.ApplyByBattle(victim, GameBridge.FindHero(message.B), false);
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error("Could not apply world event " + message, e);
                }
            });
        }

        public static IFaction FindFaction(string id)
        {
            return (IFaction)Find<Kingdom>(id) ?? Find<Clan>(id);
        }

        public static T Find<T>(string id) where T : MBObjectBase
        {
            if (string.IsNullOrEmpty(id) || Campaign.Current == null)
                return null;
            return Campaign.Current.CampaignObjectManager.Find<T>(id) ?? MBObjectManager.Instance.GetObject<T>(id);
        }
    }
}
