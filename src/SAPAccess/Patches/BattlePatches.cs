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

    /// <summary>
    /// Postfix on BoardView.DamageMinion — announces damage events.
    /// This fires when a pet takes damage during battle rendering.
    /// </summary>
    [HarmonyPatch(typeof(Spacewood.Unity.Views.BoardView), nameof(Spacewood.Unity.Views.BoardView.DamageMinion))]
    [HarmonyPostfix]
    public static void BoardView_DamageMinion_Postfix(
        Spacewood.Core.Models.Item.ItemId targetId,
        Spacewood.Core.Models.MinionModel minion,
        int amount)
    {
        try
        {
            string name = GetMinionName(minion);
            bool isFriendly = minion.Owner == Spacewood.Core.Enums.Owner.Player;
            string side = isFriendly ? "Your" : "Enemy";
            var evt = new BattleEvent
            {
                Type = BattleEventType.Hurt,
                SourcePet = name,
                Damage = amount,
                Description = $"{side} {name} takes {amount} damage"
            };
            BattleStateReader.Instance.QueueEvent(evt);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"DamageMinion patch error: {ex}");
        }
    }

    /// <summary>
    /// Postfix on BoardView.SellMinion — visual sell confirmation during shop.
    /// </summary>
    [HarmonyPatch(typeof(Spacewood.Unity.Views.BoardView), nameof(Spacewood.Unity.Views.BoardView.SellMinion))]
    [HarmonyPostfix]
    public static void BoardView_SellMinion_Postfix(Spacewood.Core.Models.Item.ItemId minionId)
    {
        try
        {
            Log.LogInfo($"Pet sold (view): {minionId}");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"SellMinion patch error: {ex}");
        }
    }

    /// <summary>
    /// Postfix on BoardView.ActivateAbility — announces ability activation.
    /// </summary>
    [HarmonyPatch(typeof(Spacewood.Unity.Views.BoardView), nameof(Spacewood.Unity.Views.BoardView.ActivateAbility))]
    [HarmonyPostfix]
    public static void BoardView_ActivateAbility_Postfix(Spacewood.Core.Models.MinionModel minion)
    {
        try
        {
            string name = GetMinionName(minion);
            bool isFriendly = minion.Owner == Spacewood.Core.Enums.Owner.Player;
            string side = isFriendly ? "Your" : "Enemy";
            var evt = new BattleEvent
            {
                Type = BattleEventType.AbilityTrigger,
                SourcePet = name,
                Description = $"{side} {name} activates ability"
            };
            BattleStateReader.Instance.QueueEvent(evt);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"ActivateAbility patch error: {ex}");
        }
    }

    /// <summary>
    /// Postfix on BoardView.SummonMinion (sync version) — announces summons.
    /// </summary>
    [HarmonyPatch(typeof(Spacewood.Unity.Views.BoardView), "SummonMinion",
        new[] { typeof(Spacewood.Core.Models.MinionModel), typeof(Spacewood.Core.Enums.SummonType) })]
    [HarmonyPostfix]
    public static void BoardView_SummonMinion_Postfix(
        Spacewood.Core.Models.MinionModel minion,
        Spacewood.Core.Enums.SummonType summonType)
    {
        try
        {
            string name = GetMinionName(minion);
            bool isFriendly = minion.Owner == Spacewood.Core.Enums.Owner.Player;
            string side = isFriendly ? "Your" : "Enemy";
            var evt = new BattleEvent
            {
                Type = BattleEventType.Summon,
                SourcePet = name,
                Description = $"{side} {name} summoned"
            };
            BattleStateReader.Instance.QueueEvent(evt);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"SummonMinion patch error: {ex}");
        }
    }

    private static string GetMinionName(Spacewood.Core.Models.MinionModel minion)
    {
        try
        {
            return Spacewood.Unity.Extensions.MinionModelExtensions.GetNameLocalized(minion) ?? minion.Enum.ToString();
        }
        catch
        {
            return minion.Enum.ToString();
        }
    }
}
