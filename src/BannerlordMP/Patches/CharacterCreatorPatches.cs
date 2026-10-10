using BannerlordMP.Ui;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BannerlordMP.Patches
{
    /// <summary>
    /// The character creator's campaign only exists to make a hero and is thrown away right after: it must never
    /// write a save (autosave, ironman or otherwise) over the player's own campaigns.
    /// </summary>
    [HarmonyPatch(typeof(SaveHandler), "SaveTick")]
    internal static class CharacterCreatorPatches
    {
        [HarmonyPrefix]
        private static bool Prefix() => !CharacterCreator.Active;
    }
}
