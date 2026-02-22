using HarmonyLib;
using BepInEx.Logging;
using SAPAccess.Announcements;
using SAPAccess.GameState;

namespace SAPAccess.Patches;

/// <summary>
/// Harmony hooks for the shop (hangar) phase.
/// Patches HangarMain, HangarStateMachine, HangarGold, HangarRoll, etc.
///
/// NOTE: Exact method signatures will be determined after running Il2CppDumper.
/// These patches use placeholder class/method names based on known class names
/// from metadata analysis. They will be updated with correct signatures in Phase 2.
/// </summary>
public static class HangarPatches
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("SAPAccess.HangarPatch");

    /// <summary>
    /// Called when the shop phase begins (HangarMain initializes or a new turn starts).
    /// Triggers shop state reading and announcements.
    /// </summary>
    public static void OnShopPhaseEnter()
    {
        Log.LogInfo("Shop phase entered");
        GamePhaseTracker.Instance.CurrentPhase = GamePhase.Shop;
        ShopAnnouncer.Instance?.OnTurnStart();
    }

    /// <summary>
    /// Called when gold changes (buy, roll, sell, etc.).
    /// Updates the ShopStateReader.
    /// </summary>
    public static void OnGoldChanged(int newGold)
    {
        ShopStateReader.Instance.Gold = newGold;
        Log.LogDebug($"Gold changed to {newGold}");
    }

    /// <summary>
    /// Called when the player rolls the shop.
    /// Triggers shop content re-read and announcement.
    /// </summary>
    public static void OnRoll()
    {
        Log.LogInfo("Shop rolled");
        ShopAnnouncer.Instance?.OnShopRolled();
    }

    /// <summary>
    /// Called when a pet or food is frozen/unfrozen.
    /// </summary>
    public static void OnFreezeToggle(int slotIndex, bool frozen)
    {
        Log.LogInfo($"Slot {slotIndex} freeze: {frozen}");
    }

    /// <summary>
    /// Called when the player ends their turn (starts battle).
    /// </summary>
    public static void OnEndTurn()
    {
        Log.LogInfo("Turn ended, battle starting");
        GamePhaseTracker.Instance.CurrentPhase = GamePhase.Battle;
    }

    /// <summary>
    /// Called when a pet is bought from the shop.
    /// </summary>
    public static void OnBuy(string petName, int cost)
    {
        Log.LogInfo($"Bought {petName} for {cost} gold");
        ShopAnnouncer.Instance?.OnBuy(petName, cost);
    }

    /// <summary>
    /// Called when a pet is sold from the team.
    /// </summary>
    public static void OnSell(string petName, int goldGained)
    {
        Log.LogInfo($"Sold {petName} for {goldGained} gold");
        ShopAnnouncer.Instance?.OnSell(petName, goldGained);
    }

    // =========================================================================
    // Harmony patch methods below will be wired up once interop DLLs are available.
    // Example of what they will look like:
    //
    // [HarmonyPatch(typeof(Il2Cpp.Spacewood.Unity.HangarMain), "Open")]
    // [HarmonyPostfix]
    // public static void HangarMain_Open_Postfix()
    // {
    //     OnShopPhaseEnter();
    // }
    //
    // [HarmonyPatch(typeof(Il2Cpp.Spacewood.Unity.HangarGold), "SetGold")]
    // [HarmonyPostfix]
    // public static void HangarGold_SetGold_Postfix(int gold)
    // {
    //     OnGoldChanged(gold);
    // }
    // =========================================================================
}
