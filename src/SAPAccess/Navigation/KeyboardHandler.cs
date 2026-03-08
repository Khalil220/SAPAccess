using BepInEx.Logging;
using SAPAccess.Announcements;
using SAPAccess.GameState;
using SAPAccess.NVDA;
using UnityEngine;

namespace SAPAccess.Navigation;

/// <summary>
/// Processes keyboard input and dispatches to FocusManager, announcers, and game actions.
/// Attached as a MonoBehaviour to persist across scenes.
/// </summary>
public class KeyboardHandler : MonoBehaviour
{
    private static ManualLogSource? _log;
    private FocusManager? _focus;

    public void Awake()
    {
        _log = BepInEx.Logging.Logger.CreateLogSource("SAPAccess.Input");
        _focus = FocusManager.Instance;
    }

    public void Update()
    {
        if (_focus == null)
            _focus = FocusManager.Instance;

        // While editing an input field, only F1 works; all other input goes to the field
        if (MenuNavigator.Instance?.IsEditing == true)
        {
            return;
        }

        // End-turn confirmation prompt: E confirms, Escape cancels, navigation still works
        if (ShopAnnouncer.Instance?.EndTurnConfirmPending == true)
        {
            if (WasPressed(KeyCode.E))
            {
                ShopAnnouncer.Instance.ConfirmEndTurn();
                return;
            }
            if (WasPressed(KeyCode.Escape))
            {
                ShopAnnouncer.Instance.CancelEndTurn();
                return;
            }
            // Enter/Space activates the focused button (Confirm or Cancel)
            if (WasPressed(KeyCode.Return) || WasPressed(KeyCode.Space))
            {
                _focus?.CurrentElement?.OnActivate?.Invoke();
                return;
            }
            // Arrow keys navigate between confirm/cancel
            if (WasPressed(KeyCode.LeftArrow)) { _focus?.MoveLeft(); return; }
            if (WasPressed(KeyCode.RightArrow)) { _focus?.MoveRight(); return; }
            // Block all other keys while prompt is showing
            return;
        }

        var phase = GamePhaseTracker.Instance.CurrentPhase;
        bool inShop = phase == GamePhase.Shop && MenuNavigator.Instance?.IsDialogOpen != true;

        // Shift+Arrow: reposition team pets (shop phase only)
        bool shiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        if (inShop && shiftHeld && WasPressed(KeyCode.LeftArrow))
        {
            MenuNavigator.Instance?.ShiftPet(-1);
        }
        else if (inShop && shiftHeld && WasPressed(KeyCode.RightArrow))
        {
            MenuNavigator.Instance?.ShiftPet(1);
        }

        // Navigation keys
        else if (WasPressed(KeyCode.LeftArrow))
        {
            _focus?.MoveLeft();
        }
        else if (WasPressed(KeyCode.RightArrow))
        {
            _focus?.MoveRight();
        }
        else if (WasPressed(KeyCode.UpArrow))
        {
            // Use InfoUp when current element has info rows (shop items, pack preview, etc.)
            if (inShop || _focus?.CurrentElement?.InfoRows is { Count: > 0 })
                _focus?.InfoUp();
            else
                _focus?.MoveUp(); // Switch groups in menus
        }
        else if (WasPressed(KeyCode.DownArrow))
        {
            if (inShop || _focus?.CurrentElement?.InfoRows is { Count: > 0 })
                _focus?.InfoDown();
            else
                _focus?.MoveDown(); // Switch groups in menus
        }
        else if (WasPressed(KeyCode.Tab))
        {
            _focus?.CycleGroup();
        }

        // Activation
        else if (WasPressed(KeyCode.Return) || WasPressed(KeyCode.Space))
        {
            var element = _focus?.CurrentElement;

            // During battle phase (includes post-game screens):
            // If a focus group is active (TallyArenaMenu), activate the focused button.
            // Otherwise try to advance battle events (manual next), or dismiss post-game screens.
            if (phase == GamePhase.Battle)
            {
                if (element?.OnActivate != null)
                    element.OnActivate();
                else if (!TryAdvanceBattle())
                    MenuNavigator.Instance?.DismissPostGameScreen();
            }
            // If pet placement is active and a team position is focused, place the pet
            else if (inShop && MenuNavigator.Instance?.HasPendingPet == true
                && _focus?.CurrentGroup?.Name == "Team"
                && element?.Tag is string placeTag && placeTag.StartsWith("place:"))
            {
                if (int.TryParse(placeTag.Substring(6), out int position))
                    MenuNavigator.Instance.PlacePendingPet(position, element.Label);
            }
            // If food targeting is active and a team pet is focused, apply food to it
            else if (inShop && MenuNavigator.Instance?.HasPendingFood == true
                && _focus?.CurrentGroup?.Name == "Team"
                && element?.Tag is Spacewood.Core.Models.MinionModel targetMinion)
            {
                MenuNavigator.Instance.ApplyPendingFood(targetMinion, element.Label);
            }
            else if (element?.OnActivate != null)
            {
                element.OnActivate();
            }
        }

        // Shop action keys (only in shop phase, not when a dialog is open)
        else if (inShop && WasPressed(KeyCode.R))
        {
            ShopAnnouncer.Instance?.Roll();
        }
        else if (inShop && WasPressed(KeyCode.Q))
        {
            ShopAnnouncer.Instance?.AnnounceTimer();
        }
        else if (inShop && WasPressed(KeyCode.F))
        {
            ShopAnnouncer.Instance?.FreezeToggle();
        }
        else if (inShop && WasPressed(KeyCode.E))
        {
            ShopAnnouncer.Instance?.EndTurn();
        }
        else if (inShop && WasPressed(KeyCode.X))
        {
            ShopAnnouncer.Instance?.Sell();
        }
        else if (inShop && WasPressed(KeyCode.M))
        {
            MenuNavigator.Instance?.MergePet();
        }

        // Quick-focus keys (shop phase)
        else if (inShop && WasPressed(KeyCode.G))
        {
            _focus?.FocusGroupByName("Shop");
        }
        else if (inShop && WasPressed(KeyCode.B))
        {
            _focus?.FocusGroupByName("Team");
        }

        // Info keys (shop phase)
        else if (inShop && WasPressed(KeyCode.S))
        {
            ShopAnnouncer.Instance?.AnnounceShop();
        }
        else if (inShop && WasPressed(KeyCode.T))
        {
            ShopAnnouncer.Instance?.AnnounceTurn();
        }
        else if (inShop && WasPressed(KeyCode.A))
        {
            ShopAnnouncer.Instance?.AnnounceGold();
        }
        else if (inShop && WasPressed(KeyCode.L))
        {
            ShopAnnouncer.Instance?.AnnounceLives();
        }

        // Escape: close dialog, cancel food targeting, go back in menus, or stop speech
        else if (WasPressed(KeyCode.Escape))
        {
            if (MenuNavigator.Instance?.IsSideBarOpen == true)
            {
                MenuNavigator.Instance.CloseSideBar();
            }
            else if (MenuNavigator.Instance?.IsDialogOpen == true)
            {
                MenuNavigator.Instance.DismissCurrentDialog();
            }
            else if (inShop && MenuNavigator.Instance?.HasPendingPet == true)
            {
                MenuNavigator.Instance.CancelPendingPet();
            }
            else if (inShop && MenuNavigator.Instance?.HasPendingFood == true)
            {
                MenuNavigator.Instance.CancelPendingFood();
            }
            else if (inShop)
            {
                MenuNavigator.Instance?.OpenSideBar();
            }
            else if (MenuNavigator.Instance?.IsMinionPickerOpen == true)
            {
                MenuNavigator.Instance.CloseMinionPicker();
            }
            else if (phase == GamePhase.MainMenu || phase == GamePhase.ModeSelect)
            {
                MenuNavigator.Instance?.GoBack();
            }
            else
            {
                ScreenReader.Instance.Stop();
            }
        }

        // Battle controls
        else if (phase == GamePhase.Battle && WasPressed(KeyCode.P))
        {
            ToggleBattleAutoPlay();
        }

        // Home: jump to first item in current group
        else if (WasPressed(KeyCode.Home))
        {
            _focus?.MoveToFirst();
        }
        // End: jump to last item in current group
        else if (WasPressed(KeyCode.End))
        {
            _focus?.MoveToLast();
        }

        // Help
        else if (WasPressed(KeyCode.F1))
        {
            AnnounceKeybindings(phase);
        }

        // Toggle end-turn confirmation prompt
        else if (WasPressed(KeyCode.F2))
        {
            ToggleEndTurnConfirm();
        }

        // Volume controls: Shift+F to decrease, F to increase
        else if (WasPressed(KeyCode.F3))
            AdjustVolume(VolumeChannel.Sound, shiftHeld ? -1 : 1);
        else if (WasPressed(KeyCode.F4))
            AdjustVolume(VolumeChannel.Music, shiftHeld ? -1 : 1);
        else if (WasPressed(KeyCode.F5))
            AdjustVolume(VolumeChannel.Ambiance, shiftHeld ? -1 : 1);
        else if (WasPressed(KeyCode.F6))
            AdjustVolume(VolumeChannel.BattleMusic, shiftHeld ? -1 : 1);
        else if (WasPressed(KeyCode.F7))
            AdjustVolume(VolumeChannel.MenuMusic, shiftHeld ? -1 : 1);
    }

