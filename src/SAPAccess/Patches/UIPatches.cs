using BepInEx.Logging;
using HarmonyLib;
using SAPAccess.NVDA;

namespace SAPAccess.Patches;

/// <summary>
/// Harmony hooks for UI text changes.
/// Intercepts TextMeshPro text updates for screen reader output.
/// </summary>
[HarmonyPatch]
public static class UIPatches
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("SAPAccess.UIPatch");

    // NOTE: Hooking TMP_Text.set_text is very high-traffic and may cause perf issues.
    // We hook specific UI components instead (HangarGold.SetGold, HangarLives.Set, etc.)
    // and only hook PopupManager for popup text. Additional text hooks can be added as needed.
}
