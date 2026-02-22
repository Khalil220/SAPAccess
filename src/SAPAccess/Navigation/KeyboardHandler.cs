using BepInEx.Logging;
using SAPAccess.Announcements;
using SAPAccess.GameState;
using SAPAccess.NVDA;
using UnityEngine;
using UnityEngine.InputSystem;

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

    public new void Awake()
    {
        _log = Logger.CreateLogSource("SAPAccess.Input");
        _focus = FocusManager.Instance;
    }

    public new void Update()
    {
        if (_focus == null)
            _focus = FocusManager.Instance;

        if (Time.time - _lastKeyTime < KeyRepeatDelay)
            return;

        // Navigation keys
        if (WasPressed(Key.LeftArrow))
        {
            _focus?.MoveLeft();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(Key.RightArrow))
        {
            _focus?.MoveRight();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(Key.UpArrow))
        {
            _focus?.MoveUp();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(Key.DownArrow))
        {
            _focus?.MoveDown();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(Key.Tab))
        {
            _focus?.CycleGroup();
            _lastKeyTime = Time.time;
        }

        // Activation
        else if (WasPressed(Key.Enter) || WasPressed(Key.Space))
        {
            var element = _focus?.CurrentElement;
            if (element?.OnActivate != null)
            {
                element.OnActivate();
                _lastKeyTime = Time.time;
            }
        }

        // Shop action keys
        else if (WasPressed(Key.R))
        {
            ShopAnnouncer.Instance?.Roll();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(Key.F))
        {
            ShopAnnouncer.Instance?.FreezeToggle();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(Key.E))
        {
            ShopAnnouncer.Instance?.EndTurn();
            _lastKeyTime = Time.time;
        }

        // Info keys
        else if (WasPressed(Key.T))
        {
            TeamAnnouncer.Instance?.AnnounceTeam();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(Key.S))
        {
            ShopAnnouncer.Instance?.AnnounceShop();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(Key.G))
        {
            ShopAnnouncer.Instance?.AnnounceStatus();
            _lastKeyTime = Time.time;
        }
        else if (WasPressed(Key.Escape))
        {
            ScreenReader.Instance.Stop();
            _lastKeyTime = Time.time;
        }

        // Help
        else if (WasPressed(Key.F1))
        {
            AnnounceKeybindings();
            _lastKeyTime = Time.time;
        }

        // Debug
        else if (WasPressed(Key.F10))
        {
            ScreenReader.Instance.Say("SAPAccess screen reader test.");
            _lastKeyTime = Time.time;
        }
    }

    private static bool WasPressed(Key key)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return false;
        return keyboard[key].wasPressedThisFrame;
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