    private enum VolumeChannel
    {
        Sound,      // PersistKey 5 (GlobalSoundVolume)
        Music,      // PersistKey 6 (GlobalMusicVolume)
        Ambiance,   // PersistKey 57 (GlobalAmbianceVolume)
        BattleMusic, // PersistKey 56 (GlobalBattleMusic)
        MenuMusic   // PersistKey 55 (GlobalMenuMusic)
    }

    private static readonly (string Name, Spacewood.Scripts.Utilities.PersistKey Key)[] _volumeChannels =
    {
        ("Sound", (Spacewood.Scripts.Utilities.PersistKey)5),
        ("Music", (Spacewood.Scripts.Utilities.PersistKey)6),
        ("Ambiance", (Spacewood.Scripts.Utilities.PersistKey)57),
        ("Battle music", (Spacewood.Scripts.Utilities.PersistKey)56),
        ("Menu music", (Spacewood.Scripts.Utilities.PersistKey)55),
    };

    private void AdjustVolume(VolumeChannel channel, int direction)
    {
        var (name, key) = _volumeChannels[(int)channel];
        try
        {
            float current = Spacewood.Scripts.Utilities.Persist.Get<float>(key);
            float step = 0.1f;
            float newValue = Mathf.Clamp01(current + direction * step);

            // Round to avoid floating point drift (e.g. 0.70000005)
            newValue = Mathf.Round(newValue * 10f) / 10f;

            Spacewood.Scripts.Utilities.Persist.Set<float>(key, newValue, true);

            int percent = Mathf.RoundToInt(newValue * 100f);
            ScreenReader.Instance.Say($"{name} {percent}%");
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Volume adjust error ({name}): {ex.Message}");
        }
    }

