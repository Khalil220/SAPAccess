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

    /// <summary>Cached reference to the game's hangar controller, set by MenuNavigator.
    /// If null or destroyed, attempts to re-find it in the scene.</summary>
    public Spacewood.Unity.MonoBehaviours.Build.HangarMain? Hangar
    {
        get
        {
            // Re-detect if null or if the underlying IL2CPP object was destroyed
            if (_hangar == null || !IsAlive(_hangar))
            {
                try
                {
                    _hangar = UnityEngine.Object.FindObjectOfType<Spacewood.Unity.MonoBehaviours.Build.HangarMain>();
                    if (_hangar != null)
                        _log.LogInfo("HangarMain re-detected in ShopAnnouncer");
                }
                catch { _hangar = null; }
            }
            return _hangar;
        }
        set => _hangar = value;
    }
    private Spacewood.Unity.MonoBehaviours.Build.HangarMain? _hangar;

    /// <summary>Checks whether a Unity Object is still alive (not destroyed).</summary>
    private static bool IsAlive(UnityEngine.Object? obj)
    {
        try { return obj != null; }
        catch { return false; }
    }

    public ShopAnnouncer()
    {
        Instance = this;
        _log = Logger.CreateLogSource("SAPAccess.ShopAnnounce");
        _shop = ShopStateReader.Instance;
    }

    public void OnTurnStart()
    {
        ResetActionGuards();
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

    // ── Action guards ─────────────────────────────────────────────────
    // Prevent double-triggering async actions before the server responds.

    private bool _endTurnInProgress;
    private bool _rollInProgress;
    private bool _sellInProgress;
    public bool BuyInProgress { get; set; }

    /// <summary>True when the end-turn confirmation prompt is showing.</summary>
    public bool EndTurnConfirmPending { get; private set; }

    /// <summary>Resets all action guards. Called on turn start when a new turn begins.</summary>
    public void ResetActionGuards()
    {
        _endTurnInProgress = false;
        _rollInProgress = false;
        _sellInProgress = false;
        BuyInProgress = false;
        EndTurnConfirmPending = false;
    }

    /// <summary>Resets roll and sell guards after shop refresh completes.</summary>
    public void ResetRollAndSellGuards()
    {
        _rollInProgress = false;
        _sellInProgress = false;
        BuyInProgress = false;
    }

    // ── Keyboard-triggered actions ──────────────────────────────────────

    public void Roll()
    {
        if (_rollInProgress)
            return;

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
            _rollInProgress = true;
            Hangar.RollShopAsync();
            _log.LogInfo("Roll executed");
            // Don't announce gold here — it's stale (board hasn't deducted yet).
            // ScheduleShopRefresh will announce updated gold via the flag.
            MenuNavigator.Instance?.ScheduleShopRefresh(announceGold: true);
        }
        catch (System.Exception ex)
        {
            _rollInProgress = false;
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
        if (_endTurnInProgress)
            return;

        if (EndTurnConfirmPending)
        {
            // E pressed again while prompt is showing — treat as confirm
            ConfirmEndTurn();
            return;
        }

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

        // Check if confirmation is needed (gold > 0 and setting enabled)
        if (Config.ModConfig.Instance?.ConfirmEndTurn.Value == true)
        {
            try
            {
                int gold = _shop.Gold;
                if (gold > 0)
                {
                    EndTurnConfirmPending = true;
                    ScreenReader.Instance.Say($"End turn with {gold} gold remaining? Press E or Enter to confirm, Escape to cancel.");
                    _log.LogInfo($"End turn confirmation prompt ({gold} gold remaining)");
                    Navigation.MenuNavigator.Instance?.ShowEndTurnConfirm(gold);
                    return;
                }
            }
            catch { }
        }

        ExecuteEndTurn();
    }

    /// <summary>Confirms the end-turn action from the prompt.</summary>
    public void ConfirmEndTurn()
    {
        EndTurnConfirmPending = false;
        Navigation.MenuNavigator.Instance?.DismissEndTurnConfirm();
        ExecuteEndTurn();
    }

    /// <summary>Cancels the end-turn prompt and returns to shop.</summary>
    public void CancelEndTurn()
    {
        EndTurnConfirmPending = false;
        Navigation.MenuNavigator.Instance?.DismissEndTurnConfirm();
        ScreenReader.Instance.Say("End turn cancelled.");
    }

    private void ExecuteEndTurn()
    {
        try
        {
            _endTurnInProgress = true;
            ScreenReader.Instance.Say("Ending turn.");
            Hangar.EndTurnAsync();
            _log.LogInfo("End turn executed");
        }
        catch (System.Exception ex)
        {
            _endTurnInProgress = false;
            _log.LogError($"End turn error: {ex}");
            ScreenReader.Instance.Say("End turn failed.");
        }
    }

    public void Sell()
    {
        if (_sellInProgress)
            return;

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
                _sellInProgress = true;
                string name = element.Label;
                Hangar.SellMinionAsync(minion);
                ScreenReader.Instance.Say($"Sold {name}.");
                _log.LogInfo($"Sell executed: {name}");
                MenuNavigator.Instance?.ScheduleShopRefresh();
            }
            catch (System.Exception ex)
            {
                _sellInProgress = false;
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

    /// <summary>Announces the remaining turn timer. Returns true if a timer was active.</summary>
    public bool AnnounceTimer()
    {
        try
        {
            var versus = Hangar?.MatchModel?.Versus;
            if (versus == null)
            {
                ScreenReader.Instance.Say("No timer.");
                return false;
            }

            // Check if this match has a meaningful turn timer.
            // Async matches use very long durations (e.g. 86400s = 24 hours) where
            // the timer is irrelevant — players end turns manually.
            int turnDuration = 0;
            try { turnDuration = versus.TurnDuration; } catch { }
            if (turnDuration <= 0 || turnDuration > 300)
            {
                ScreenReader.Instance.Say("No timer.");
                return false;
            }

            var turnEndTime = versus.TurnEndTime;
            if (!turnEndTime.HasValue)
            {
                ScreenReader.Instance.Say("No timer.");
                return false;
            }

            // Convert Il2CppSystem.DateTime to System.DateTime via ticks
            long endTicks = turnEndTime.Value.Ticks;
            var endTimeUtc = new System.DateTime(endTicks, System.DateTimeKind.Utc);
            var remaining = endTimeUtc.Subtract(System.DateTime.UtcNow);
            if (remaining.TotalSeconds <= 0)
            {
                ScreenReader.Instance.Say("Time's up.");
                return true;
            }

            int minutes = (int)remaining.TotalMinutes;
            int seconds = remaining.Seconds;

            string timeText;
            if (minutes > 0)
                timeText = $"{minutes} minute{(minutes != 1 ? "s" : "")}, {seconds} second{(seconds != 1 ? "s" : "")} remaining.";
            else
                timeText = $"{seconds} second{(seconds != 1 ? "s" : "")} remaining.";

            ScreenReader.Instance.Say(timeText);
            return true;
        }
        catch (System.Exception ex)
        {
            _log.LogError($"Timer announce error: {ex.Message}");
            return false;
        }
    }
}
