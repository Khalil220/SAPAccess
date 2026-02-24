using HarmonyLib;
using BepInEx.Logging;
using SAPAccess.Announcements;
using SAPAccess.GameState;

namespace SAPAccess.Patches;

/// <summary>
/// Harmony hooks for the shop (hangar) phase.
/// Targets Spacewood.Unity.MonoBehaviours.Build.HangarMain and related UI classes.
/// </summary>
[HarmonyPatch]
public static class HangarPatches
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("SAPAccess.HangarPatch");

    /// <summary>Postfix on HangarMain.StartTurnAsync — detects new turn start.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.MonoBehaviours.Build.HangarMain), nameof(Spacewood.Unity.MonoBehaviours.Build.HangarMain.StartTurnAsync))]
    [HarmonyPostfix]
    public static void HangarMain_StartTurnAsync_Postfix(Spacewood.Unity.MonoBehaviours.Build.HangarMain __instance)
    {
        try
        {
            GamePhaseTracker.Instance.CurrentPhase = GamePhase.Shop;

            var board = __instance.BuildModel?.Board;
            if (board != null)
            {
                ShopStateReader.Instance.ReadFromBoard(board);
                TeamStateReader.Instance.ReadFromBoard(board);
            }

            ShopAnnouncer.Instance?.OnTurnStart();
        }
        catch (System.Exception ex)
        {
            Log.LogError($"StartTurnAsync patch error: {ex}");
        }
    }

    /// <summary>Postfix on HangarMain.EndTurnAsync — logs the call.
    /// NOTE: Do NOT set phase to Battle here — this postfix fires immediately
    /// when the async method is invoked, before the server confirms. If the server
    /// rejects the call (e.g. "Can't end turn"), a phantom battle cycle occurs.
    /// BattlePatches.PlayBattle_Prefix sets the phase when the actual battle starts.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.MonoBehaviours.Build.HangarMain), nameof(Spacewood.Unity.MonoBehaviours.Build.HangarMain.EndTurnAsync))]
    [HarmonyPostfix]
    public static void HangarMain_EndTurnAsync_Postfix()
    {
        try
        {
            Log.LogInfo("EndTurnAsync called");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"EndTurnAsync patch error: {ex}");
        }
    }

    /// <summary>Postfix on HangarMain.RollShopAsync — detects shop roll.
    /// Note: gold announcement is deferred to RefreshShopState (board data is stale here).</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.MonoBehaviours.Build.HangarMain), nameof(Spacewood.Unity.MonoBehaviours.Build.HangarMain.RollShopAsync))]
    [HarmonyPostfix]
    public static void HangarMain_RollShopAsync_Postfix(Spacewood.Unity.MonoBehaviours.Build.HangarMain __instance)
    {
        try
        {
            Log.LogInfo("Shop rolled");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"RollShopAsync patch error: {ex}");
        }
    }

    /// <summary>Postfix on HangarMain.SellMinionAsync — detects pet sell.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.MonoBehaviours.Build.HangarMain), "SellMinionAsync", new[] { typeof(Spacewood.Core.Models.Item.ItemId) })]
    [HarmonyPostfix]
    public static void HangarMain_SellMinionAsync_Postfix(Spacewood.Unity.MonoBehaviours.Build.HangarMain __instance)
    {
        try
        {
            var board = __instance.BuildModel?.Board;
            if (board != null)
            {
                ShopStateReader.Instance.ReadFromBoard(board);
                TeamStateReader.Instance.ReadFromBoard(board);
            }
        }
        catch (System.Exception ex)
        {
            Log.LogError($"SellMinionAsync patch error: {ex}");
        }
    }

    /// <summary>Postfix on HangarMain.FreezeItem — detects freeze toggle.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.MonoBehaviours.Build.HangarMain), nameof(Spacewood.Unity.MonoBehaviours.Build.HangarMain.FreezeItem))]
    [HarmonyPostfix]
    public static void HangarMain_FreezeItem_Postfix()
    {
        try
        {
            Log.LogInfo("Item freeze toggled");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"FreezeItem patch error: {ex}");
        }
    }

    /// <summary>Postfix on HangarGold.SetGold — tracks gold display changes.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.MonoBehaviours.Build.HangarGold), nameof(Spacewood.Unity.MonoBehaviours.Build.HangarGold.SetGold))]
    [HarmonyPostfix]
    public static void HangarGold_SetGold_Postfix(int gold)
    {
        try
        {
            ShopStateReader.Instance.Gold = gold;
            Log.LogDebug($"Gold: {gold}");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"SetGold patch error: {ex}");
        }
    }

    /// <summary>Postfix on HangarLives.Set — tracks lives display changes.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.MonoBehaviours.Build.HangarLives), nameof(Spacewood.Unity.MonoBehaviours.Build.HangarLives.Set))]
    [HarmonyPostfix]
    public static void HangarLives_Set_Postfix(int current, int max)
    {
        try
        {
            ShopStateReader.Instance.Lives = current;
            Log.LogDebug($"Lives: {current}/{max}");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"HangarLives.Set patch error: {ex}");
        }
    }

    /// <summary>Postfix on HangarTurns.Set — tracks turn display changes.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.MonoBehaviours.Build.HangarTurns), nameof(Spacewood.Unity.MonoBehaviours.Build.HangarTurns.Set))]
    [HarmonyPostfix]
    public static void HangarTurns_Set_Postfix(int turn)
    {
        try
        {
            ShopStateReader.Instance.Turn = turn;
            Log.LogDebug($"Turn: {turn}");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"HangarTurns.Set patch error: {ex}");
        }
    }
}
