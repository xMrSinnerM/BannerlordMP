using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Game;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;

namespace BannerlordMP.Session
{
    /// <summary>
    /// Kingdom votes for player clans. On the host a player's clan would be voted for by the AI; instead each
    /// pending decision of their kingdom is sent to the player, and their answer replaces the AI's at election
    /// time (see Patches/KingdomVotePatches). A player who does not answer abstains.
    /// </summary>
    internal sealed partial class HostSession
    {
        private const float DecisionScanInterval = 5f;

        private sealed class TrackedDecision
        {
            public int Id;
            public KingdomDecision Decision;
            public Kingdom Kingdom;
            public readonly List<string> OptionTitles = new List<string>();
            public readonly HashSet<int> OfferedTo = new HashSet<int>();
            /// <summary>Clan id → (chosen option title or null to abstain, weight).</summary>
            public readonly Dictionary<string, (string Title, VoteWeight Weight)> Votes = new Dictionary<string, (string, VoteWeight)>();
        }

        private readonly Dictionary<KingdomDecision, TrackedDecision> _decisions = new Dictionary<KingdomDecision, TrackedDecision>();
        private readonly Dictionary<int, TrackedDecision> _decisionsById = new Dictionary<int, TrackedDecision>();
        private int _nextDecisionId = 1;
        private float _decisionTimer;

        /// <summary>The player's vote for this decision, if they cast one.</summary>
        public bool TryGetVote(KingdomDecision decision, Clan clan, out string optionTitle, out VoteWeight weight)
        {
            optionTitle = null;
            weight = VoteWeight.Abstain;
            if (decision == null || clan == null || !_decisions.TryGetValue(decision, out var tracked)
                || !tracked.Votes.TryGetValue(clan.StringId, out var vote))
                return false;
            optionTitle = vote.Title;
            weight = vote.Weight;
            return true;
        }

        private void TickDecisions(float dt)
        {
            _decisionTimer += dt;
            if (_decisionTimer < DecisionScanInterval)
                return;
            _decisionTimer = 0;

            foreach (var pair in _peers.ToList())
            {
                var playerId = pair.Value.PlayerId;
                if (playerId < 0 || !Players.TryGetValue(playerId, out var player))
                    continue;
                var clan = GameBridge.FindHero(player.HeroId)?.Clan;
                var kingdom = clan?.Kingdom;
                if (kingdom == null)
                    continue;
                foreach (var decision in kingdom.UnresolvedDecisions.ToList())
                {
                    var tracked = Track(decision, kingdom);
                    if (tracked.OfferedTo.Add(playerId))
                        OfferDecision(pair.Key, tracked, clan);
                }
            }

            // Forget decisions that have been resolved or cancelled.
            foreach (var tracked in _decisions.Values.Where(t => !t.Kingdom.UnresolvedDecisions.Contains(t.Decision)).ToList())
            {
                _decisions.Remove(tracked.Decision);
                _decisionsById.Remove(tracked.Id);
            }
        }

        private TrackedDecision Track(KingdomDecision decision, Kingdom kingdom)
        {
            if (_decisions.TryGetValue(decision, out var tracked))
                return tracked;
            tracked = new TrackedDecision { Id = _nextDecisionId++, Decision = decision, Kingdom = kingdom };
            _decisions[decision] = tracked;
            _decisionsById[tracked.Id] = tracked;
            return tracked;
        }

        private void OfferDecision(int peer, TrackedDecision tracked, Clan clan)
        {
            var decision = tracked.Decision;
            try
            {
                // The same object the game's own decision screen builds: it lists the outcomes to choose from.
                var election = new KingdomElection(decision);
                tracked.OptionTitles.Clear();
                var request = new DecisionVoteRequestMessage
                {
                    DecisionId = tracked.Id,
                    KingdomName = tracked.Kingdom.Name?.ToString() ?? string.Empty,
                    Title = decision.GetGeneralTitle()?.ToString() ?? decision.GetType().Name,
                    IsRuler = decision.DetermineChooser() == clan,
                    DaysLeft = Math.Max(0f, decision.TriggerTime.RemainingDaysFromNow),
                };
                request.Description = (request.IsRuler ? decision.GetChooseDescription() : decision.GetSupportDescription())?.ToString() ?? string.Empty;
                foreach (var outcome in election.PossibleOutcomes)
                {
                    var title = outcome.GetDecisionTitle()?.ToString() ?? string.Empty;
                    tracked.OptionTitles.Add(title);
                    request.Options.Add(new DecisionOption { Title = title, Description = outcome.GetDecisionDescription()?.ToString() ?? string.Empty });
                }
                foreach (var weight in new[] { Supporter.SupportWeights.SlightlyFavor, Supporter.SupportWeights.StronglyFavor, Supporter.SupportWeights.FullyPush })
                    request.WeightCosts.Add(decision.GetInfluenceCostOfSupport(clan, weight));
                Net.Send(peer, request);
                Log.Info($"Offered decision {tracked.Id} ({request.Title}) to clan {clan.StringId}");
            }
            catch (Exception e)
            {
                Log.Error($"Could not offer kingdom decision {decision.GetType().Name} to {clan.StringId}", e);
            }
        }

        private void HandleVote(int playerId, DecisionVoteMessage vote)
        {
            var clan = GameBridge.FindHero(Players[playerId].HeroId)?.Clan;
            if (clan == null || !_decisionsById.TryGetValue(vote.DecisionId, out var tracked) || tracked.Kingdom != clan.Kingdom)
                return;
            var title = vote.Weight != VoteWeight.Abstain && vote.OptionIndex >= 0 && vote.OptionIndex < tracked.OptionTitles.Count
                ? tracked.OptionTitles[vote.OptionIndex]
                : null;
            tracked.Votes[clan.StringId] = (title, title == null ? VoteWeight.Abstain : vote.Weight);
            Log.Info($"{NameOf(playerId)} voted on decision {tracked.Id}: {(title == null ? "abstain" : title + " (" + vote.Weight + ")")}");
        }

        public static Supporter.SupportWeights ToGameWeight(VoteWeight weight)
        {
            switch (weight)
            {
                case VoteWeight.SlightlyFavor:
                    return Supporter.SupportWeights.SlightlyFavor;
                case VoteWeight.StronglyFavor:
                    return Supporter.SupportWeights.StronglyFavor;
                case VoteWeight.FullyPush:
                    return Supporter.SupportWeights.FullyPush;
                case VoteWeight.Choose:
                    return Supporter.SupportWeights.Choose;
                default:
                    return Supporter.SupportWeights.StayNeutral;
            }
        }
    }
}
