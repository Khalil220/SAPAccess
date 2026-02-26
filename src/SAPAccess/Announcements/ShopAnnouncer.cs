using BepInEx.Logging;
using SAPAccess.Config;
using SAPAccess.GameState;
using SAPAccess.Navigation;
using SAPAccess.NVDA;

namespace SAPAccess.Announcements;

/// <summary>
/// Announces shop-phase events and executes shop actions (roll, freeze, end turn, sell).
/// </summary>
public class ShopAnnouncer
{
    public static ShopAnnouncer? Instance { get; private set; }

    private readonly ManualLogSource _log;
    private readonly ShopStateReader _shop;

    /// <summary>Cached reference to the game's hangar controller, set by MenuNavigator.</summary>
    public Spacewood.Unity.MonoBehaviours.Build.HangarMain? Hangar { get; set; }

    public ShopAnnouncer()
    {
        Instance = this;
        _log = Logger.CreateLogSource("SAPAccess.ShopAnnounce");
        _shop = ShopStateReader.Instance;
    }

    public void OnTurnStart()
    {
        var sr = ScreenReader.Instance;
        sr.Say($"Turn {_shop.Turn}. {_shop.Gold} gold. {_shop.Lives} lives.");

        if (ModConfig.Instance?.AnnounceShopOnTurnStart.Value == true)
        {
            sr.SayQueued(_shop.GetShopSummary());
        }
    }

    public void OnBuy(string petName, int cost)
    {
        ScreenReader.Instance.Say($"Bought {petName}. {_shop.Gold} gold remaining.");
    }

    public void OnSell(string petName, int goldGained)
    {
        ScreenReader.Instance.Say($"Sold {petName} for {goldGained} gold. {_shop.Gold} gold total.");
    }

    public void OnFreeze(string name, bool frozen)
    {
        string action = frozen ? "Frozen" : "Unfrozen";
        ScreenReader.Instance.Say($"{action} {name}.");
    }

    public void OnLevelUp(string petName, int newLevel)
    {
        ScreenReader.Instance.Say($"{petName} leveled up to level {newLevel}!");
    }

    // ── Keyboard-triggered actions ──────────────────────────────────────

    public void Roll()
    {
        if (Hangar == null)
        {
            ScreenReader.Instance.Say("Cannot roll.");
            _log.LogWarning("Cannot roll: HangarMain not found");
            return;
        }

        // Pre-validate: rolling costs 1 gold
        try
        {
            var board = Hangar.BuildModel?.Board;
            if (board != null && board.Gold < 1)
            {
                ScreenReader.Instance.Say("Not enough gold to roll.");
                return;
            }
        }
        catch { }

        try
        {
            Hangar.RollShopAsync();
            _log.LogInfo("Roll executed");
            // Don't announce gold here — it's stale (board hasn't deducted yet).
            // ScheduleShopRefresh will announce updated gold via the flag.
            MenuNavigator.Instance?.ScheduleShopRefresh(announceGold: true);
        }
        catch (System.Exception ex)
        {
            _log.LogError($"Roll error: {ex}");
            ScreenReader.Instance.Say("Roll failed.");
        }
    }

    public void FreezeToggle()
    {
        if (Hangar == null)
        {
            ScreenReader.Instance.Say("Cannot freeze.");
            _log.LogWarning("Cannot freeze: HangarMain not found");
            return;
        }

        // Only allow freezing shop items, not team pets
        if (FocusManager.Instance?.CurrentGroup?.Name != "Shop")
        {
            ScreenReader.Instance.Say("Can only freeze shop items.");
            return;
        }

        var element = FocusManager.Instance?.CurrentElement;
        if (element?.Tag == null)
        {
            ScreenReader.Instance.Say("No shop item focused.");
            return;
        }

        try
        {
            if (element.Tag is Spacewood.Core.Models.Item.ItemModel item)
            {
                bool wasFrozen = item.Frozen;
                Hangar.FreezeItem(item);
                string action = wasFrozen ? "Unfrozen" : "Frozen";
                ScreenReader.Instance.Say($"{action} {element.Label}.");
                _log.LogInfo($"Freeze toggled on {element.Label} (was frozen: {wasFrozen})");
                MenuNavigator.Instance?.ScheduleShopRefresh();
            }
            else
            {
                ScreenReader.Instance.Say("Cannot freeze this item.");
            }
        }
        catch (System.Exception ex)
        {
            _log.LogError($"Freeze error: {ex}");
            ScreenReader.Instance.Say("Freeze failed.");
        }
    }

    public void EndTurn()
    {
        if (Hangar == null)
        {
            ScreenReader.Instance.Say("Cannot end turn.");
            _log.LogWarning("Cannot end turn: HangarMain not found");
            return;
        }

        // Pre-validate: need at least 1 pet on team
        try
        {
            var board = Hangar.BuildModel?.Board;
            if (board?.Minions?.Items != null)
            {
                int teamSize = 0;
                for (int i = 0; i < board.Minions.Items.Count; i++)
                {
                    var m = board.Minions.Items[i];
                    if (m != null && !m.Dead) teamSize++;
                }
                if (teamSize == 0)
                {
                    ScreenReader.Instance.Say("Can't end turn. No pets on team.");
                    return;
                }
            }
        }
        catch { }

        try
        {
            ScreenReader.Instance.Say("Ending turn.");
            Hangar.EndTurnAsync();
            _log.LogInfo("End turn executed");
        }
        catch (System.Exception ex)
        {
            _log.LogError($"End turn error: {ex}");
            ScreenReader.Instance.Say("End turn failed.");
        }
    }

    public void Sell()
    {
        if (Hangar == null)
        {
            ScreenReader.Instance.Say("Cannot sell.");
            _log.LogWarning("Cannot sell: HangarMain not found");
            return;
        }

        var element = FocusManager.Instance?.CurrentElement;
        if (element?.Tag == null)
        {
            ScreenReader.Instance.Say("No pet focused.");
            return;
        }

        if (element.Tag is Spacewood.Core.Models.MinionModel minion
            && minion.Location == Spacewood.Core.Enums.Location.Team)
        {
            try
            {
                string name = element.Label;
                Hangar.SellMinionAsync(minion);
                ScreenReader.Instance.Say($"Sold {name}.");
                _log.LogInfo($"Sell executed: {name}");
                MenuNavigator.Instance?.ScheduleShopRefresh();
            }
            catch (System.Exception ex)
            {
                _log.LogError($"Sell error: {ex}");
                ScreenReader.Instance.Say("Sell failed.");
            }
        }
        else
        {
            ScreenReader.Instance.Say("Can only sell team pets.");
        }
    }

    public void AnnounceShop()
    {
        ScreenReader.Instance.Say(_shop.GetShopSummary());
    }

    public void AnnounceStatus()
    {
        ScreenReader.Instance.Say(_shop.GetStatusSummary());
    }

    public void AnnounceTurn()
    {
        ScreenReader.Instance.Say($"Turn {_shop.Turn}.");
    }

    public void AnnounceGold()
    {
        ScreenReader.Instance.Say($"{_shop.Gold} gold.");
    }

    public void AnnounceLives()
    {
        ScreenReader.Instance.Say($"{_shop.Lives} lives.");
    }
}
