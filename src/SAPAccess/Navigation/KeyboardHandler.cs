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
    private float _lastKeyTime;
    private const float KeyRepeatDelay = 0.15f;

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

        if (Time.time - _lastKeyTime < KeyRepeatDelay)
            return;

        // Navigation keys
        if (WasPressed(KeyCode.LeftArrow))
        {
            _focus?.MoveLeft();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(KeyCode.RightArrow))
        {
            _focus?.MoveRight();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(KeyCode.UpArrow))
        {
            _focus?.MoveUp();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(KeyCode.DownArrow))
        {
            _focus?.MoveDown();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(KeyCode.Tab))
        {
            _focus?.CycleGroup();
            _lastKeyTime = Time.time;
        }

        // Activation
        else if (WasPressed(KeyCode.Return) || WasPressed(KeyCode.Space))
        {
            var element = _focus?.CurrentElement;
            if (element?.OnActivate != null)
            {
                element.OnActivate();
                _lastKeyTime = Time.time;
            }
        }

        // Shop action keys (only in shop phase)
        else if (GamePhaseTracker.Instance.CurrentPhase == GamePhase.Shop && WasPressed(KeyCode.R))
        {
            ShopAnnouncer.Instance?.Roll();
            _lastKeyTime = Time.time;
        }
        else if (GamePhaseTracker.Instance.CurrentPhase == GamePhase.Shop && WasPressed(KeyCode.F))
        {
            ShopAnnouncer.Instance?.FreezeToggle();
            _lastKeyTime = Time.time;
        }
        else if (GamePhaseTracker.Instance.CurrentPhase == GamePhase.Shop && WasPressed(KeyCode.E))
        {
            ShopAnnouncer.Instance?.EndTurn();
            _lastKeyTime = Time.time;
        }

        // Info keys (only in shop phase)
        else if (GamePhaseTracker.Instance.CurrentPhase == GamePhase.Shop && WasPressed(KeyCode.T))
        {
            TeamAnnouncer.Instance?.AnnounceTeam();
            _lastKeyTime = Time.time;
        }
        else if (GamePhaseTracker.Instance.CurrentPhase == GamePhase.Shop && WasPressed(KeyCode.S))
        {
            ShopAnnouncer.Instance?.AnnounceShop();
            _lastKeyTime = Time.time;
        }
        else if (GamePhaseTracker.Instance.CurrentPhase == GamePhase.Shop && WasPressed(KeyCode.G))
        {
            ShopAnnouncer.Instance?.AnnounceStatus();
            _lastKeyTime = Time.time;
        }

        // Escape: in menus go back, otherwise stop speech
        else if (WasPressed(KeyCode.Escape))
        {
            var phase = GamePhaseTracker.Instance.CurrentPhase;
            if (phase == GamePhase.MainMenu || phase == GamePhase.ModeSelect)
            {
                MenuNavigator.Instance?.GoBack();
            }
            else
            {
                ScreenReader.Instance.Stop();
            }
            _lastKeyTime = Time.time;
        }

        // Help
        else if (WasPressed(KeyCode.F1))
        {
            AnnounceKeybindings();
            _lastKeyTime = Time.time;
        }

        // Debug
        else if (WasPressed(KeyCode.F10))
        {
            ScreenReader.Instance.Say("SAPAccess screen reader test.");
            _lastKeyTime = Time.time;
        }
    }

    private static bool WasPressed(KeyCode key)
    {
        return Input.GetKeyDown(key);
    }

    private void AnnounceKeybindings()
    {
        ScreenReader.Instance.Say(
            "Keybindings. " +
            "Left and Right arrows: navigate items. " +
            "Up and Down arrows: switch rows. " +
            "Tab: cycle groups. " +
            "Enter or Space: activate. " +
            "R: roll shop. " +
            "F: freeze or unfreeze. " +
            "E: end turn. " +
            "T: team summary. " +
            "S: shop summary. " +
            "G: gold, lives, and turn. " +
            "Escape: cancel speech. " +
            "F1: this help.");
    }
}
