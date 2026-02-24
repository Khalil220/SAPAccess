using System.Collections.Generic;
using BepInEx.Logging;

namespace SAPAccess.GameState;

/// <summary>
/// Reads the current team composition from the game's BoardModel.
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

    /// <summary>Reads team composition from the game's BoardModel.</summary>
    public void ReadFromBoard(Spacewood.Core.Models.BoardModel board)
    {
        Slots.Clear();

        if (board.Minions?.Items == null) return;

        for (int i = 0; i < board.Minions.Items.Count; i++)
        {
            var minion = board.Minions.Items[i];
            if (minion == null || minion.Dead) continue;

            string name;
            try
            {
                name = Spacewood.Unity.Extensions.MinionModelExtensions.GetNameLocalized(minion) ?? minion.Enum.ToString();
            }
            catch
            {
                name = minion.Enum.ToString();
            }

            string? heldItem = null;
            try
            {
                if (minion.Perk.HasValue)
                    heldItem = minion.Perk.Value.ToString();
            }
            catch { /* IL2CPP Nullable<T> throws when perk is null */ }

            Slots.Add(new TeamSlot
            {
                Name = name,
                Attack = minion.Attack?.Total ?? 0,
                Health = minion.Health?.Total ?? 0,
                Level = minion.Level,
                Experience = minion.Exp,
                HeldItem = heldItem,
                Position = i
            });
        }

        _log.LogDebug($"Team read: {Slots.Count} pets");
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
}
