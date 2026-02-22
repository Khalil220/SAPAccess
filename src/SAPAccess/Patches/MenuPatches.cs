using BepInEx.Logging;
using SAPAccess.Announcements;
using SAPAccess.GameState;

namespace SAPAccess.Patches;

/// <summary>
/// Harmony hooks for menus (main menu, mode select, settings, results).
///
/// NOTE: Exact method signatures will be determined after running Il2CppDumper.
/// </summary>
public static class MenuPatches
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("SAPAccess.MenuPatch");

    /// <summary>Called when the main menu is shown.</summary>
    public static void OnMainMenuShown()
    {
        Log.LogInfo("Main menu shown");
        GamePhaseTracker.Instance.CurrentPhase = GamePhase.MainMenu;
        MenuAnnouncer.Instance?.OnMainMenu();
    }

    /// <summary>Called when the mode selection screen is shown.</summary>
    public static void OnModeSelectShown()
    {
        Log.LogInfo("Mode select shown");
        GamePhaseTracker.Instance.CurrentPhase = GamePhase.ModeSelect;
        MenuAnnouncer.Instance?.OnModeSelect();
    }

    /// <summary>Called when a modal/popup appears.</summary>
    public static void OnModalShown(string title, string body)
    {
        Log.LogInfo($"Modal: {title}");
        MenuAnnouncer.Instance?.OnModal(title, body);
    }

    /// <summary>Called when a page transition occurs.</summary>
    public static void OnPageChanged(string pageName)
    {
        Log.LogInfo($"Page changed: {pageName}");
        MenuAnnouncer.Instance?.OnPageChanged(pageName);
    }

    /// <summary>Called on game over.</summary>
    public static void OnGameOver(int wins, int lives)
    {
        Log.LogInfo($"Game over: {wins} wins, {lives} lives");
        GamePhaseTracker.Instance.CurrentPhase = GamePhase.GameOver;
        MenuAnnouncer.Instance?.OnGameOver(wins, lives);
    }

    // =========================================================================
    // Harmony patch methods will be wired up once interop DLLs are available.
    // Example:
    //
    // [HarmonyPatch(typeof(Il2Cpp.Spacewood.Unity.Menu), "Show")]
    // [HarmonyPostfix]
    // public static void Menu_Show_Postfix(Il2Cpp.Spacewood.Unity.Menu __instance)
    // {
    //     OnMainMenuShown();
    // }
    // =========================================================================
}
