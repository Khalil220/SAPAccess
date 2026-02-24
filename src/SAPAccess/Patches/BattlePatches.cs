using BepInEx.Logging;
using HarmonyLib;
using SAPAccess.Announcements;
using SAPAccess.GameState;

namespace SAPAccess.Patches;

/// <summary>
/// Harmony hooks for the battle phase.
/// Targets BoardController.PlayBattle and battle event renderers.
/// </summary>
[HarmonyPatch]
public static class BattlePatches
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("SAPAccess.BattlePatch");

    /// <summary>Prefix on BoardController.PlayBattle — detects battle start.</summary>
    [HarmonyPatch(typeof(Spacewood.Unity.MonoBehaviours.Board.BoardController), nameof(Spacewood.Unity.MonoBehaviours.Board.BoardController.PlayBattle))]
    [HarmonyPrefix]
    public static void BoardController_PlayBattle_Prefix()
    {
        try
        {
            Log.LogInfo("Battle starting");
            GamePhaseTracker.Instance.CurrentPhase = GamePhase.Battle;
            BattleStateReader.Instance.Reset();
            BattleAnnouncer.Instance?.OnBattleStart();
        }
        catch (System.Exception ex)
        {
            Log.LogError($"PlayBattle prefix error: {ex}");
        }
    }

    // NOTE: BoardView postfix patches on DamageMinion, ActivateAbility, SummonMinion
    // have been disabled — they cause native crashes in IL2CPP because these methods
    // are async (UniTask-returning). Battle event announcements need a different
    // approach (e.g., polling BoardController state or using board event renderers).
}
