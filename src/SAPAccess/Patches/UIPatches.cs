using BepInEx.Logging;
using SAPAccess.NVDA;

namespace SAPAccess.Patches;

/// <summary>
/// Harmony hooks for generic UI elements (Label, ButtonBase, etc.).
/// Intercepts text updates to provide screen reader output for UI changes.
///
/// NOTE: Exact method signatures will be determined after running Il2CppDumper.
/// </summary>
public static class UIPatches
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("SAPAccess.UIPatch");

    /// <summary>Called when a Label's text is set.</summary>
    public static void OnLabelTextSet(string text)
    {
        // Only log; actual announcements are handled by context-specific announcers.
        Log.LogDebug($"Label text: {text}");
    }

    /// <summary>Called when a button becomes focused/highlighted.</summary>
    public static void OnButtonFocused(string buttonText)
    {
        Log.LogDebug($"Button focused: {buttonText}");
    }

    /// <summary>Called when a tooltip or hover text appears.</summary>
    public static void OnTooltipShown(string text)
    {
        Log.LogDebug($"Tooltip: {text}");
        ScreenReader.Instance.SayQueued(text);
    }

    // =========================================================================
    // Harmony patch methods will be wired up once interop DLLs are available.
    // Example:
    //
    // [HarmonyPatch(typeof(Il2Cpp.Spacewood.Unity.Label), "set_Text")]
    // [HarmonyPostfix]
    // public static void Label_SetText_Postfix(string value)
    // {
    //     OnLabelTextSet(value);
    // }
    // =========================================================================
}
