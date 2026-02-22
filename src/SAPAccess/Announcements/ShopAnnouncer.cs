using BepInEx.Logging;
using SAPAccess.Config;
using SAPAccess.GameState;
using SAPAccess.NVDA;

namespace SAPAccess.Announcements;

/// <summary>
/// Announces shop-phase events: turn start, buy, sell, roll, freeze, status.
/// </summary>
public class ShopAnnouncer
{
    public static ShopAnnouncer? Instance { get; private set; }

    private readonly ManualLogSource _log;
    private readonly ShopStateReader _shop;

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

    public void OnShopRolled()
    {
        ScreenReader.Instance.Say($"Rolled. {_shop.Gold} gold remaining.");
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

    // Keyboard-triggered methods

    public void Roll()
    {
        // Will invoke the game's roll action once hooked up
        _log.LogInfo("Roll requested via keyboard");
    }

    public void FreezeToggle()
    {
        // Will invoke the game's freeze action on current focus
        _log.LogInfo("Freeze toggle requested via keyboard");
    }

    public void EndTurn()
    {
        // Will invoke the game's end turn action once hooked up
        _log.LogInfo("End turn requested via keyboard");
    }

    public void AnnounceShop()
    {
        ScreenReader.Instance.Say(_shop.GetShopSummary());
    }

    public void AnnounceStatus()
    {
        ScreenReader.Instance.Say(_shop.GetStatusSummary());
    }
}
