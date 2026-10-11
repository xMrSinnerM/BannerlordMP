using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BannerlordMP.Core.Protocol;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements.Workshops;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.TournamentGames;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.ObjectSystem;

namespace BannerlordMP.Game
{
    /// <summary>
    /// Things a player sets in their own game that the server must have too: troops left in a garrison, prisoners
    /// left in a dungeon, how their fiefs are run, their kingdom's policies, their party's roles, relations,
    /// marriages, raided villages, companions, workshops, caravans. Each is described as text; the deciding machine
    /// sends the description (or what changed) when it changes (<see cref="WorldEventKind"/> 19–28) and the other side applies it.
    /// </summary>
    internal static class StateSync
    {
        private static string Join(params object[] fields) =>
            string.Join(WorldEventMessage.FieldSeparator.ToString(), fields.Select(f => Convert.ToString(f, CultureInfo.InvariantCulture) ?? string.Empty));

        private static string[] Fields(string text, int count)
        {
            var fields = (text ?? string.Empty).Split(WorldEventMessage.FieldSeparator);
            if (fields.Length < count)
                Array.Resize(ref fields, count);
            for (var i = 0; i < fields.Length; i++)
                fields[i] = fields[i] ?? string.Empty;
            return fields;
        }

        private static float Number(string text, float fallback) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

        // ----- Garrisons and dungeons -------------------------------------------------------------------------

        public const string Garrison = "g";
        public const string Prison = "p";

        public static TroopRoster Roster(Settlement settlement, string which)
        {
            if (settlement == null || !(settlement.IsTown || settlement.IsCastle))
                return null;
            return which == Garrison ? settlement.Town?.GarrisonParty?.MemberRoster : settlement.Party?.PrisonRoster;
        }


        /// <summary>Regular troops only, sorted, so equal rosters give equal text.</summary>
        public static string DescribeRoster(TroopRoster roster)
        {
            if (roster == null)
                return string.Empty;
            return string.Join(";", roster.GetTroopRoster()
                .Where(e => e.Character != null && !e.Character.IsHero && e.Number > 0)
                .OrderBy(e => e.Character.StringId, StringComparer.Ordinal)
                .Select(e => $"{e.Character.StringId}:{e.Number}:{e.WoundedNumber}"));
        }

