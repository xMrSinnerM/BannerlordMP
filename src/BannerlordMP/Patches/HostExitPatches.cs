using BannerlordMP.Session;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace BannerlordMP.Patches
{
    /// <summary>
    /// "Exit to main menu" on a server: save the world first, so nothing since the last autosave is lost. The
    /// exit is held back and happens as soon as the save is written (see HostSession.SaveBeforeExit).
    /// </summary>
    [HarmonyPatch(typeof(MBGameManager), nameof(MBGameManager.EndGame))]
    internal static class HostExitPatches
    {
        private static bool Prefix() => !(MpSession.Current is HostSession host && host.SaveBeforeExit());
    }
}
