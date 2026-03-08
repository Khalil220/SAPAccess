using System.Collections.Generic;
using BepInEx.Logging;
using SAPAccess.NVDA;

namespace SAPAccess.Announcements;

/// <summary>
/// Announces menu navigation: main menu, mode select, modals, page transitions.
/// </summary>
public class MenuAnnouncer
{
    public static MenuAnnouncer? Instance { get; private set; }

    private readonly ManualLogSource _log;

    public MenuAnnouncer()
    {
        Instance = this;
        _log = Logger.CreateLogSource("SAPAccess.MenuAnnounce");
    }

    public void OnMainMenu()
    {
        ScreenReader.Instance.Say("Main menu.");
    }

    public void OnModeSelect()
    {
        ScreenReader.Instance.Say("Mode select. Choose a game mode.");
    }

    public void OnModal(string title, string body)
    {
        string text = string.IsNullOrEmpty(body)
            ? title
            : $"{title}. {body}";
        ScreenReader.Instance.Say(text);
    }

    private static readonly Dictionary<string, string> _pageNames = new()
    {
        { "VersusCreator", "Create private match" },
        { "VersusFront", "Private match" },
        { "VersusLobby", "Match lobby" },
    };

    public void OnPageChanged(string pageName)
    {
        string friendly = _pageNames.TryGetValue(pageName, out var name) ? name : pageName;
        ScreenReader.Instance.Say(friendly);
    }

    public void OnGameOver(int wins, int lives)
    {
        string msg = lives <= 0
            ? $"Game over. You finished with {wins} wins."
            : $"Game over. {wins} wins, {lives} lives remaining.";
        ScreenReader.Instance.Say(msg);
    }

    public void OnResultsScreen(int wins, int lives, string? reward)
    {
        string msg = $"Results. {wins} wins, {lives} lives.";
        if (!string.IsNullOrEmpty(reward))
            msg += $" Reward: {reward}.";
        ScreenReader.Instance.Say(msg);
    }
}
