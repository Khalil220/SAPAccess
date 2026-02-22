using System.Collections.Generic;
using BepInEx.Logging;

namespace SAPAccess.GameState;

/// <summary>
/// Reads the current team composition (up to 5 pet slots).
/// Data is populated by Harmony patches on BoardModel/BoardView.
/// </summary>
public class TeamStateReader
{
    public static TeamStateReader Instance { get; } = new();

    private readonly ManualLogSource _log;

    public List<TeamSlot> Slots { get; } = new();

    private TeamStateReader()
    {
        _log = Logger.CreateLogSource("SAPAccess.Team");
    }

    /// <summary>Clears team data (called before repopulating).</summary>
    public void Clear()
    {
        Slots.Clear();
    }

    public string GetTeamSummary()
    {
        if (Slots.Count == 0)
            return "Team is empty.";

        var parts = new List<string> { $"Team. {Slots.Count} pets." };
        for (int i = 0; i < Slots.Count; i++)
        {
            var slot = Slots[i];
            string desc = $"{i + 1}. {slot.Name}, {slot.Attack} attack, {slot.Health} health";
            if (slot.Level > 1)
                desc += $", level {slot.Level}";
            if (!string.IsNullOrEmpty(slot.HeldItem))
                desc += $", holding {slot.HeldItem}";
            parts.Add(desc);
        }

        return string.Join(". ", parts);
    }
}

public class TeamSlot
{
    public string Name { get; set; } = "";
    public int Attack { get; set; }
    public int Health { get; set; }
    public int Level { get; set; } = 1;
    public int Experience { get; set; }
    public string? HeldItem { get; set; }
    public int Position { get; set; }
    public Il2CppSystem.Object? GameReference { get; set; }
}
