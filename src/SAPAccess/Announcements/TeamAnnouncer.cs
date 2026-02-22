using BepInEx.Logging;
using SAPAccess.GameState;
using SAPAccess.NVDA;

namespace SAPAccess.Announcements;

/// <summary>
/// Announces team composition on demand (T key).
/// </summary>
public class TeamAnnouncer
{
    public static TeamAnnouncer? Instance { get; private set; }

    private readonly ManualLogSource _log;
    private readonly TeamStateReader _team;

    public TeamAnnouncer()
    {
        Instance = this;
        _log = Logger.CreateLogSource("SAPAccess.TeamAnnounce");
        _team = TeamStateReader.Instance;
    }

    public void AnnounceTeam()
    {
        ScreenReader.Instance.Say(_team.GetTeamSummary());
    }

    public void OnPetPlaced(string petName, int position)
    {
        ScreenReader.Instance.Say($"{petName} placed in slot {position + 1}.");
    }

    public void OnPetMoved(string petName, int fromPos, int toPos)
    {
        ScreenReader.Instance.Say($"{petName} moved from slot {fromPos + 1} to slot {toPos + 1}.");
    }

    public void OnPetBuffed(string petName, int atkChange, int hpChange)
    {
        string changes = "";
        if (atkChange != 0)
            changes += $"{(atkChange > 0 ? "+" : "")}{atkChange} attack";
        if (hpChange != 0)
        {
            if (changes.Length > 0) changes += ", ";
            changes += $"{(hpChange > 0 ? "+" : "")}{hpChange} health";
        }

        if (changes.Length > 0)
            ScreenReader.Instance.SayQueued($"{petName} {changes}.");
    }
}
