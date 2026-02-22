using BepInEx.Logging;
using SAPAccess.Config;
using SAPAccess.GameState;
using SAPAccess.NVDA;
using UnityEngine;

namespace SAPAccess.Announcements;

/// <summary>
/// Announces battle-phase events: attacks, faints, abilities, summons, outcome.
/// Processes the BattleStateReader event queue each frame during battle.
/// </summary>
public class BattleAnnouncer : MonoBehaviour
{
    public static BattleAnnouncer? Instance { get; private set; }

    private ManualLogSource? _log;
    private float _lastAnnouncementTime;

    public void Awake()
    {
        Instance = this;
        _log = BepInEx.Logging.Logger.CreateLogSource("SAPAccess.BattleAnnounce");
    }

    public void Update()
    {
        if (GamePhaseTracker.Instance.CurrentPhase != GamePhase.Battle)
            return;

        float delay = ModConfig.Instance?.SpeechDelay.Value ?? 0.1f;
        if (Time.time - _lastAnnouncementTime < delay)
            return;

        var evt = BattleStateReader.Instance.DequeueEvent();
        if (evt == null)
            return;

        bool detailed = ModConfig.Instance?.AnnounceBattleDetails.Value ?? true;

        if (detailed || evt.Type == BattleEventType.Faint)
        {
            ScreenReader.Instance.SayQueued(evt.Description);
            _lastAnnouncementTime = Time.time;
        }
    }

    public void OnBattleStart()
    {
        ScreenReader.Instance.Say("Battle starting.");
    }

    public void OnBattleEnd(BattleOutcome outcome)
    {
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
}
