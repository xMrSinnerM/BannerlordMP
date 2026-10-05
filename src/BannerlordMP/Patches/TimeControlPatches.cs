using BannerlordMP.Game;
using BannerlordMP.Session;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BannerlordMP.Patches
{
    /// <summary>
    /// While a session is running, the local game no longer decides the campaign speed: player input becomes a
    /// request to the host, and the session applies whatever the shared clock says.
    /// </summary>
    [HarmonyPatch(typeof(Campaign), nameof(Campaign.SetTimeSpeed))]
    internal static class SetTimeSpeedPatch
    {
        private static bool Prefix(int speed)
        {
            var session = MpSession.Current;
            if (session == null || GameBridge.ApplyingTime)
                return true;
            session.RequestSpeed(GameBridge.FromGameSpeed(speed));
            return false;
        }
    }

    [HarmonyPatch(typeof(Campaign), nameof(Campaign.TimeControlMode), MethodType.Setter)]
    internal static class TimeControlModeSetterPatch
    {
        private static bool Prefix()
        {
            // Block every change the game makes on its own (menus auto-pausing, encounters stopping time...).
            // The session re-applies the shared speed each frame through GameBridge.SetLocalTime.
            return MpSession.Current == null || GameBridge.ApplyingTime;
        }
    }
}
