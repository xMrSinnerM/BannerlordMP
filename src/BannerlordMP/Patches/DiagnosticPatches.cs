using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BannerlordMP.Patches
{
    /// <summary>
    /// Error loggers only: these change nothing. When the game throws inside one of these methods, the full
    /// exception (with its stack trace) is written to BannerlordMP.log before the game handles or crashes on it.
    /// Covers the map's per-frame update and the leave-settlement path, where crashes were reported but the
    /// game's own log ended before the error.
    /// </summary>
    [HarmonyPatch]
    internal static class DiagnosticPatches
    {
        private static readonly (string Type, string Method)[] Targets =
        {
            ("TaleWorlds.CampaignSystem.GameState.MapState", "OnTick"),
            ("TaleWorlds.CampaignSystem.GameState.MapState", "OnMenuModeTick"),
            ("TaleWorlds.CampaignSystem.GameState.MapState", "OnMapModeTick"),
            ("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "LeaveSettlement"),
            ("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Finish"),
            ("TaleWorlds.CampaignSystem.Actions.LeaveSettlementAction", "ApplyForParty"),
            ("TaleWorlds.CampaignSystem.Actions.EnterSettlementAction", "ApplyForParty"),
            ("TaleWorlds.CampaignSystem.Campaign", "RealTick"),
        };

        private static string _lastLogged;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var (typeName, methodName) in Targets)
            {
                var type = AccessTools.TypeByName(typeName);
                var method = type == null ? null : AccessTools.Method(type, methodName);
                if (method != null)
                    yield return method;
                else
                    Log.Info($"Diagnostics: {typeName}.{methodName} not found");
            }
        }

        private static Exception Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null)
                return null;
            // A failing per-frame method would log every frame; log each distinct error once.
            var text = __exception.ToString();
            if (text != _lastLogged)
            {
                _lastLogged = text;
                Log.Error($"Game error in {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name}", __exception);
            }
            return __exception; // Unchanged: the game still sees its own exception.
        }
    }
}
