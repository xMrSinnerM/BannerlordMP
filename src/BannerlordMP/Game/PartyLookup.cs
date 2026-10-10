using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordMP.Game
{
    /// <summary>StringId → party cache. Snapshots touch every party several times a second, so lookups must be cheap.</summary>
    internal sealed class PartyLookup
    {
        private readonly Dictionary<string, MobileParty> _parties = new Dictionary<string, MobileParty>();
        private double _lastRebuildSeconds = double.MinValue;

        public MobileParty Find(string stringId, double realTimeSeconds)
        {
            if (_parties.TryGetValue(stringId, out var party) && party.IsActive)
                return party;

            // Unknown id: the party may have spawned since the last rebuild. Rebuild at most once a second.
            if (realTimeSeconds - _lastRebuildSeconds < 1.0)
                return null;
            Rebuild(realTimeSeconds);
            return _parties.TryGetValue(stringId, out party) && party.IsActive ? party : null;
        }

        public void Rebuild(double realTimeSeconds)
        {
            _lastRebuildSeconds = realTimeSeconds;
            _parties.Clear();
            foreach (var party in MobileParty.All)
            {
                if (party?.StringId != null)
                    _parties[party.StringId] = party;
            }
        }
    }
}
