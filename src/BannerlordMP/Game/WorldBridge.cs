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
                if (hero.Clan != null && hero.Clan.Leader == hero)
                {
                    ledger["inf"] = (int)Math.Floor(hero.Clan.Influence);
                    ledger["ren"] = (int)Math.Floor(hero.Clan.Renown);
                }
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
                CaptureEquipment(hero.BattleEquipment, 'b', ledger);
                CaptureEquipment(hero.CivilianEquipment, 'c', ledger);
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
            var equipmentChanges = new List<KeyValuePair<string, int>>();

            foreach (var pair in delta)
            {
                var key = pair.Key;
                var change = pair.Value;
                try
                {
                    if (key == "g" && hero != null)
                        hero.Gold = Math.Max(0, hero.Gold + change);
                    else if (key == "inf" && hero?.Clan != null)
                        hero.Clan.Influence += change;
                    else if (key == "ren" && hero?.Clan != null)
                    {
                        // AddRenown also raises the clan tier; renown never lowers a tier, so a loss is set directly.
                        if (change > 0)
                            hero.Clan.AddRenown(change, false);
                        else
                            hero.Clan.Renown = Math.Max(0f, hero.Clan.Renown + change);
                    }
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
                    else if (key.StartsWith("e:") && hero != null)
                        equipmentChanges.Add(pair);
                }
                catch (Exception e)
                {
                    Log.Error($"Ledger entry {key}{change:+#;-#;0} failed", e);
                }
            }

            // Take off before putting on, so swapping the item in a slot works whatever order the entries came in.
            foreach (var pair in equipmentChanges.OrderBy(p => p.Value))
            {
                try
                {
                    ApplyEquipmentChange(hero, pair.Key, pair.Value);
                }
                catch (Exception e)
                {
                    Log.Error($"Equipment change {pair.Key}{pair.Value:+#;-#;0} failed", e);
                }
            }

            foreach (var pair in troopChanges)
            {
                try
                {
                    ApplyTroopChange(pair.Key.Roster, pair.Key.Id, pair.Value.Count, pair.Value.Wounded);
                }
                catch (Exception e)
                {
                    Log.Error($"Troop change {pair.Key.Id} ({pair.Value.Count:+#;-#;0}, wounded {pair.Value.Wounded:+#;-#;0}) failed", e);
                }
            }
        }

        /// <summary>
        /// Applies a troop count change, kept within what the roster can hold: the two sides' rosters can differ
        /// slightly (troops healed or died on one side first), and the game throws on a count or wounded number
        /// below zero, or more wounded than troops.
        /// </summary>
        private static void ApplyTroopChange(TroopRoster roster, string characterId, int countChange, int woundedChange)
        {
            var character = MBObjectManager.Instance.GetObject<CharacterObject>(characterId);
            if (character == null || character.IsHero)
                return;
            var index = roster.FindIndexOfTroop(character);
            var count = index >= 0 ? roster.GetElementNumber(index) : 0;
            var wounded = index >= 0 ? roster.GetElementWoundedNumber(index) : 0;
            var newCount = Math.Max(0, count + countChange);
            var newWounded = Math.Max(0, Math.Min(newCount, wounded + woundedChange));
            if (newCount == count && newWounded == wounded)
                return;
            roster.AddToCounts(character, newCount - count, false, newWounded - wounded, 0, true, -1);
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

        /// <summary>
        /// What the hero wears, one counter per filled slot: "e:" + set ('b' battle, 'c' civilian) + slot index +
        /// ":" + "itemId|modifierId" = 1. Changing a slot's item is then -1 for the old one and +1 for the new one.
        /// </summary>
        private static void CaptureEquipment(Equipment equipment, char set, Dictionary<string, int> ledger)
        {
            if (equipment == null)
                return;
            for (var i = 0; i < Equipment.EquipmentSlotLength; i++)
            {
                var element = equipment[i];
                if (element.IsEmpty || element.Item == null)
                    continue;
                ledger[$"e:{set}{i}:{element.Item.StringId}|{element.ItemModifier?.StringId ?? string.Empty}"] = 1;
            }
        }

        private static void ApplyEquipmentChange(Hero hero, string key, int change)
        {
            var colon = key.IndexOf(':', 2);
            if (colon < 4 || change == 0 || !int.TryParse(key.Substring(3, colon - 3), out var slot) || slot < 0 || slot >= Equipment.EquipmentSlotLength)
                return;
            var equipment = key[2] == 'c' ? hero.CivilianEquipment : key[2] == 'b' ? hero.BattleEquipment : null;
            var element = ParseElement(key.Substring(colon + 1));
            if (equipment == null || element.IsEmpty)
                return;
            var current = equipment[slot];
            var same = !current.IsEmpty && current.Item == element.Item && current.ItemModifier == element.ItemModifier;
            if (change < 0 && same)
                equipment[slot] = EquipmentElement.Invalid;
            else if (change > 0 && !same)
                equipment[slot] = element;
        }

        /// <summary>"itemId|modifierId" to an item, or <see cref="EquipmentElement.Invalid"/> if this game has no such item.</summary>
        public static EquipmentElement ParseElement(string id)
        {
            var bar = id.IndexOf('|');
            var item = MBObjectManager.Instance.GetObject<ItemObject>(bar < 0 ? id : id.Substring(0, bar));
            if (item == null)
                return EquipmentElement.Invalid;
            var modifierId = bar < 0 ? string.Empty : id.Substring(bar + 1);
            var modifier = modifierId.Length > 0 ? MBObjectManager.Instance.GetObject<ItemModifier>(modifierId) : null;
            return new EquipmentElement(item, modifier, null, false);
        }

        public static string ElementKey(EquipmentElement element) => element.Item.StringId + "|" + (element.ItemModifier?.StringId ?? string.Empty);

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
            if (party == null || party == MobileParty.MainParty || !party.IsActive)
                return;
            try
            {
                party.Ai.DisableAi();
                // Parties inside settlements or attached to armies are held by the game already.
                if (party.CurrentSettlement == null && party.AttachedTo == null && party.MapEvent == null)
                    party.SetMoveModeHold();
            }
            catch (Exception e)
            {
                Log.Error("Could not make a puppet of " + party.StringId, e);
            }
        }

        /// <summary>
        /// Host: a player created a party for one of their clan's heroes on their machine (clan screen). The real party
        /// is made here as a lord party of that hero, with the id, position and troops their game gave it, so their
        /// copy becomes its mirror. From then on the host's AI leads it, as single player's AI leads clan parties.
        /// </summary>
        /// <returns>Null when created, otherwise why not.</returns>
        public static string CreatePlayerClanParty(Clan clan, PartySpawnedMessage message)
        {
            var leader = GameBridge.FindHero(message.LeaderHeroId);
            if (clan == null || leader == null || leader.Clan != clan || leader == clan.Leader)
                return "its leader is not a hero of your clan here";
            if (!leader.IsAlive || leader.IsPrisoner)
                return $"{leader.Name} is dead or a prisoner here";
            var existing = GameBridge.FindParty(message.PartyId);
            if (existing != null)
                return existing.LeaderHero == leader ? null : "its id is already used by another party";
            if (leader.PartyBelongedTo != null && leader.PartyBelongedTo.LeaderHero == leader)
                return $"{leader.Name} already leads a party here";

            string problem = null;
            Remote(() =>
            {
                try
                {
                    // Here the hero is usually still a member of the player's party (heroes are not in the ledger).
                    leader.PartyBelongedTo?.MemberRoster.AddToCounts(leader.CharacterObject, -1, false, 0, 0, true, -1);
                    var home = Find<Settlement>(message.HomeSettlementId) ?? leader.HomeSettlement ?? clan.HomeSettlement;
                    var position = new CampaignVec2(new Vec2(message.X, message.Y), message.IsOnLand);
                    var party = LordPartyComponent.CreateLordParty(message.PartyId, leader, position, 0f, home, leader);
                    GameBridge.ApplyRoster(party.MemberRoster, message.Members);
                    GameBridge.ApplyRoster(party.PrisonRoster, message.Prisoners);
                }
                catch (Exception e)
                {
                    Log.Error($"Creating clan party {message.PartyId} for {leader.StringId} failed", e);
                    problem = "the game refused";
                }
            });
            return problem;
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
        /// <summary>
        /// Why the host's "they caught you" cannot be acted on here, or null if it can. The two worlds can differ
        /// for a moment (a party beaten in our siege still exists on the host), and starting an encounter with a
        /// party that is gone here kept the attack menu coming back and then crashed the game.
        /// </summary>
        public static string WhyNoEncounter(MobileParty attacker)
        {
            var main = MobileParty.MainParty;
            if (attacker == null)
                return "party unknown here";
            if (main == null || main.MapEvent != null || main.CurrentSettlement != null || main.BesiegedSettlement != null || main.SiegeEvent != null)
                return "we are busy (battle, town or siege)";
            if (!attacker.IsActive || attacker.ShouldBeIgnored)
                return "that party is gone here";
            if (attacker.MapEvent != null || attacker.CurrentSettlement != null || attacker.BesiegedSettlement != null)
                return "that party is busy (battle, town or siege)";
            if (attacker.MemberRoster == null || attacker.MemberRoster.TotalHealthyCount <= 0)
                return "that party has no troops here";
            if (attacker.MapFaction == null || main.MapFaction == null || !FactionManager.IsAtWarAgainstFaction(attacker.MapFaction, main.MapFaction))
                return "not at war here";
            if (attacker.Position.ToVec2().DistanceSquared(main.Position.ToVec2()) > 3f * 3f)
                return "too far away here";
            return null;
        }

        public static bool StartEncounterWith(MobileParty attacker)
        {
            var main = MobileParty.MainParty;
            if (attacker == null || !attacker.IsActive || main == null || PlayerEncounter.Current != null || !GameBridge.IsOnMapWithoutMenu())
                return false;
            EncounterManager.StartPartyEncounter(attacker.Party, main.Party);
            return true;
        }

        // ----- Markets ----------------------------------------------------------------------------------------

        /// <summary>Towns and villages: places with stock and gold to trade against.</summary>
        public static bool HasMarket(Settlement settlement) =>
            settlement != null && (settlement.IsTown || settlement.IsVillage) && settlement.SettlementComponent != null && settlement.ItemRoster != null;

        public static MarketStateMessage CaptureMarket(Settlement settlement)
        {
            var market = new MarketStateMessage
            {
                SettlementId = settlement.StringId,
                Gold = settlement.SettlementComponent.Gold,
                Prosperity = settlement.Town?.Prosperity ?? -1f,
                Items = CaptureStock(settlement.ItemRoster),
            };
            var data = settlement.Town?.MarketData;
            if (data != null)
            {
                foreach (var category in ItemCategories.All)
                    market.Categories.Add(new CategoryMarket(category.StringId, data.GetSupply(category), data.GetDemand(category)));
            }
            return market;
        }

        /// <summary>Client: make a settlement's market what the host says it is.</summary>
        public static void ApplyMarketState(Settlement settlement, MarketStateMessage market)
        {
            var component = settlement.SettlementComponent;
            component.ChangeGold(Math.Max(0, market.Gold) - component.Gold);
            if (settlement.Town != null && market.Prosperity >= 0)
                settlement.Town.Prosperity = market.Prosperity;

            var current = CaptureStock(settlement.ItemRoster);
            foreach (var key in current.Keys.Union(market.Items.Keys).ToList())
            {
                current.TryGetValue(key, out var have);
                market.Items.TryGetValue(key, out var want);
                ChangeStock(settlement.ItemRoster, key, Math.Max(0, want) - have);
            }

            var data = settlement.Town?.MarketData;
            if (data != null)
            {
                foreach (var entry in market.Categories)
                {
                    var category = ItemCategories.All.FirstOrDefault(c => c.StringId == entry.CategoryId);
                    if (category != null)
                        data.SetSupplyDemand(category, entry.Supply, entry.Demand);
                }
            }
        }

        /// <summary>Client: what trading changed since <paramref name="before"/>, or null if nothing did.</summary>
        public static MarketChangeMessage DiffMarket(Settlement settlement, MarketStateMessage before)
        {
            var change = new MarketChangeMessage { SettlementId = settlement.StringId, GoldChange = settlement.SettlementComponent.Gold - before.Gold };
            var now = CaptureStock(settlement.ItemRoster);
            foreach (var key in now.Keys.Union(before.Items.Keys))
            {
                now.TryGetValue(key, out var after);
                before.Items.TryGetValue(key, out var was);
                if (after != was)
                    change.Items[key] = after - was;
            }
            return change.GoldChange == 0 && change.Items.Count == 0 ? null : change;
        }

        /// <summary>Host: apply a player's trading, kept within what the settlement has.</summary>
        public static void ApplyMarketChange(Settlement settlement, MarketChangeMessage change)
        {
            var component = settlement.SettlementComponent;
            component.ChangeGold(Math.Max(-component.Gold, change.GoldChange));
            var current = CaptureStock(settlement.ItemRoster);
            foreach (var pair in change.Items)
            {
                current.TryGetValue(pair.Key, out var have);
                ChangeStock(settlement.ItemRoster, pair.Key, Math.Max(-have, pair.Value));
            }
        }

        private static Dictionary<string, int> CaptureStock(ItemRoster roster)
        {
            var stock = new Dictionary<string, int>();
            for (var i = 0; i < roster.Count; i++)
            {
                var element = roster.GetElementCopyAtIndex(i);
                if (element.EquipmentElement.Item == null || element.Amount <= 0)
                    continue;
                var key = ElementKey(element.EquipmentElement);
                stock.TryGetValue(key, out var amount);
                stock[key] = amount + element.Amount;
            }
            return stock;
        }

        private static void ChangeStock(ItemRoster roster, string key, int change)
        {
            if (change == 0)
                return;
            var element = ParseElement(key);
            if (!element.IsEmpty)
                roster.AddToCounts(element, change);
        }

        // ----- World events -----------------------------------------------------------------------------------

        public static void ApplyWorldEvent(WorldEventMessage message)
        {
            Log.Info("World event applied: " + message);
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
                        case WorldEventKind.SiegeStarted:
                        {
                            var settlement = Find<Settlement>(message.A);
                            var besieger = GameBridge.FindParty(message.B);
                            if (settlement == null || besieger == null || !besieger.IsActive)
                                break;
                            if (settlement.SiegeEvent == null)
                                Campaign.Current.SiegeEventManager.StartSiegeEvent(settlement, besieger);
                            // The besieger here is a stand-in that holds still (the network moves it). A siege whose
                            // leader is not set to besiege is lifted at once, which kicked players out of their sieges.
                            if (besieger != MobileParty.MainParty)
                                besieger.SetMoveBesiegeSettlement(settlement, MobileParty.NavigationType.Default);
                            break;
                        }
                        case WorldEventKind.SiegeEnded:
                        {
                            var siege = Find<Settlement>(message.A)?.SiegeEvent;
                            if (siege == null || siege.ReadyToBeRemoved)
                                break;
                            // A player's own siege is theirs: only they end it (lift, break, take the town).
                            if (siege.BesiegerCamp?.LeaderParty == MobileParty.MainParty && Session.MpSession.Current is Session.ClientSession)
                            {
                                Log.Info("Ignoring siege end for our own siege of " + message.A);
                                break;
                            }
                            siege.FinalizeSiegeEvent();
                            break;
                        }
                        case WorldEventKind.HeroCaptured:
                        {
                            var prisoner = GameBridge.FindHero(message.A);
                            var captor = GameBridge.FindParty(message.B)?.Party ?? Find<Settlement>(message.B)?.Party;
                            if (prisoner == null || captor == null || !prisoner.IsAlive || prisoner.PartyBelongedToAsPrisoner == captor)
                                break;
                            // Players' heroes are captured only by their own game (their party would fall apart here).
                            if (Session.MpSession.Current?.IsPlayedHero(prisoner) == true)
                            {
                                Log.Info("Ignoring capture of a played hero " + message.A);
                                break;
                            }
                            if (prisoner.IsPrisoner)
                                EndCaptivityAction.ApplyByReleasedAfterBattle(prisoner); // Held by someone else here: move them.
                            TakePrisonerAction.Apply(captor, prisoner);
                            break;
                        }
                        case WorldEventKind.HeroReleased:
                        {
                            var hero = GameBridge.FindHero(message.A);
                            if (hero != null && hero.IsAlive && hero.IsPrisoner)
                                EndCaptivityAction.ApplyByReleasedAfterBattle(hero);
                            break;
                        }
                        case WorldEventKind.HeroKilled:
                        {
                            var victim = GameBridge.FindHero(message.A);
                            if (victim != null && victim.IsAlive)
                                KillCharacterAction.ApplyByBattle(victim, GameBridge.FindHero(message.B), false);
                            break;
                        }
                        case WorldEventKind.ArmyCreated:
                        case WorldEventKind.ArmyPartyJoined:
                        case WorldEventKind.ArmyPartyAttached:
                        case WorldEventKind.ArmyPartyLeft:
                        case WorldEventKind.ArmyDispersed:
                            ApplyArmyEvent(message);
                            break;
                    }
                }
                catch (Exception e)
                {
                    Log.Error("Could not apply world event " + message, e);
                }
            });
        }

        // ----- Armies -----------------------------------------------------------------------------------------

        /// <summary>
        /// Players' own armies. A player makes an army on their machine; the host makes the same army in the real
        /// world, where its AI walks the called parties to the player's party and attaches them. Each attachment
        /// comes back so the player's game attaches them too, and they move with the player. A client only ever
        /// changes its own army here: other armies are just parties the host moves.
        /// </summary>
        private static void ApplyArmyEvent(WorldEventMessage message)
        {
            var onClient = Session.MpSession.Current is Session.ClientSession;
            switch (message.Kind)
            {
                case WorldEventKind.ArmyCreated:
                {
                    // The client that proposed it made this army itself; only the host has to create it.
                    var leader = GameBridge.FindHero(message.A);
                    var leaderParty = leader?.PartyBelongedTo;
                    var kingdom = leader?.Clan?.Kingdom;
                    if (onClient || leaderParty == null || leaderParty.LeaderHero != leader || leaderParty.Army != null || kingdom == null)
                        break;
                    var parts = (message.B ?? string.Empty).Split('|');
                    var type = int.TryParse(parts[0], out var t) && Enum.IsDefined(typeof(Army.ArmyTypes), t) ? (Army.ArmyTypes)t : Army.ArmyTypes.Patrolling;
                    var target = parts.Length > 1 ? Find<Settlement>(parts[1]) : null;
                    KeepInfluence(leader.Clan, () => kingdom.CreateArmy(leader, target, type, new MBList<MobileParty>()));
                    Log.Info($"Army of {leader.StringId} created ({type}, target {target?.StringId ?? "none"}): {leaderParty.Army != null}");
                    break;
                }
                case WorldEventKind.ArmyPartyJoined:
                {
                    var party = GameBridge.FindParty(message.A);
                    var leaderParty = GameBridge.FindParty(message.B);
                    var army = leaderParty?.Army;
                    if (party == null || !party.IsActive || army == null || army.LeaderParty != leaderParty || party == leaderParty || party.Army == army)
                        break;
                    if (party.Army != null || (onClient && leaderParty != MobileParty.MainParty))
                        break;
                    KeepInfluence(leaderParty.ActualClan, () => party.Army = army);
                    if (onClient)
                        MakePuppet(party);
                    else if (party.MapEvent == null)
                        party.SetMoveEscortParty(leaderParty, MobileParty.NavigationType.Default, false);
                    break;
                }
                case WorldEventKind.ArmyPartyAttached:
                {
                    var party = GameBridge.FindParty(message.A);
                    var leaderParty = GameBridge.FindParty(message.B);
                    var army = leaderParty?.Army;
                    if (party == null || !party.IsActive || army == null || army.LeaderParty != leaderParty || party == leaderParty
                        || party.AttachedTo == leaderParty || party.MapEvent != null)
                        break;
                    if (onClient && leaderParty != MobileParty.MainParty)
                        break;
                    if (party.Army != army)
                    {
                        if (party.Army != null)
                            break;
                        KeepInfluence(leaderParty.ActualClan, () => party.Army = army);
                    }
                    army.AddPartyToMergedParties(party);
                    break;
                }
                case WorldEventKind.ArmyPartyLeft:
                {
                    var party = GameBridge.FindParty(message.A);
                    var leaderParty = GameBridge.FindParty(message.B);
                    var army = party?.Army;
                    // The leader leaving is the army breaking up, which arrives as its own event.
                    if (army == null || army.LeaderParty != leaderParty || party == leaderParty)
                        break;
                    if (onClient && leaderParty != MobileParty.MainParty)
                        break;
                    party.Army = null;
                    if (party.AttachedTo != null)
                        party.AttachedTo = null;
                    if (onClient)
                        MakePuppet(party);
                    break;
                }
                case WorldEventKind.ArmyDispersed:
                {
                    var leaderParty = GameBridge.FindParty(message.A);
                    var army = leaderParty?.Army;
                    if (army == null || army.LeaderParty != leaderParty || (onClient && leaderParty != MobileParty.MainParty))
                        break;
                    var members = army.Parties.Where(p => p != leaderParty).ToList();
                    DisbandArmyAction.ApplyByUnknownReason(army);
                    if (onClient)
                        members.ForEach(MakePuppet);
                    break;
                }
            }
        }

        /// <summary>Influence spent on an army is paid on the player's machine and arrives through the ledger, never twice.</summary>
        private static void KeepInfluence(Clan clan, Action action)
        {
            var influence = clan?.Influence ?? 0f;
            try
            {
                action();
            }
            finally
            {
                if (clan != null)
                    clan.Influence = influence;
            }
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
