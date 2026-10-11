using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Game;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordMP.Session
{
    /// <summary>
    /// Battles with friends. A battle is fought on one machine only, so when our battle starts, other players'
    /// parties right next to us that are at war with our enemy join it on our side, as single player's nearby allies
    /// join a battle. Our game's AI commands their troops. Afterwards their losses go to the server, which takes them
    /// from their party; their own game learns it like any other change to their party.
    /// </summary>
    internal sealed partial class ClientSession
    {
        /// <summary>How close (map units) another player's party must be to fight beside us.</summary>
        private const float AllyJoinDistance = 3f;
        /// <summary>Allies in our current battle, and their troops when it started.</summary>
        private readonly Dictionary<string, string> _allyTroopsAtStart = new Dictionary<string, string>();

        public override void OnLocalMapEventStarted(MapEvent mapEvent)
        {
            var main = MobileParty.MainParty;
            if (!_welcomed || WorldBridge.ApplyingRemote || main == null)
                return;
            try
            {
                var ourSide = main.Party.MapEventSide ?? main.AttachedTo?.Party?.MapEventSide;
                if (ourSide == null || ourSide.MapEvent != mapEvent)
                    return;
                JoinNearbyPlayers(main, ourSide);
            }
            catch (Exception e)
            {
                Log.Error("Could not bring nearby players into our battle", e);
            }
        }

        private void JoinNearbyPlayers(MobileParty main, MapEventSide ourSide)
        {
            _allyTroopsAtStart.Clear();
            var enemy = ourSide.OtherSide?.MapFaction;
            if (enemy == null)
                return;
            var center = main.Position.ToVec2();
            foreach (var party in MobileParty.All.ToList())
            {
                if (party == null || party == main || !party.IsActive || !IsPlayerParty(party) || party.MapEvent != null || party.CurrentSettlement != null)
                    continue;
                if (party.Position.ToVec2().DistanceSquared(center) > AllyJoinDistance * AllyJoinDistance || party.MemberRoster.TotalHealthyCount <= 0)
                    continue;
                // On our side only if they are at war with our enemy and not with us.
                if (party.MapFaction == null || !FactionManager.IsAtWarAgainstFaction(party.MapFaction, enemy)
                    || FactionManager.IsAtWarAgainstFaction(party.MapFaction, main.MapFaction))
                    continue;
                _allyTroopsAtStart[party.StringId] = StateSync.DescribeRoster(party.MemberRoster);
                WorldBridge.Remote(() => party.Party.MapEventSide = ourSide);
                var name = Players.Values.FirstOrDefault(p => p.PartyId == party.StringId)?.Name ?? party.Name?.ToString();
                Log.Notify($"{name}'s party is close by and joins the battle on your side.");
            }
        }

        /// <summary>After our battle: what our allies lost, for the server to take from their parties.</summary>
        private void ReportAllyLosses()
        {
            foreach (var pair in _allyTroopsAtStart)
            {
                var party = GameBridge.FindParty(pair.Key);
                var change = StateSync.RosterChange(pair.Value, StateSync.DescribeRoster(party?.MemberRoster));
                if (change.Length > 0)
                    OnLocalWorldEvent(WorldEventKind.AllyBattleLosses, pair.Key, change);
            }
            _allyTroopsAtStart.Clear();
        }
    }
}
