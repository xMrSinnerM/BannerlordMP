using BannerlordMP.Session;
using TaleWorlds.CampaignSystem;

namespace BannerlordMP.Game
{
    /// <summary>
    /// On the host, every joined player's clan looks like an ordinary AI clan to the game, so its AI would make
    /// decisions for it (join or leave kingdoms, marry off the leader, declare war...). This tells the protection
    /// patches which clans belong to players. Changes a player made themselves arrive from their client and are
    /// applied with <see cref="WorldBridge.ApplyingRemote"/> set, so they pass.
    /// </summary>
    internal static class PlayerClanGuard
    {
        /// <summary>True when the host's AI must not change this clan on its own.</summary>
        public static bool Protects(Clan clan)
        {
            return clan != null
                && !WorldBridge.ApplyingRemote
                && MpSession.Current is HostSession host
                && host.IsPlayerClan(clan);
        }

        public static bool Protects(Hero hero) => hero != null && Protects(hero.Clan);

        /// <summary>A faction a player decides for: their independent clan, or a kingdom their clan rules.</summary>
        public static bool Protects(IFaction faction)
        {
            switch (faction)
            {
                case Clan clan:
                    return clan.Kingdom == null && Protects(clan);
                case Kingdom kingdom:
                    return Protects(kingdom.RulingClan);
                default:
                    return false;
            }
        }

        public static void Blocked(string what)
        {
            Log.Info("Blocked AI decision for a player clan: " + what);
        }
    }
}