    /// <summary>Toggles battle auto-play and announces the new state.</summary>
    private void ToggleBattleAutoPlay()
    {
        try
        {
            var persistKey = (Spacewood.Scripts.Utilities.PersistKey)3; // BattleAutoPlay
            bool current = Spacewood.Scripts.Utilities.Persist.Get<bool>(persistKey);
            bool newValue = !current;

            // Persist the new value
            Spacewood.Scripts.Utilities.Persist.Set<bool>(persistKey, newValue, true);

            // Apply to live UI/controller if available
            try
            {
                var uiBattle = Spacewood.Unity.MonoBehaviours.Battle.UIBattle.Instance;
                if (uiBattle != null)
                    uiBattle.SetAutoplay(newValue);
            }
            catch { }

            try
            {
                var ctrl = Spacewood.Unity.MonoBehaviours.Battle.BattleController.Instance;
                if (ctrl != null)
                    ctrl.SetAutoplay(newValue, false);
            }
            catch { }

            ScreenReader.Instance.Say(newValue ? "Auto-play on" : "Auto-play off");
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Auto-play toggle error: {ex.Message}");
        }
    }

    /// <summary>Tries to click the MoveNext button to advance a paused battle.
    /// Returns true if the button was found and clicked.</summary>
    private bool TryAdvanceBattle()
    {
        try
        {
            var uiBattle = Spacewood.Unity.MonoBehaviours.Battle.UIBattle.Instance;
            if (uiBattle == null) return false;

            var nextBtn = uiBattle.MoveNextButton;
            if (nextBtn != null && nextBtn.gameObject.activeInHierarchy)
            {
                nextBtn.onClick.Invoke();
                return true;
            }
        }
        catch { }
        return false;
    }

