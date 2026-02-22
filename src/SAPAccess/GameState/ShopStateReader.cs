using System.Collections.Generic;
using BepInEx.Logging;

namespace SAPAccess.GameState;

/// <summary>
/// Reads the current state of the shop: gold, lives, turn number, and shop contents.
/// Data is populated by Harmony patches on HangarMain and related classes.
/// </summary>
public class ShopStateReader
{
    public static ShopStateReader Instance { get; } = new();

    private readonly ManualLogSource _log;

    public int Gold { get; set; }
    public int Lives { get; set; }
    public int Turn { get; set; }
    public int MaxTeamSize { get; set; } = 5;

    public List<ShopSlot> PetSlots { get; } = new();
    public List<ShopSlot> FoodSlots { get; } = new();

    private ShopStateReader()
    {
        _log = Logger.CreateLogSource("SAPAccess.Shop");
    }

    /// <summary>Clears shop slot data (called on turn start before repopulating).</summary>
    public void ClearSlots()
    {
        PetSlots.Clear();
        FoodSlots.Clear();
    }

    public string GetStatusSummary()
    {
        return $"Turn {Turn}. {Gold} gold. {Lives} lives.";
    }

    public string GetShopSummary()
    {
        var parts = new List<string>();

        if (PetSlots.Count > 0)
        {
            parts.Add($"{PetSlots.Count} pets:");
            for (int i = 0; i < PetSlots.Count; i++)
            {
                var slot = PetSlots[i];
                parts.Add(slot.IsFrozen
                    ? $"  {i + 1}. {slot.Name} {slot.Attack}/{slot.Health} frozen"
                    : $"  {i + 1}. {slot.Name} {slot.Attack}/{slot.Health}");
            }
        }

        if (FoodSlots.Count > 0)
        {
            parts.Add($"{FoodSlots.Count} food:");
            for (int i = 0; i < FoodSlots.Count; i++)
            {
                var slot = FoodSlots[i];
                parts.Add($"  {i + 1}. {slot.Name}");
            }
        }

        return parts.Count > 0
            ? $"Shop. {string.Join(". ", parts)}"
            : "Shop is empty.";
    }
}

public class ShopSlot
{
    public string Name { get; set; } = "";
    public int Attack { get; set; }
    public int Health { get; set; }
    public int Cost { get; set; } = 3;
    public bool IsFrozen { get; set; }
    public bool IsFood { get; set; }
    public int Tier { get; set; }
    public Il2CppSystem.Object? GameReference { get; set; }
}
