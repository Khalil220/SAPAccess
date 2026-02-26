using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using SAPAccess.Config;
using SAPAccess.GameState;
using SAPAccess.NVDA;
using UnityEngine;

namespace SAPAccess.Announcements;

/// <summary>
/// Announces battle-phase events: team intro, attacks, faints, abilities, summons, outcome.
/// Polls BattleController for the precomputed EventLog, parses events, and queues them
/// to BattleStateReader for frame-by-frame screen reader output.
/// </summary>
public class BattleAnnouncer : MonoBehaviour
{
    public static BattleAnnouncer? Instance { get; private set; }

    private ManualLogSource? _log;
    private float _lastAnnouncementTime;

    // Battle controller polling
    private Spacewood.Unity.MonoBehaviours.Battle.BattleController? _cachedBattleCtrl;
    private bool _battleActive;
    private bool _petLookupBuilt;

    // Early team announcement: poll for boards during intro animation
    private bool _awaitingTeamAnnounce;
    private string? _earlyPlayerName;
    private string? _earlyPlayerTeamName;
    private string? _earlyOpponentName;
    private string? _earlyOpponentTeamName;

    // Incremental event reading from RenderEvents (populated during battle animation)
    private Il2CppSystem.Collections.Generic.List<BoardEvents.Interfaces.IBoardEvent>? _renderEvents;
    private int _lastProcessedIndex;
    private bool _battleComplete;
    // Also track ResolverEvents for pre-battle events (start-of-battle abilities)
    private Il2CppSystem.Collections.Generic.List<BoardEvents.Interfaces.IBoardEvent>? _resolverEvents;
    private int _resolverLastIndex;

    // Pet name lookup: "BoardId:Unique" -> (name, ownerValue)
    // Owner: 1 = Player, 2 = Opponent
    private Dictionary<string, (string name, int owner)>? _petLookup;

    public void Awake()
    {
        Instance = this;
        _log = BepInEx.Logging.Logger.CreateLogSource("SAPAccess.BattleAnnounce");
    }

    public void Update()
    {
        // During intro animation: poll for boards and announce teams as soon as available
        if (_awaitingTeamAnnounce)
        {
            try
            {
                var ctrl = FindBattleController();
                if (ctrl != null)
                {
                    bool hasBoards = false;
                    try { hasBoards = ctrl.PlayerBoardModel?.Minions?.Items != null; } catch { }
                    if (hasBoards)
                    {
                        _awaitingTeamAnnounce = false;
                        AnnounceTeams(ctrl);
                    }
                }
            }
            catch { }
        }

        if (!_battleActive)
            return;

        // Poll for new events incrementally
        PollNewEvents();

        // Dequeue and announce events with configurable delay
        float delay = ModConfig.Instance?.SpeechDelay.Value ?? 0.1f;
        if (Time.time - _lastAnnouncementTime < delay)
            return;

        var evt = BattleStateReader.Instance.DequeueEvent();
        if (evt == null)
        {
            // All events announced and battle complete — deactivate
            if (_battleComplete && BattleStateReader.Instance.AllEventsAnnounced)
            {
                _battleActive = false;
                _log?.LogInfo("All battle events announced");
            }
            return;
        }

        bool detailed = ModConfig.Instance?.AnnounceBattleDetails.Value ?? true;

        if (detailed || evt.Type == BattleEventType.Faint)
        {
            ScreenReader.Instance.SayQueued(evt.Description);
            _lastAnnouncementTime = Time.time;
        }
    }