    private void ToggleEndTurnConfirm()
    {
        var config = SAPAccess.Config.ModConfig.Instance?.ConfirmEndTurn;
        if (config == null) return;
        config.Value = !config.Value;
        string state = config.Value ? "on" : "off";
        NVDA.ScreenReader.Instance.Say($"End turn confirmation {state}.");
    }

    private static bool WasPressed(KeyCode key)
    {
        return Input.GetKeyDown(key);
    }

    private void AnnounceKeybindings(GamePhase phase)
    {
        if (phase == GamePhase.Battle)
        {
            ScreenReader.Instance.Say(
                "Battle keybindings. " +
                "P: toggle auto-play. " +
                "Enter or Space: advance to next event when auto-play is off. " +
                "F3: sound volume. " +
                "F4: music volume. " +
                "F5: ambiance volume. " +
                "F6: battle music volume. " +
                "F7: menu music volume. " +
                "Hold Shift to decrease. " +
                "F1: this help.");
        }
        else if (phase == GamePhase.Shop)
        {
            ScreenReader.Instance.Say(
                "Shop keybindings. " +
                "Left and Right arrows: cycle items. " +
                "Up and Down arrows: scroll item details. " +
                "Tab: switch between shop and team. " +
                "G: focus shop. " +
                "B: focus team. " +
                "Enter or Space: buy pet and choose position, or apply food to target. " +
                "R: roll shop. " +
                "Q: read turn timer. " +
                "F: freeze or unfreeze. " +
                "E: end turn. " +
                "X: sell focused team pet. " +
                "M: merge shop pet onto matching team pet. " +
                "Shift plus Left or Right: reposition team pet. " +
                "T: team summary. " +
                "S: shop summary. " +
                "A: gold, lives, and turn. " +
                "Escape: cancel placement, food targeting, or speech. " +
                "F2: toggle end turn confirmation. " +
                "F3: sound volume. " +
                "F4: music volume. " +
                "F5: ambiance volume. " +
                "F6: battle music volume. " +
                "F7: menu music volume. " +
                "Hold Shift to decrease. " +
                "F1: this help.");
        }
        else
        {
            ScreenReader.Instance.Say(
                "Keybindings. " +
                "Left and Right arrows: navigate items. " +
                "Up and Down arrows: switch rows. " +
                "Tab: cycle groups. " +
                "Enter or Space: activate. " +
                "Escape: go back or cancel speech. " +
                "F3: sound volume. " +
                "F4: music volume. " +
                "F5: ambiance volume. " +
                "F6: battle music volume. " +
                "F7: menu music volume. " +
                "Hold Shift to decrease. " +
                "F1: this help.");
        }
    }
}