        private static Dictionary<string, (int Count, int Wounded)> ParseRoster(string description)
        {
            var troops = new Dictionary<string, (int, int)>();
            foreach (var part in (description ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = part.Split(':');
                if (bits.Length >= 3 && int.TryParse(bits[1], out var count) && int.TryParse(bits[2], out var wounded))
                    troops[bits[0]] = (count, wounded);
            }
            return troops;
        }

        /// <summary>
        /// What changed between two <see cref="DescribeRoster"/> texts, as "id:count:wounded" changes. Changes, not the
        /// whole roster: the server's garrison or dungeon may have changed meanwhile (wages, ransoms, the AI).
        /// </summary>
        public static string RosterChange(string before, string after)
        {
            var old = ParseRoster(before);
            var now = ParseRoster(after);
            var changes = new List<string>();
            foreach (var id in old.Keys.Union(now.Keys).OrderBy(id => id, StringComparer.Ordinal))
            {
                old.TryGetValue(id, out var a);
                now.TryGetValue(id, out var b);
                if (a.Count != b.Count || a.Wounded != b.Wounded)
                    changes.Add($"{id}:{b.Count - a.Count}:{b.Wounded - a.Wounded}");
            }
            return string.Join(";", changes);
        }

        public static string DescribeRosterChange(string which, string change) => Join(which, change);

        /// <summary>Applies <see cref="RosterChange"/> text to a roster, never below zero.</summary>
        public static void ApplyRosterChange(TroopRoster roster, string change)
        {
            if (roster == null)
                return;
            foreach (var pair in ParseRoster(change))
            {
                var character = MBObjectManager.Instance.GetObject<CharacterObject>(pair.Key);
                if (character == null || character.IsHero)
                    continue;
                var index = roster.FindIndexOfTroop(character);
                var have = index >= 0 ? roster.GetElementNumber(index) : 0;
                var haveWounded = index >= 0 ? roster.GetElementWoundedNumber(index) : 0;
                var count = Math.Max(-have, pair.Value.Count);
                var wounded = Math.Max(-haveWounded, Math.Min(pair.Value.Wounded, have + count - haveWounded));
                if (count != 0 || wounded != 0)
                    roster.AddToCounts(character, count, false, wounded, 0, true, -1);
            }
        }

        public static void ApplySettlementRosterChange(Settlement settlement, string description)
        {
            var fields = Fields(description, 2);
            var which = fields[0];
            if (settlement == null || (which != Garrison && which != Prison) || !(settlement.IsTown || settlement.IsCastle))
                return;
            var changes = ParseRoster(fields[1]);
            if (changes.Count == 0)
                return;
            if (which == Garrison && settlement.Town.GarrisonParty == null)
                settlement.AddGarrisonParty();
            var roster = Roster(settlement, which);
            if (roster == null)
                return;
            ApplyRosterChange(roster, fields[1]);
            Log.Info($"{(which == Garrison ? "Garrison" : "Dungeon")} of {settlement.StringId} changed by its owner's side ({fields[1]}); now {roster.TotalManCount} men");
        }

        // ----- Fiefs ------------------------------------------------------------------------------------------

        /// <summary>
        /// Governor, building queue, daily project, garrison wage limit, garrison auto-recruitment: what the owner
        /// decides. With <paramref name="full"/> also what the server's world works out (building levels and progress,
        /// loyalty, security, prosperity, food).
        /// </summary>
        public static string DescribeFief(Town town, bool full)
        {
            if (town == null)
                return null;
            var queue = string.Join(",", town.BuildingsInProgress.Where(b => b?.BuildingType != null).Select(b => b.BuildingType.StringId));
            var current = town.Buildings.FirstOrDefault(b => b != null && b.IsCurrentlyDefault)?.BuildingType?.StringId;
            var choices = Join(town.Governor?.StringId, queue, current, town.Settlement.GarrisonWagePaymentLimit, town.GarrisonAutoRecruitmentIsEnabled ? 1 : 0);
            if (!full)
                return choices;
            var levels = string.Join(",", town.Buildings.Where(b => b?.BuildingType != null)
                .Select(b => $"{b.BuildingType.StringId}:{b.CurrentLevel}:{b.BuildingProgress.ToString("0.#", CultureInfo.InvariantCulture)}"));
            return Join(choices, levels, Round(town.Loyalty), Round(town.Security), Round(town.Prosperity), Round(town.FoodStocks));
        }

        private static string Round(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        public static void ApplyFief(Town town, string description, bool full)
        {
            if (town == null)
                return;
            var fields = Fields(description, 10);

            var governor = GameBridge.FindHero(fields[0]);
            if (governor != town.Governor)
            {
                if (governor == null)
                    ChangeGovernorAction.RemoveGovernorOfIfExists(town);
                else if (governor.IsAlive && !governor.IsPrisoner && governor.Clan == town.OwnerClan)
                {
                    // The governor lives in the town; in their owner's game they left their party for it.
                    var party = governor.PartyBelongedTo;
                    if (party != null && party.LeaderHero != governor)
                        party.MemberRoster.AddToCounts(governor.CharacterObject, -1, false, 0, 0, true, -1);
                    ChangeGovernorAction.Apply(town, governor);
                }
            }

            var queue = fields[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => town.Buildings.FirstOrDefault(b => b?.BuildingType?.StringId == id)).Where(b => b != null).ToList();
            if (!queue.SequenceEqual(town.BuildingsInProgress))
            {
                town.BuildingsInProgress.Clear();
                foreach (var building in queue)
                    town.BuildingsInProgress.Enqueue(building);
            }
            if (!string.IsNullOrEmpty(fields[2]))
            {
                foreach (var building in town.Buildings.Where(b => b?.BuildingType != null))
                    building.IsCurrentlyDefault = building.BuildingType.StringId == fields[2];
            }
            if (int.TryParse(fields[3], out var wageLimit) && wageLimit != town.Settlement.GarrisonWagePaymentLimit)
                town.Settlement.SetGarrisonWagePaymentLimit(wageLimit);
            town.GarrisonAutoRecruitmentIsEnabled = fields[4] == "1";

            if (!full)
                return;
            foreach (var part in fields[5].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = part.Split(':');
                var building = bits.Length >= 3 ? town.Buildings.FirstOrDefault(b => b?.BuildingType?.StringId == bits[0]) : null;
                if (building == null || !int.TryParse(bits[1], out var level))
                    continue;
                building.CurrentLevel = level;
                building.BuildingProgress = Number(bits[2], building.BuildingProgress);
            }
            town.Loyalty = Number(fields[6], town.Loyalty);
            town.Security = Number(fields[7], town.Security);
            town.Prosperity = Number(fields[8], town.Prosperity);
            town.FoodStocks = Number(fields[9], town.FoodStocks);
        }

        // ----- Kingdom policies -------------------------------------------------------------------------------

        public static string DescribePolicies(Kingdom kingdom) =>
            kingdom == null ? null : string.Join(",", kingdom.ActivePolicies.Where(p => p != null).Select(p => p.StringId).OrderBy(id => id, StringComparer.Ordinal));

        public static void ApplyPolicies(Kingdom kingdom, string description)
        {
            if (kingdom == null)
                return;
            var wanted = new HashSet<string>((description ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            foreach (var policy in kingdom.ActivePolicies.Where(p => p != null && !wanted.Contains(p.StringId)).ToList())
                kingdom.RemovePolicy(policy);
            foreach (var id in wanted)
            {
                var policy = WorldBridge.Find<PolicyObject>(id);
                if (policy != null && !kingdom.HasPolicy(policy))
                    kingdom.AddPolicy(policy);
            }
        }

        // ----- Party roles ------------------------------------------------------------------------------------

        public static string DescribeRoles(MobileParty party)
        {
            if (party == null)
                return null;
            string Id(Hero hero) => hero == null || hero == party.LeaderHero ? string.Empty : hero.StringId;
            return string.Join(",", Id(party.EffectiveScout), Id(party.EffectiveQuartermaster), Id(party.EffectiveSurgeon), Id(party.EffectiveEngineer));
        }

        public static void ApplyRoles(MobileParty party, string description)
        {
            if (party == null)
                return;
            var ids = ((description ?? string.Empty) + ",,,").Split(',');
            Hero Member(string id)
            {
                var hero = GameBridge.FindHero(id);
                return hero != null && hero.PartyBelongedTo == party ? hero : null;
            }
            party.SetPartyScout(Member(ids[0]));
            party.SetPartyQuartermaster(Member(ids[1]));
            party.SetPartySurgeon(Member(ids[2]));
            party.SetPartyEngineer(Member(ids[3]));
        }

        // ----- Companions -------------------------------------------------------------------------------------

        public static string DescribeCompanion(Hero hero) =>
            hero == null ? null : string.Join(";", WorldBridge.CaptureCompanion(hero).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

        public static Dictionary<string, int> ParseCounters(string text)
        {
            var counters = new Dictionary<string, int>();
            foreach (var part in (text ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = part.LastIndexOf('=');
                if (equals > 0 && int.TryParse(part.Substring(equals + 1), out var value))
                    counters[part.Substring(0, equals)] = value;
            }
            return counters;
        }

        /// <summary>What changed between two <see cref="DescribeCompanion"/> texts, in the same "key=change" form.</summary>
        public static string CounterChange(string before, string after)
        {
            var old = ParseCounters(before);
            var now = ParseCounters(after);
            return string.Join(";", old.Keys.Union(now.Keys).OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => (Key: k, Change: (now.TryGetValue(k, out var b) ? b : 0) - (old.TryGetValue(k, out var a) ? a : 0)))
                .Where(c => c.Change != 0).Select(c => c.Key + "=" + c.Change));
        }

        public static void ApplyCompanion(Hero hero, string change)
        {
            var delta = ParseCounters(change);
            if (hero == null || delta.Count == 0)
                return;
            WorldBridge.ApplyCompanionDelta(hero, delta);
        }

        // ----- Workshops and caravans --------------------------------------------------------------------------

        public static string DescribeWorkshop(Workshop workshop) =>
            workshop?.Settlement == null ? null : Join(workshop.Tag, workshop.Owner?.StringId, workshop.WorkshopType?.StringId, workshop.Capital);

        public static Workshop FindWorkshop(Settlement settlement, string tag) =>
            settlement?.Town?.Workshops?.FirstOrDefault(w => w != null && w.Tag == tag);

        /// <summary>Who owns a workshop and what it makes: bought, sold, changed or lost to a war or bankruptcy.</summary>
        public static void ApplyWorkshop(Settlement settlement, string description)
        {
            var fields = Fields(description, 4);
            var workshop = FindWorkshop(settlement, fields[0]);
            var owner = GameBridge.FindHero(fields[1]);
            var type = WorkshopType.Find(fields[2]);
            if (workshop == null || owner == null || type == null)
                return;
            if (workshop.Owner != owner)
            {
                var capital = int.TryParse(fields[3], out var c) ? c : workshop.Capital;
                workshop.ChangeOwnerOfWorkshop(owner, type, capital);
                Log.Info($"Workshop {fields[0]} in {settlement.StringId} now belongs to {owner.StringId} ({type.StringId})");
            }
            else if (workshop.WorkshopType != type)
                workshop.ChangeWorkshopProduction(type);
        }

        public static string DescribeCaravan(MobileParty caravan)
        {
            var component = caravan?.CaravanPartyComponent;
            if (component == null)
                return null;
            return Join(component.Owner?.StringId, caravan.LeaderHero?.StringId, (caravan.HomeSettlement ?? component.Settlement)?.StringId,
                component.IsElite ? 1 : 0, DescribeRoster(caravan.MemberRoster));
        }

        /// <summary>
        /// Host: a player sent a caravan out in their game. The real one is made here with that id; the server's AI
        /// trades with it, as single player's AI runs the player's caravans.
        /// </summary>
        /// <returns>Null when created, otherwise why not.</returns>
        public static string CreateCaravan(string partyId, string description, Clan playerClan)
        {
            var fields = Fields(description, 5);
            var owner = GameBridge.FindHero(fields[0]);
            var leader = GameBridge.FindHero(fields[1]);
            var home = WorldBridge.Find<Settlement>(fields[2]);
            if (GameBridge.FindParty(partyId) != null)
                return null;
            if (owner == null || owner.Clan != playerClan || home == null)
                return "unknown owner or town";
            if (leader != null && (leader.Clan != playerClan || !leader.IsAlive || leader.IsPrisoner || leader.PartyBelongedTo?.LeaderHero == leader))
                return "its leader is not a free hero of their clan";
            var culture = home.Culture ?? owner.Culture;
            var elite = fields[3] == "1";
            var template = (elite ? culture?.EliteCaravanPartyTemplates : culture?.CaravanPartyTemplates)?.FirstOrDefault()
                           ?? culture?.CaravanPartyTemplates?.FirstOrDefault();
            if (template == null)
                return "no caravan template for that culture";
            // The leader usually still rides with the player here.
            if (leader?.PartyBelongedTo != null)
                leader.PartyBelongedTo.MemberRoster.AddToCounts(leader.CharacterObject, -1, false, 0, 0, true, -1);
            var caravan = CaravanPartyComponent.CreateCaravanParty(owner, home, template, false, leader, null, elite);
            if (caravan == null)
                return "the game refused";
            if (caravan.StringId != partyId)
                caravan.StringId = partyId;
            var troops = new List<TroopCount>();
            foreach (var pair in ParseRoster(fields[4]))
                troops.Add(new TroopCount(pair.Key, pair.Value.Count, pair.Value.Wounded));
            if (troops.Count > 0)
                GameBridge.ApplyRoster(caravan.MemberRoster, troops);
            Log.Info($"Created caravan {partyId} for {owner.StringId} from {home.StringId}, led by {leader?.StringId ?? "a caravan master"}, {caravan.MemberRoster.TotalManCount} men");
            return null;
        }

        // ----- Tournaments ------------------------------------------------------------------------------------

        public static void StartTournament(Town town)
        {
            var manager = Campaign.Current?.TournamentManager;
            if (town == null || manager == null || manager.GetTournamentGame(town) != null)
                return;
            manager.AddTournament(new FightTournamentGame(town));
        }

        private static readonly System.Reflection.FieldInfo Leaderboard =
            HarmonyLib.AccessTools.Field(typeof(TournamentManager), "_worldWideTournamentLeaderboard");

        /// <summary>
        /// A tournament is over: it is taken off the town (otherwise a player could win the same one again, or the AI
        /// would hold it once more). A player who won it gets the win on the leaderboard; prize, renown and influence
        /// travel through their own ledger.
        /// </summary>
        public static void EndTournament(Town town, Hero winner, bool countWin)
        {
            var manager = Campaign.Current?.TournamentManager as TournamentManager;
            var game = town == null ? null : Campaign.Current?.TournamentManager?.GetTournamentGame(town);
            if (game == null)
                return;
            if (manager == null)
                return;
            manager.RemoveTournament(game);
            if (countWin && winner != null && manager != null && Leaderboard?.GetValue(manager) is Dictionary<Hero, int> board)
            {
                board.TryGetValue(winner, out var wins);
                board[winner] = wins + 1;
            }
            Log.Info($"Tournament in {town.Settlement.StringId} is over{(winner != null ? ", won by " + winner.StringId : string.Empty)}");
        }

        // ----- Quests -----------------------------------------------------------------------------------------

        /// <summary>
        /// A player took this hero's issue as a quest in their game. Here the issue is taken off the hero, so nobody
        /// else is offered it and the AI doesn't solve it meanwhile. The quest itself runs in the player's game.
        /// </summary>
        public static void TakeIssue(Hero giver, string title)
        {
            var manager = Campaign.Current?.IssueManager;
            if (giver == null || manager == null || !manager.Issues.TryGetValue(giver, out var issue) || issue == null || issue.IssueQuest != null)
                return;
            manager.DeactivateIssue(issue);
            Log.Info($"{giver.StringId}'s issue was taken by a player as a quest ({title}); removed it here");
        }

        private static readonly int[] QuestTick = { -1, -1, -1 };

        /// <summary>
        /// Client: quests run in the player's game, but its hourly and daily turns are off. Quests' own turns (time
        /// limits, alternative solutions sent with a companion) are given here, as single player gives them.
        /// </summary>
        public static void TickQuests(double hours)
        {
            var campaign = Campaign.Current;
            if (campaign?.QuestManager == null)
                return;
            var hour = (int)Math.Floor(hours);
            if (QuestTick[0] < 0)
            {
                QuestTick[0] = hour;
                QuestTick[1] = hour / 24;
                QuestTick[2] = hour / (24 * 7);
                return;
            }
            // A big jump (a skipped catch-up) gives one turn, not one per missed hour.
            if (hour != QuestTick[0])
            {
                QuestTick[0] = hour;
                Safely("hourly", () =>
                {
                    campaign.QuestManager.HourlyTick();
                    campaign.IssueManager?.HourlyTick();
                });
            }
            if (hour / 24 != QuestTick[1])
            {
                QuestTick[1] = hour / 24;
                Safely("daily", () => campaign.QuestManager.DailyTick());
            }
            if (hour / (24 * 7) != QuestTick[2])
            {
                QuestTick[2] = hour / (24 * 7);
                Safely("weekly", () => campaign.QuestManager.WeeklyTick());
            }
        }

        private static readonly HashSet<string> QuestTickFailed = new HashSet<string>();

        private static void Safely(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                if (QuestTickFailed.Add(what))
                    Log.Error($"Quests' {what} turn failed", e);
            }
        }

        // ----- Relations, marriages, villages -----------------------------------------------------------------

        public static string DescribeRelation(Hero hero, Hero other) => Join(other.StringId, CharacterRelationManager.GetHeroRelation(hero, other));

        public static void ApplyRelation(Hero hero, string description)
        {
            var fields = Fields(description, 2);
            var other = GameBridge.FindHero(fields[0]);
            if (hero == null || other == null || hero == other || !int.TryParse(fields[1], out var value))
                return;
            if (CharacterRelationManager.GetHeroRelation(hero, other) != value)
                CharacterRelationManager.SetHeroRelation(hero, other, value);
        }

        public static void ApplyMarriage(Hero hero, Hero other)
        {
            if (hero == null || other == null || hero == other || !hero.IsAlive || !other.IsAlive || hero.Spouse == other)
                return;
            MarriageAction.Apply(hero, other, false);
            Log.Info($"{hero.StringId} and {other.StringId} married");
        }

        /// <summary>Only the end states travel: a raid in progress is a battle on one machine, the others see its outcome.</summary>
        public static bool IsSharedVillageState(Village.VillageStates state) =>
            state == Village.VillageStates.Normal || state == Village.VillageStates.Looted;

        public static string DescribeVillageState(Village.VillageStates state, MobileParty raider) => Join(state, raider?.StringId);

        public static void ApplyVillageState(Settlement settlement, string description)
        {
            var village = settlement?.Village;
            var fields = Fields(description, 2);
            if (village == null || !Enum.TryParse(fields[0], out Village.VillageStates state) || !IsSharedVillageState(state) || village.VillageState == state)
                return;
            if (state == Village.VillageStates.Normal && Session.MpSession.Current is Session.ClientSession)
            {
                village.VillageState = state;
                return;
            }
            if (state == Village.VillageStates.Normal)
            {
                ChangeVillageStateAction.ApplyBySettingToNormal(settlement);
                return;
            }
            // The server runs what follows a raid (relations, recovery); a client only shows the village burnt.
            var raider = Session.MpSession.Current is Session.ClientSession ? null : GameBridge.FindParty(fields[1]);
            if (raider != null)
                ChangeVillageStateAction.ApplyBySettingToLooted(settlement, raider);
            else
                village.VillageState = state;
        }
    }
}