    /// <summary>
    /// Called from BattleController.Start postfix — announces teams during intro animation,
    /// seconds before combat begins. Uses the instance directly since singleton may not be cached.
    /// </summary>
    /// <summary>
    /// Called from BattleController.Start postfix — captures names and starts polling
    /// for boards during the intro animation. Teams are announced as soon as boards appear.
    /// </summary>
    public void AnnounceTeamsEarly(Spacewood.Unity.MonoBehaviours.Battle.BattleController ctrl)
    {
        try
        {
            try { _earlyPlayerName = ctrl._playerName; } catch { }
            try { _earlyPlayerTeamName = ctrl._playerTeamName; } catch { }
            try { _earlyOpponentName = ctrl._opponentName; } catch { }
            try { _earlyOpponentTeamName = ctrl._opponentTeamName; } catch { }

            _cachedBattleCtrl = ctrl;
            _awaitingTeamAnnounce = true;
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"Early team intro failed: {ex.Message}");
        }
    }

    public void OnBattleStart()
    {
        _battleComplete = false;
        _announcedDescriptions = new HashSet<string>();
        _renderEvents = null;
        _resolverEvents = null;
        _lastProcessedIndex = 0;
        _resolverLastIndex = 0;
        _awaitingTeamAnnounce = false; // Stop intro polling, combat is starting

        // Build pet lookup if not already done during intro
        if (!_petLookupBuilt)
        {
            _petLookup = null;
            _cachedBattleCtrl = null;
            var ctrl = FindBattleController();
            if (ctrl != null)
            {
                BuildPetLookup(ctrl);
                _petLookupBuilt = true;
            }
        }

        ScreenReader.Instance.SayQueued("Battle starting.");
        _battleActive = true;
    }

    public void OnBattleEnd(BattleOutcome outcome)
    {
        _battleActive = false;

        string msg = outcome switch
        {
            BattleOutcome.Win => "You won!",
            BattleOutcome.Loss => "You lost.",
            BattleOutcome.Draw => "Draw.",
            _ => "Battle ended."
        };

        var reader = BattleStateReader.Instance;
        if (reader.FriendlyPetsRemaining > 0 || reader.EnemyPetsRemaining > 0)
        {
            msg += $" {reader.FriendlyPetsRemaining} friendly, {reader.EnemyPetsRemaining} enemy remaining.";
        }

        ScreenReader.Instance.Say(msg);
    }

    // ── Team Intro ──────────────────────────────────────────────────

    private void AnnounceTeams(Spacewood.Unity.MonoBehaviours.Battle.BattleController? ctrl = null)
    {
        ctrl ??= FindBattleController();
        if (ctrl == null) return;

        // Build pet lookup from this controller if not done yet
        if (!_petLookupBuilt)
        {
            _petLookup = new Dictionary<string, (string, int)>();
            BuildPetLookup(ctrl);
            _petLookupBuilt = true;
        }

        // Try reading player/team names (fall back to early-captured names)
        string? playerName = null;
        string? playerTeamName = null;
        string? opponentName = null;
        string? opponentTeamName = null;
        try { playerName = ctrl._playerName; } catch { }
        try { playerTeamName = ctrl._playerTeamName; } catch { }
        try { opponentName = ctrl._opponentName; } catch { }
        try { opponentTeamName = ctrl._opponentTeamName; } catch { }
        playerName ??= _earlyPlayerName;
        playerTeamName ??= _earlyPlayerTeamName;
        opponentName ??= _earlyOpponentName;
        opponentTeamName ??= _earlyOpponentTeamName;

        // List player/team names but only opponent pets — player knows their own team
        string opponentTeam = BuildTeamString(ctrl.OpponentBoardModel);

        string playerLabel = BuildTeamLabel(playerName, playerTeamName, "Your team");
        string opponentLabel = BuildTeamLabel(opponentName, opponentTeamName, "Enemy team");

        if (!string.IsNullOrEmpty(opponentTeam))
        {
            ScreenReader.Instance.Say($"{playerLabel} vs {opponentLabel}: {opponentTeam}.");
            _log?.LogInfo($"Battle intro: {playerLabel} vs {opponentLabel}: {opponentTeam}");
        }
    }

    private static string BuildTeamLabel(string? playerName, string? teamName, string fallback)
    {
        bool hasPlayer = !string.IsNullOrWhiteSpace(playerName);
        bool hasTeam = !string.IsNullOrWhiteSpace(teamName);

        if (hasPlayer && hasTeam)
            return $"{playerName}'s {teamName}";
        if (hasPlayer)
            return playerName!;
        if (hasTeam)
            return teamName!;
        return fallback;
    }

    private string BuildTeamString(Spacewood.Core.Models.BoardModel? board)
    {
        if (board?.Minions?.Items == null) return "";

        var names = new List<string>();
        for (int i = 0; i < board.Minions.Items.Count; i++)
        {
            var m = board.Minions.Items[i];
            if (m == null || m.Dead) continue;

            string name = GetMinionName(m);
            int atk = m.Attack?.Total ?? 0;
            int hp = m.Health?.Total ?? 0;
            names.Add($"{name} {atk}/{hp}");
        }
        return string.Join(", ", names);
    }

    // ── EventLog Polling ────────────────────────────────────────────

    // Track event descriptions already announced to avoid duplicates across event sources
    private HashSet<string>? _announcedDescriptions;

    /// <summary>
    /// Polls both ResolverEvents and RenderEvents from BoardController each frame.
    ///
    /// ResolverEvents: populated immediately with pre-battle setup events (MoveMinion,
    ///   start-of-battle abilities like Mosquito/Dolphin).
    /// RenderEvents: populated incrementally during battle animation with ALL combat events
    ///   (MinionTrade, DamageMinion, MarkMinionDead, SummonMinion, etc.).
    ///
    /// Both lists are deduped by description string to avoid announcing the same event twice.
    /// </summary>
    private int _pollCount;

    private void PollNewEvents()
    {
        if (_battleComplete) return;

        _pollCount++;

        try
        {
            // Build pet lookup (once, from BattleController boards)
            if (!_petLookupBuilt)
            {
                var ctrl = FindBattleController();
                if (ctrl != null)
                {
                    BuildPetLookup(ctrl);
                    _petLookupBuilt = true;
                }
            }

            // Find BoardController and its event lists (once)
            if (_renderEvents == null || _resolverEvents == null)
            {
                try
                {
                    var boardCtrl = Object.FindObjectOfType<
                        Spacewood.Unity.MonoBehaviours.Board.BoardController>();
                    if (boardCtrl != null)
                    {
                        _resolverEvents ??= boardCtrl.ResolverEvents;
                        _renderEvents ??= boardCtrl.RenderEvents;
                    }
                }
                catch { }
            }

            // Poll ResolverEvents for pre-battle events (start-of-battle abilities)
            if (_resolverEvents != null)
            {
                int count = _resolverEvents.Count;
                if (count > _resolverLastIndex)
                {
                    for (int i = _resolverLastIndex; i < count; i++)
                        ProcessResolverEvent(i);
                    _resolverLastIndex = count;
                }
            }

            // Poll RenderEvents for combat events (grows during battle animation)
            if (_renderEvents != null)
            {
                int count = _renderEvents.Count;
                if (count > _lastProcessedIndex)
                {
                    for (int i = _lastProcessedIndex; i < count; i++)
                        ProcessRenderEvent(i);
                    _lastProcessedIndex = count;
                }
            }

            // Detect battle end: BattleController destroyed after scene transition
            if (_pollCount % 60 == 0 && _lastProcessedIndex > 0)
            {
                var ctrl = FindBattleController();
                if (ctrl == null)
                {
                    _battleComplete = true;
                    _log?.LogInfo("All battle events processed");
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"PollNewEvents error: {ex.Message}");
        }
    }

    private void BuildPetLookup(Spacewood.Unity.MonoBehaviours.Battle.BattleController ctrl)
    {
        _petLookup = new Dictionary<string, (string, int)>();

        // Primary: BattleController's board models (original BoardIds)
        try { AddMinionsToLookup(ctrl.PlayerBoardModel, 1); } catch { }
        try { AddMinionsToLookup(ctrl.OpponentBoardModel, 2); } catch { }

        // Also scan the resolver's merged board if available
        TryScanResolverBoard(ctrl);

        _log?.LogInfo($"Pet lookup built: {_petLookup.Count} entries");
    }

    /// <summary>
    /// Tries to scan the resolver's merged BoardModel to add pets with resolver BoardIds.
    /// Called during initial lookup build and lazily when unknown BoardIds are encountered.
    /// Not one-shot — the board changes as events play, so we rescan on each miss.
    /// </summary>

    private void TryScanResolverBoard(Spacewood.Unity.MonoBehaviours.Battle.BattleController? ctrl = null)
    {
        ctrl ??= FindBattleController();
        if (ctrl == null) return;

        // Try BoardView's current board model (the resolver's merged board)
        try
        {
            var boardCtrl = Object.FindObjectOfType<
                Spacewood.Unity.MonoBehaviours.Board.BoardController>();
            if (boardCtrl?.BoardView != null)
            {
                var boardModel = boardCtrl.BoardView.Model;
                if (boardModel?.Minions?.Items != null)
                {
                    AddMinionsFromMergedBoard(boardModel);
                }
            }
        }
        catch { }
    }

    private void AddMinionsToLookup(Spacewood.Core.Models.BoardModel? board, int owner)
    {
        if (board?.Minions?.Items == null) return;

        for (int i = 0; i < board.Minions.Items.Count; i++)
        {
            var m = board.Minions.Items[i];
            if (m == null) continue;

            string name = GetMinionName(m);
            try
            {
                var id = m.Id;
                string key = $"{id.BoardId}:{id.Unique}";
                _petLookup![key] = (name, owner);
                _log?.LogInfo($"  Pet lookup: [{key}] = {name} (owner={owner})");
            }
            catch { }
        }
    }

    /// <summary>
    /// Adds pets from the resolver's merged BoardModel, using each pet's Owner field.
    /// This ensures events referencing the resolver's BoardId can resolve pet names.
    /// </summary>
    private void AddMinionsFromMergedBoard(Spacewood.Core.Models.BoardModel board)
    {
        for (int i = 0; i < board.Minions.Items.Count; i++)
        {
            var m = board.Minions.Items[i];
            if (m == null) continue;

            string name = GetMinionName(m);
            int owner = (int)m.Owner;
            try
            {
                var id = m.Id;
                string key = $"{id.BoardId}:{id.Unique}";
                if (!_petLookup!.ContainsKey(key))
                {
                    _petLookup[key] = (name, owner);
                    _log?.LogInfo($"  Pet lookup (resolver): [{key}] = {name} (owner={owner})");
                }
            }
            catch { }
        }
    }

    private void ProcessResolverEvent(int i)
    {
        try
        {
            var boardEvent = _resolverEvents![i];
            if (boardEvent == null) return;
            ProcessBoardEvent(boardEvent, i, dedup: true);
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"  [resolver:{i}] Error: {ex.Message}");
        }
    }

    private void ProcessRenderEvent(int i)
    {
        try
        {
            var boardEvent = _renderEvents![i];
            if (boardEvent == null) return;
            // RenderEvents may overlap with ResolverEvents — dedup by description
            ProcessBoardEvent(boardEvent, i, dedup: true);
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"  [render:{i}] Error: {ex.Message}");
        }
    }

    private void ProcessBoardEvent(BoardEvents.Interfaces.IBoardEvent boardEvent, int i, bool dedup = false)
    {
        var obj = boardEvent.Cast<Il2CppSystem.Object>();

        string fullTypeName = "";
        try { fullTypeName = obj.GetIl2CppType().FullName ?? ""; } catch { }

        int ownerVal = 0;
        try { ownerVal = (int)boardEvent.Owner; } catch { }

        // Only process Done<T> events (completed actions)
        if (!fullTypeName.Contains("Done")) return;

        BattleEvent? evt = null;

        if (fullTypeName.Contains("MinionTrade"))
            evt = TryFormatTrade(obj, ownerVal);
        else if (fullTypeName.Contains("MinionAbilityDamage"))
            evt = TryFormatAbilityDamage(obj, ownerVal);
        else if (fullTypeName.Contains("DamageMinion"))
            evt = TryFormatDamage(obj, ownerVal);
        else if (fullTypeName.Contains("MarkMinionDead"))
            evt = TryFormatFaint(obj, ownerVal);
        // DestroyMinion skipped — MarkMinionDead already announces faints
        else if (fullTypeName.Contains("SummonMinion"))
            evt = TryFormatSummon(obj, ownerVal);
        else if (fullTypeName.Contains("AbilityActivate"))
            evt = TryFormatAbilityActivate(obj, ownerVal);
        else if (fullTypeName.Contains("AbilityTriggered"))
            evt = TryFormatAbility(obj, ownerVal);
        else if (fullTypeName.Contains("BuffMinion"))
            evt = TryFormatBuff(obj, ownerVal);

        if (evt != null)
        {
            // Skip genuine replays (same event re-emitted in RenderEvents)
            // DedupKey includes ItemIds, so different pets with the same name are distinguishable
            string key = evt.DedupKey;
            if (string.IsNullOrEmpty(key)) key = evt.Description;

            if (dedup && _announcedDescriptions != null && _announcedDescriptions.Contains(key))
                return;

            BattleStateReader.Instance.QueueEvent(evt);
            _announcedDescriptions?.Add(key);

            // When ability damage is announced, suppress the redundant DamageMinion
            // that follows with the same target and amount
            if (evt.Type == BattleEventType.Hurt && key.StartsWith("AbilityDmg:"))
            {
                string damageKey = "Damage:" + key.Substring("AbilityDmg:".Length);
                _announcedDescriptions?.Add(damageKey);
            }
        }
    }

    // ── Event Formatters ────────────────────────────────────────────

    private BattleEvent? TryFormatDamage(Il2CppSystem.Object obj, int ownerVal)
    {
        try
        {
            var done = obj.TryCast<
                Spacewood.Core.Models.BoardResolver.BoardEvents.Wrappers.Done<BoardEvents.DamageMinion>>();
            var dmg = done?.Event ?? GetInnerEvent<BoardEvents.DamageMinion>(obj);
            if (dmg == null) return null;

            int amount = dmg.Amount;
            string targetName = ResolvePetName(dmg.TargetId, ownerVal, out int targetOwner);
            string prefix = OwnerPrefix(targetOwner);

            string desc = $"{prefix}{targetName} takes {amount} damage";
            if (dmg.CriticalHit)
                desc += ", critical hit";

            return new BattleEvent
            {
                Type = BattleEventType.Hurt,
                Description = desc,
                TargetPet = targetName,
                Damage = amount,
                DedupKey = $"Damage:{IdKey(dmg.TargetId)}:{amount}"
            };
        }
        catch (System.Exception ex)
        {
            _log?.LogDebug($"DamageMinion parse error: {ex.Message}");
            return null;
        }
    }

    private BattleEvent? TryFormatFaint(Il2CppSystem.Object obj, int ownerVal)
    {
        try
        {
            var done = obj.TryCast<
                Spacewood.Core.Models.BoardResolver.BoardEvents.Wrappers.Done<BoardEvents.MarkMinionDead>>();
            if (done == null)
            {
                // Fallback: reflection-based access
                return TryFormatFaintReflection(obj, ownerVal);
            }

            var dead = done.Event;
            if (dead == null) return null;

            string targetName = ResolvePetName(dead.TargetId, ownerVal, out int targetOwner);
            string prefix = OwnerPrefix(targetOwner);

            return new BattleEvent
            {
                Type = BattleEventType.Faint,
                Description = $"{prefix}{targetName} faints",
                TargetPet = targetName,
                DedupKey = $"Faint:{IdKey(dead.TargetId)}"
            };
        }
        catch (System.Exception ex)
        {
            _log?.LogDebug($"MarkMinionDead parse error: {ex.Message}");
            return null;
        }
    }

    private BattleEvent? TryFormatFaintReflection(Il2CppSystem.Object obj, int ownerVal)
    {
        var inner = GetInnerEvent<BoardEvents.MarkMinionDead>(obj);
        if (inner == null) return null;

        string targetName = ResolvePetName(inner.TargetId, ownerVal, out int targetOwner);
        string prefix = OwnerPrefix(targetOwner);

        return new BattleEvent
        {
            Type = BattleEventType.Faint,
            Description = $"{prefix}{targetName} faints",
            TargetPet = targetName,
            DedupKey = $"Faint:{IdKey(inner.TargetId)}"
        };
    }

    private BattleEvent? TryFormatTrade(Il2CppSystem.Object obj, int ownerVal)
    {
        try
        {
            var done = obj.TryCast<
                Spacewood.Core.Models.BoardResolver.BoardEvents.Wrappers.Done<BoardEvents.MinionTrade>>();
            if (done == null)
            {
                return TryFormatTradeReflection(obj, ownerVal);
            }

            var trade = done.Event;
            if (trade == null) return null;

            string attackerName = ResolvePetName(trade.attackerId, ownerVal, out int attackerOwner);
            string defenderName = ResolvePetName(trade.defenderId, 0, out int defenderOwner);

            string atkPrefix = OwnerPrefix(attackerOwner);
            string defPrefix = OwnerPrefix(defenderOwner);

            return new BattleEvent
            {
                Type = BattleEventType.Attack,
                Description = $"{atkPrefix}{attackerName} attacks {defPrefix}{defenderName}",
                SourcePet = attackerName,
                TargetPet = defenderName,
                DedupKey = $"Trade:{IdKey(trade.attackerId)}:{IdKey(trade.defenderId)}"
            };
        }
        catch (System.Exception ex)
        {
            _log?.LogDebug($"MinionTrade parse error: {ex.Message}");
            return null;
        }
    }

    private BattleEvent? TryFormatTradeReflection(Il2CppSystem.Object obj, int ownerVal)
    {
        var trade = GetInnerEvent<BoardEvents.MinionTrade>(obj);
        if (trade == null) return null;

        string attackerName = ResolvePetName(trade.attackerId, ownerVal, out int attackerOwner);
        string defenderName = ResolvePetName(trade.defenderId, 0, out int defenderOwner);

        string atkPrefix = OwnerPrefix(attackerOwner);
        string defPrefix = OwnerPrefix(defenderOwner);

        return new BattleEvent
        {
            Type = BattleEventType.Attack,
            Description = $"{atkPrefix}{attackerName} attacks {defPrefix}{defenderName}",
            SourcePet = attackerName,
            TargetPet = defenderName,
            DedupKey = $"Trade:{IdKey(trade.attackerId)}:{IdKey(trade.defenderId)}"
        };
    }

    private BattleEvent? TryFormatAbilityDamage(Il2CppSystem.Object obj, int ownerVal)
    {
        try
        {
            var done = obj.TryCast<
                Spacewood.Core.Models.BoardResolver.BoardEvents.Wrappers.Done<BoardEvents.MinionAbilityDamage>>();
            var dmg = done?.Event ?? GetInnerEvent<BoardEvents.MinionAbilityDamage>(obj);
            if (dmg == null) return null;

            int amount = dmg.Amount;
            string targetName = ResolvePetName(dmg.TargetId, ownerVal, out int targetOwner);
            string prefix = OwnerPrefix(targetOwner);

            return new BattleEvent
            {
                Type = BattleEventType.Hurt,
                Description = $"{prefix}{targetName} takes {amount} ability damage",
                TargetPet = targetName,
                Damage = amount,
                DedupKey = $"AbilityDmg:{IdKey(dmg.TargetId)}:{amount}"
            };
        }
        catch (System.Exception ex)
        {
            _log?.LogDebug($"MinionAbilityDamage parse error: {ex.Message}");
            return null;
        }
    }

    private BattleEvent? TryFormatSummon(Il2CppSystem.Object obj, int ownerVal)
    {
        try
        {
            var done = obj.TryCast<
                Spacewood.Core.Models.BoardResolver.BoardEvents.Wrappers.Done<BoardEvents.SummonMinion>>();
            var summon = done?.Event ?? GetInnerEvent<BoardEvents.SummonMinion>(obj);
            if (summon == null) return null;

            string petName = "pet";
            try
            {
                if (summon.Minion != null)
                {
                    petName = GetMinionName(summon.Minion);
                    // Add to lookup for future reference
                    try
                    {
                        var id = summon.Minion.Id;
                        string key = $"{id.BoardId}:{id.Unique}";
                        _petLookup![key] = (petName, ownerVal);
                    }
                    catch { }
                }
            }
            catch { }

            string teamLabel = ownerVal == 1 ? "your team" : "enemy team";
            string summonKey = "Summon:" + ownerVal;
            try { if (summon.Minion != null) summonKey = $"Summon:{IdKey(summon.Minion.Id)}"; } catch { }

            return new BattleEvent
            {
                Type = BattleEventType.Summon,
                Description = $"{petName} summoned to {teamLabel}",
                SourcePet = petName,
                DedupKey = summonKey
            };
        }
        catch (System.Exception ex)
        {
            _log?.LogDebug($"SummonMinion parse error: {ex.Message}");
            return null;
        }
    }

    private BattleEvent? TryFormatAbility(Il2CppSystem.Object obj, int ownerVal)
    {
        try
        {
            var done = obj.TryCast<
                Spacewood.Core.Models.BoardResolver.BoardEvents.Wrappers.Done<BoardEvents.AbilityTriggered>>();
            var ability = done?.Event ?? GetInnerEvent<BoardEvents.AbilityTriggered>(obj);
            if (ability == null) return null;

            string petName = ResolvePetName(ability.MinionId, ownerVal, out int petOwner);
            string prefix = OwnerPrefix(petOwner);

            return new BattleEvent
            {
                Type = BattleEventType.AbilityTrigger,
                Description = $"{prefix}{petName} triggers",
                SourcePet = petName,
                DedupKey = $"Ability:{IdKey(ability.MinionId)}"
            };
        }
        catch (System.Exception ex)
        {
            _log?.LogDebug($"AbilityTriggered parse error: {ex.Message}");
            return null;
        }
    }

    private BattleEvent? TryFormatAbilityActivate(Il2CppSystem.Object obj, int ownerVal)
    {
        try
        {
            var done = obj.TryCast<
                Spacewood.Core.Models.BoardResolver.BoardEvents.Wrappers.Done<BoardEvents.AbilityActivate>>();
            var activate = done?.Event ?? GetInnerEvent<BoardEvents.AbilityActivate>(obj);
            if (activate == null) return null;

            string petName = ResolvePetName(activate.MinionId, ownerVal, out int petOwner);
            string prefix = OwnerPrefix(petOwner);

            return new BattleEvent
            {
                Type = BattleEventType.AbilityTrigger,
                Description = $"{prefix}{petName} triggers",
                SourcePet = petName,
                DedupKey = $"Ability:{IdKey(activate.MinionId)}"
            };
        }
        catch (System.Exception ex)
        {
            _log?.LogDebug($"AbilityActivate parse error: {ex.Message}");
            return null;
        }
    }

    private BattleEvent? TryFormatBuff(Il2CppSystem.Object obj, int ownerVal)
    {
        try
        {
            var done = obj.TryCast<
                Spacewood.Core.Models.BoardResolver.BoardEvents.Wrappers.Done<BoardEvents.BuffMinion>>();
            var buff = done?.Event ?? GetInnerEvent<BoardEvents.BuffMinion>(obj);
            if (buff == null) return null;

            string targetName = ResolvePetName(buff.TargetId, ownerVal, out int targetOwner);
            string prefix = OwnerPrefix(targetOwner);

            int atk = buff.Attack;
            int hp = buff.Health;

            // Skip zero-value buffs
            if (atk == 0 && hp == 0) return null;

            string stats = "";
            if (atk != 0 && hp != 0)
                stats = $"+{atk} attack, +{hp} health";
            else if (atk != 0)
                stats = $"+{atk} attack";
            else
                stats = $"+{hp} health";

            return new BattleEvent
            {
                Type = BattleEventType.Buff,
                Description = $"{prefix}{targetName} gains {stats}",
                TargetPet = targetName,
                DedupKey = $"Buff:{IdKey(buff.TargetId)}:{atk}:{hp}"
            };
        }
        catch (System.Exception ex)
        {
            _log?.LogDebug($"BuffMinion parse error: {ex.Message}");
            return null;
        }
    }

    // ── IL2CPP Reflection Helper ────────────────────────────────────

    /// <summary>
    /// Gets the inner .Event from a Done&lt;T&gt; wrapper via IL2CPP reflection,
    /// then TryCasts it to the target type. Used when TryCast&lt;Done&lt;T&gt;&gt; fails.
    /// </summary>
    private T? GetInnerEvent<T>(Il2CppSystem.Object doneWrapper) where T : Il2CppSystem.Object
    {
        try
        {
            var eventProp = doneWrapper.GetIl2CppType().GetProperty("Event");
            if (eventProp == null) return null;

            var innerObj = eventProp.GetValue(doneWrapper, null);
            if (innerObj == null) return null;

            return innerObj.TryCast<T>();
        }
        catch { return null; }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private string ResolvePetName(Spacewood.Core.Models.Item.ItemId id, int fallbackOwner, out int owner)
    {
        owner = fallbackOwner;
        if (_petLookup == null) return "pet";

        try
        {
            string key = $"{id.BoardId}:{id.Unique}";
            if (_petLookup.TryGetValue(key, out var entry))
            {
                owner = entry.owner;
                return entry.name;
            }

            // Lazy scan: rescan resolver board — it may have new entries
            TryScanResolverBoard();
            if (_petLookup.TryGetValue(key, out entry))
            {
                owner = entry.owner;
                return entry.name;
            }

            // Fallback: search by Unique value across all BoardIds, constrained by owner.
            // The resolver may use a different BoardId than the original boards,
            // and some pets might not appear in the merged board scan (e.g. already dead).
            // Only match entries with the same owner to avoid cross-team collisions
            // (e.g. player Unique=1 vs enemy Unique=1).
            if (fallbackOwner > 0)
            {
                int unique = id.Unique;
                string suffix = $":{unique}";
                foreach (var kvp in _petLookup)
                {
                    if (kvp.Key.EndsWith(suffix) && kvp.Value.owner == fallbackOwner)
                    {
                        owner = kvp.Value.owner;
                        _petLookup[key] = kvp.Value;
                        return kvp.Value.name;
                    }
                }
            }

            _log?.LogInfo($"Pet not found: key={key}, fallbackOwner={fallbackOwner}");
        }
        catch { }

        return "pet";
    }

    private static string IdKey(Spacewood.Core.Models.Item.ItemId id)
    {
        try { return $"{id.BoardId}:{id.Unique}"; } catch { return "?"; }
    }

    private static string OwnerPrefix(int owner)
    {
        return owner switch
        {
            1 => "Your ",
            2 => "Enemy ",
            _ => ""
        };
    }

    private Spacewood.Unity.MonoBehaviours.Battle.BattleController? FindBattleController()
    {
        if (_cachedBattleCtrl != null) return _cachedBattleCtrl;

        try
        {
            // BattleController is a MonoSingleton — try .Instance first
            _cachedBattleCtrl = Spacewood.Unity.MonoBehaviours.Battle.BattleController.Instance;
        }
        catch { }

        if (_cachedBattleCtrl == null)
        {
            try
            {
                _cachedBattleCtrl = Object.FindObjectOfType<
                    Spacewood.Unity.MonoBehaviours.Battle.BattleController>();
            }
            catch { }
        }

        return _cachedBattleCtrl;
    }

    private static string GetMinionName(Spacewood.Core.Models.MinionModel minion)
    {
        try
        {
            return Spacewood.Unity.Extensions.MinionModelExtensions.GetNameLocalized(minion)
                ?? minion.Enum.ToString();
        }
        catch
        {
            try { return minion.Enum.ToString(); } catch { return "pet"; }
        }
    }
}
