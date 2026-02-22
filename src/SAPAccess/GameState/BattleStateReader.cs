using System.Collections.Generic;
using BepInEx.Logging;

namespace SAPAccess.GameState;

/// <summary>
/// Reads the current state of a battle: events, outcome, and team health.
/// Data is populated by Harmony patches on BoardController and BoardView.
/// </summary>
public class BattleStateReader
{
    public static BattleStateReader Instance { get; } = new();

    private readonly ManualLogSource _log;
    private readonly Queue<BattleEvent> _eventQueue = new();

    public BattleOutcome LastOutcome { get; set; } = BattleOutcome.Unknown;
    public int EnemyPetsRemaining { get; set; }
    public int FriendlyPetsRemaining { get; set; }

    private BattleStateReader()
    {
        _log = Logger.CreateLogSource("SAPAccess.Battle");
    }

    /// <summary>Queues a battle event for announcement.</summary>
    public void QueueEvent(BattleEvent evt)
    {
        _eventQueue.Enqueue(evt);
        _log.LogDebug($"Battle event: {evt.Type} - {evt.Description}");
    }

    /// <summary>Dequeues the next battle event, or null if empty.</summary>
    public BattleEvent? DequeueEvent()
    {
        return _eventQueue.Count > 0 ? _eventQueue.Dequeue() : null;
    }

    /// <summary>Clears all pending events (called on battle start).</summary>
    public void Reset()
    {
        _eventQueue.Clear();
        LastOutcome = BattleOutcome.Unknown;
        EnemyPetsRemaining = 0;
        FriendlyPetsRemaining = 0;
    }

    public string GetOutcomeSummary()
    {
        return LastOutcome switch
        {
            BattleOutcome.Win => "You won the battle!",
            BattleOutcome.Loss => "You lost the battle.",
            BattleOutcome.Draw => "The battle was a draw.",
            _ => "Battle outcome unknown."
        };
    }
}

public class BattleEvent
{
    public BattleEventType Type { get; set; }
    public string Description { get; set; } = "";
    public string? SourcePet { get; set; }
    public string? TargetPet { get; set; }
    public int Damage { get; set; }
}

public enum BattleEventType
{
    Attack,
    Faint,
    AbilityTrigger,
    Summon,
    Buff,
    Hurt,
    Swap
}

public enum BattleOutcome
{
    Unknown,
    Win,
    Loss,
    Draw
}
