using System;
using BepInEx.Logging;

namespace SAPAccess.GameState;

/// <summary>
/// Tracks the current game phase (menu, shop, battle, results).
/// Updated by Harmony patches on phase-transition methods.
/// </summary>
public class GamePhaseTracker
{
    public static GamePhaseTracker Instance { get; } = new();

    private readonly ManualLogSource _log;
    private GamePhase _currentPhase = GamePhase.Unknown;

    public GamePhase CurrentPhase
    {
        get => _currentPhase;
        set
        {
            if (_currentPhase == value) return;
            var previous = _currentPhase;
            _currentPhase = value;
            _log.LogInfo($"Phase changed: {previous} -> {value}");
            PhaseChanged?.Invoke(previous, value);
        }
    }

    public event Action<GamePhase, GamePhase>? PhaseChanged;

    private GamePhaseTracker()
    {
        _log = Logger.CreateLogSource("SAPAccess.Phase");
    }
}

public enum GamePhase
{
    Unknown,
    MainMenu,
    ModeSelect,
    Shop,
    Battle,
    BattleResult,
    GameOver,
    Victory
}
