using System.Collections.Generic;
using BepInEx.Logging;

namespace SAPAccess.GameState;

/// <summary>
/// Reads the current state of the shop: gold, lives, turn number, and shop contents.
/// Data is populated by Harmony patches on HangarMain and HangarOverlay.
/// </summary>
public class ShopStateReader
{
    public static ShopStateReader Instance { get; } = new();

    private readonly ManualLogSource _log;

    public int Gold { get; set; }
    public int Lives { get; set; }
    public int Turn { get; set; }
    public int Tier { get; set; }
    public int Victories { get; set; }

    public List<ShopSlot> PetSlots { get; } = new();
    public List<ShopSlot> FoodSlots { get; } = new();

    private ShopStateReader()
    {
        _log = Logger.CreateLogSource("SAPAccess.Shop");
    }

    /// <summary>Reads all shop state from the game's BoardModel.</summary>
    public void ReadFromBoard(Spacewood.Core.Models.BoardModel board)
    {
        Gold = board.Gold;
        Turn = board.Turn;
        Lives = board.Lives;
        Tier = board.Tier;
        Victories = board.Victories;

        PetSlots.Clear();
        if (board.MinionShop != null)
        {
            for (int i = 0; i < board.MinionShop.Count; i++)
            {
                var minion = board.MinionShop[i];
                if (minion == null) continue;

                string name;
                try
                {
                    name = Spacewood.Unity.Extensions.MinionModelExtensions.GetNameLocalized(minion) ?? minion.Enum.ToString();
                }
                catch
                {
                    name = minion.Enum.ToString();
                }

                PetSlots.Add(new ShopSlot
                {
                    Name = name,
                    Attack = minion.Attack?.Total ?? 0,
                    Health = minion.Health?.Total ?? 0,
                    Cost = minion.Price,
                    IsFrozen = minion.Frozen,
                    Tier = minion.Tier
                });
            }
        }

        FoodSlots.Clear();
        if (board.SpellShop != null)
        {
            for (int i = 0; i < board.SpellShop.Count; i++)
            {
                var spell = board.SpellShop[i];
                if (spell == null) continue;

                string name;
                try
                {
                    name = Spacewood.Unity.Extensions.SpellModelExtensions.GetNameLocalized(spell) ?? spell.Enum.ToString();
                }
                catch
                {
                    name = spell.Enum.ToString();
                }

                FoodSlots.Add(new ShopSlot
                {
                    Name = name,
                    Cost = spell.Price,
                    IsFrozen = spell.Frozen,
                    IsFood = true
                });
            }
        }

        _log.LogDebug($"Shop read: Turn {Turn}, Gold {Gold}, Lives {Lives}, {PetSlots.Count} pets, {FoodSlots.Count} food");
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
                string desc = $"{i + 1}. {slot.Name} {slot.Attack}/{slot.Health}";
                if (slot.IsFrozen) desc += " frozen";
                parts.Add(desc);
            }
        }

        if (FoodSlots.Count > 0)
        {
            parts.Add($"{FoodSlots.Count} food:");
            for (int i = 0; i < FoodSlots.Count; i++)
            {
                var slot = FoodSlots[i];
                string desc = $"{i + 1}. {slot.Name}";
                if (slot.IsFrozen) desc += " frozen";
                parts.Add(desc);
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
}
