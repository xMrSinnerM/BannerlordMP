namespace BannerlordMP.Game
{
    internal static class WorldAuthority
    {
        /// <summary>
        /// True on a joined client: its campaign mirrors the host's world, so its own world simulation
        /// (periodic ticks, AI thinking) is switched off by <see cref="Patches.WorldSimulationPatches"/>.
        /// </summary>
        public static bool ClientMirroring { get; set; }
    }
}
