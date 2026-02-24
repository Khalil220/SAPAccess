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

        // While editing an input field, only F10 and F1 work; all other input goes to the field
        if (MenuNavigator.Instance?.IsEditing == true)
        {
            if (WasPressed(KeyCode.F10))
            {
                ScreenReader.Instance.Say("SAPAccess screen reader test.");
            }
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
            if (inShop)
                _focus?.InfoUp(); // Scroll up through item info rows
            else
                _focus?.MoveUp(); // Switch groups in menus
        }
        else if (WasPressed(KeyCode.DownArrow))
        {
            if (inShop)
                _focus?.InfoDown(); // Scroll down through item info rows
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
            // If a focus group is active (TallyArenaMenu), activate the focused button
            // Otherwise, dismiss whatever post-game screen is showing
            if (phase == GamePhase.Battle)
            {
                if (element?.OnActivate != null)
                    element.OnActivate();
                else
                    MenuNavigator.Instance?.DismissPostGameScreen();
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
        else if (inShop && WasPressed(KeyCode.T))
        {
            TeamAnnouncer.Instance?.AnnounceTeam();
        }
        else if (inShop && WasPressed(KeyCode.S))
        {
            ShopAnnouncer.Instance?.AnnounceShop();
        }
        else if (inShop && WasPressed(KeyCode.A))
        {
            ShopAnnouncer.Instance?.AnnounceStatus();
        }

        // Escape: cancel food targeting, go back in menus, or stop speech
        else if (WasPressed(KeyCode.Escape))
        {
            if (inShop && MenuNavigator.Instance?.HasPendingFood == true)
            {
                MenuNavigator.Instance.CancelPendingFood();
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

        // Help
        else if (WasPressed(KeyCode.F1))
        {
            AnnounceKeybindings(phase);
        }

        // Debug
        else if (WasPressed(KeyCode.F10))
        {
            ScreenReader.Instance.Say("SAPAccess screen reader test.");
        }
    }

    private static bool WasPressed(KeyCode key)
    {
        return Input.GetKeyDown(key);
    }

    private void AnnounceKeybindings(GamePhase phase)
    {
        if (phase == GamePhase.Shop)
        {
            ScreenReader.Instance.Say(
                "Shop keybindings. " +
                "Left and Right arrows: cycle items. " +
                "Up and Down arrows: scroll item details. " +
                "Tab: switch between shop and team. " +
                "G: focus shop. " +
                "B: focus team. " +
                "Enter or Space: buy pet, or apply food to target. " +
                "R: roll shop. " +
                "F: freeze or unfreeze. " +
                "E: end turn. " +
                "X: sell focused team pet. " +
                "M: merge shop pet onto matching team pet. " +
                "Shift plus Left or Right: reposition team pet. " +
                "T: team summary. " +
                "S: shop summary. " +
                "A: gold, lives, and turn. " +
                "Escape: cancel food targeting or speech. " +
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
                "F1: this help.");
        }
    }
}
