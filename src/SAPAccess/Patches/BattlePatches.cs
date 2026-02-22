using BepInEx.Logging;
using SAPAccess.Announcements;
using SAPAccess.GameState;

namespace SAPAccess.Patches;

/// <summary>
/// Harmony hooks for the battle phase.
/// Patches BattleController, BattleModel, BattlePhase, etc.
///
/// NOTE: Exact method signatures will be determined after running Il2CppDumper.
/// </summary>
public static class BattlePatches
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("SAPAccess.BattlePatch");

    /// <summary>Called when a battle begins.</summary>
    public static void OnBattleStart()
    {
        Log.LogInfo("Battle started");
        GamePhaseTracker.Instance.CurrentPhase = GamePhase.Battle;
        BattleStateReader.Instance.Reset();
        BattleAnnouncer.Instance?.OnBattleStart();
    }

    /// <summary>Called when a pet attacks another.</summary>
    public static void OnAttack(string attackerName, int attackerDmg, string defenderName, int defenderDmg)
    {
        var evt = new BattleEvent
        {
            Type = BattleEventType.Attack,
            SourcePet = attackerName,
            TargetPet = defenderName,
            Damage = attackerDmg,
            Description = $"{attackerName} attacks {defenderName} for {attackerDmg} damage"
        };
        BattleStateReader.Instance.QueueEvent(evt);
    }

    /// <summary>Called when a pet faints.</summary>
    public static void OnFaint(string petName, bool isFriendly)
    {
        var evt = new BattleEvent
        {
            Type = BattleEventType.Faint,
            SourcePet = petName,
            Description = isFriendly
                ? $"Your {petName} fainted"
                : $"Enemy {petName} fainted"
        };
        BattleStateReader.Instance.QueueEvent(evt);

        if (isFriendly)
            BattleStateReader.Instance.FriendlyPetsRemaining--;
        else
            BattleStateReader.Instance.EnemyPetsRemaining--;
    }

    /// <summary>Called when a pet ability triggers.</summary>
    public static void OnAbilityTrigger(string petName, string abilityDescription)
    {
        var evt = new BattleEvent
        {
            Type = BattleEventType.AbilityTrigger,
            SourcePet = petName,
            Description = $"{petName}: {abilityDescription}"
        };
        BattleStateReader.Instance.QueueEvent(evt);
    }

    /// <summary>Called when a pet is summoned during battle.</summary>
    public static void OnSummon(string petName, bool isFriendly)
    {
        var evt = new BattleEvent
        {
            Type = BattleEventType.Summon,
            SourcePet = petName,
            Description = isFriendly
                ? $"Your {petName} was summoned"
                : $"Enemy {petName} was summoned"
        };
        BattleStateReader.Instance.QueueEvent(evt);
    }

    /// <summary>Called when the battle ends.</summary>
    public static void OnBattleEnd(BattleOutcome outcome)
    {
        Log.LogInfo($"Battle ended: {outcome}");
        BattleStateReader.Instance.LastOutcome = outcome;
        GamePhaseTracker.Instance.CurrentPhase = GamePhase.BattleResult;
        BattleAnnouncer.Instance?.OnBattleEnd(outcome);
    }

    // =========================================================================
    // Harmony patch methods will be wired up once interop DLLs are available.
    // Example:
    //
    // [HarmonyPatch(typeof(Il2Cpp.Spacewood.Unity.BattleController), "StartBattle")]
    // [HarmonyPostfix]
    // public static void BattleController_StartBattle_Postfix()
    // {
    //     OnBattleStart();
    // }
    // =========================================================================
}
