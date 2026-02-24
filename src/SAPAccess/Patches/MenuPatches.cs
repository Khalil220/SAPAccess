using BepInEx.Logging;
using HarmonyLib;
using SAPAccess.Announcements;
using SAPAccess.GameState;
using SAPAccess.Navigation;

namespace SAPAccess.Patches;

/// <summary>
/// Harmony hooks for menus (page transitions, popups).
/// </summary>
[HarmonyPatch]
public static class MenuPatches
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("SAPAccess.MenuPatch");

    /// <summary>Postfix on PageManager.Open — detects page transitions.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.PageManager), nameof(Spacewood.Unity.PageManager.Open))]
    [HarmonyPostfix]
    public static void PageManager_Open_Postfix(Spacewood.Unity.Page page)
    {
        try
        {
            string pageName = page?.gameObject?.name ?? "Unknown";
            Log.LogInfo($"Page opened: {pageName}");
            MenuAnnouncer.Instance?.OnPageChanged(pageName);
            MenuNavigator.Instance?.OnPageChanged(page);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"PageManager.Open patch error: {ex}");
        }
    }

    /// <summary>Postfix on PopupManager.AddMessage — intercepts popup messages.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.PopupManager), nameof(Spacewood.Unity.PopupManager.AddMessage))]
    [HarmonyPostfix]
    public static void PopupManager_AddMessage_Postfix(string message)
    {
        try
        {
            Log.LogInfo($"Popup: {message}");
            MenuAnnouncer.Instance?.OnModal(message, "");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"PopupManager.AddMessage patch error: {ex}");
        }
    }

    /// <summary>Postfix on Menu.StartLobby — detects return to main lobby.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.Menu), nameof(Spacewood.Unity.Menu.StartLobby))]
    [HarmonyPostfix]
    public static void Menu_StartLobby_Postfix()
    {
        try
        {
            Log.LogInfo("Lobby opened");
            GamePhaseTracker.Instance.CurrentPhase = GamePhase.MainMenu;
            MenuAnnouncer.Instance?.OnMainMenu();
            MenuNavigator.Instance?.OnPageChanged(null);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Menu.StartLobby patch error: {ex}");
        }
    }

    /// <summary>Postfix on Menu.StartModeMenu — detects mode select screen.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.Menu), nameof(Spacewood.Unity.Menu.StartModeMenu))]
    [HarmonyPostfix]
    public static void Menu_StartModeMenu_Postfix()
    {
        try
        {
            Log.LogInfo("Mode menu opened");
            GamePhaseTracker.Instance.CurrentPhase = GamePhase.ModeSelect;
            MenuAnnouncer.Instance?.OnModeSelect();
            MenuNavigator.Instance?.OnPageChanged(null);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Menu.StartModeMenu patch error: {ex}");
        }
    }

    /// <summary>Postfix on Menu.StartSignIn — detects sign-in page.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.Menu), nameof(Spacewood.Unity.Menu.StartSignIn))]
    [HarmonyPostfix]
    public static void Menu_StartSignIn_Postfix()
    {
        try
        {
            Log.LogInfo("Sign-in page opened");
            MenuAnnouncer.Instance?.OnPageChanged("Sign In");
            MenuNavigator.Instance?.OnPageChanged(null);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Menu.StartSignIn patch error: {ex}");
        }
    }

    /// <summary>Postfix on Menu.StartSignUp — detects sign-up page.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.Menu), nameof(Spacewood.Unity.Menu.StartSignUp))]
    [HarmonyPostfix]
    public static void Menu_StartSignUp_Postfix()
    {
        try
        {
            Log.LogInfo("Sign-up page opened");
            MenuAnnouncer.Instance?.OnPageChanged("Register");
            MenuNavigator.Instance?.OnPageChanged(null);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Menu.StartSignUp patch error: {ex}");
        }
    }

    /// <summary>Postfix on Menu.StartForgotPassword — detects forgot password page.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.Menu), nameof(Spacewood.Unity.Menu.StartForgotPassword))]
    [HarmonyPostfix]
    public static void Menu_StartForgotPassword_Postfix()
    {
        try
        {
            Log.LogInfo("Forgot password page opened");
            MenuAnnouncer.Instance?.OnPageChanged("Forgot Password");
            MenuNavigator.Instance?.OnPageChanged(null);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Menu.StartForgotPassword patch error: {ex}");
        }
    }

    // ── Picker Dialog Patches ────────────────────────────────────────

    /// <summary>Postfix on Picker.Open — detects choice/confirmation dialogs.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.UI.Picker), nameof(Spacewood.Unity.UI.Picker.Open))]
    [HarmonyPostfix]
    public static void Picker_Open_Postfix()
    {
        try
        {
            Log.LogInfo("Picker dialog opened");
            MenuNavigator.Instance?.OnPickerOpened();
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Picker.Open patch error: {ex}");
        }
    }

    /// <summary>Postfix on Picker.Pick — detects when a choice is made.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.UI.Picker), nameof(Spacewood.Unity.UI.Picker.Pick))]
    [HarmonyPostfix]
    public static void Picker_Pick_Postfix(Spacewood.Unity.UI.PickerItem item)
    {
        try
        {
            string? label = null;
            try { label = item?.GetLabel(); } catch { }
            Log.LogInfo($"Picker choice: {label ?? "unknown"}");
            MenuNavigator.Instance?.OnPickerClosed(label);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Picker.Pick patch error: {ex}");
        }
    }

    /// <summary>Postfix on Picker.Close (private) — detects dialog dismissal (e.g. backdrop click).</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.UI.Picker), "Close")]
    [HarmonyPostfix]
    public static void Picker_Close_Postfix()
    {
        try
        {
            Log.LogInfo("Picker dialog closed");
            MenuNavigator.Instance?.OnPickerClosed();
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Picker.Close patch error: {ex}");
        }
    }
}
