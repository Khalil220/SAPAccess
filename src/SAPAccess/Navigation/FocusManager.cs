using System;
using System.Collections.Generic;
using BepInEx.Logging;
using SAPAccess.NVDA;

namespace SAPAccess.Navigation;

/// <summary>
/// Manages a virtual focus cursor that tracks the currently focused UI element.
/// Elements are organized into groups (rows), navigated with arrow keys and Tab.
/// Supports an info-row buffer for scrolling through element details with Up/Down.
/// </summary>
public class FocusManager
{
    public static FocusManager? Instance { get; private set; }

    private readonly ManualLogSource _log;
    private readonly List<FocusGroup> _groups = new();
    private int _currentGroupIndex;
    private int _currentElementIndex;
    private int _currentInfoRow;

    // Dwell timer for tooltip announcement
    private FocusElement? _dwellElement;
    private float _dwellTimer;
    private bool _dwellAnnounced;
    private const float DwellDelay = 2.0f;

    public FocusElement? CurrentElement
    {
        get
        {
            if (_groups.Count == 0) return null;
            var group = _groups[_currentGroupIndex];
            if (group.Elements.Count == 0) return null;
            return group.Elements[_currentElementIndex];
        }
    }

    public FocusGroup? CurrentGroup =>
        _groups.Count > 0 ? _groups[_currentGroupIndex] : null;

    public int CurrentElementIndex => _currentElementIndex;

    public event Action<FocusElement?>? FocusChanged;

    public FocusManager()
    {
        Instance = this;
        _log = Logger.CreateLogSource("SAPAccess.Focus");
    }

    /// <summary>Replaces all focus groups with a new set (e.g., on phase change).
    /// Uses queued speech so it doesn't interrupt any in-progress announcements.</summary>
    public void SetGroups(List<FocusGroup> groups)
    {
        _groups.Clear();
        _groups.AddRange(groups);
        _currentGroupIndex = 0;
        _currentElementIndex = 0;
        _currentInfoRow = 0;

        var element = CurrentElement;
        if (element != null)
        {
            string text;
            if (element.InfoRows != null && element.InfoRows.Count > 0)
                text = element.InfoRows[0];
            else
                text = element.GetDescription();

            ScreenReader.Instance.Say(text);
            FocusChanged?.Invoke(element);
            _log.LogDebug($"Focus: {element.Label}");
        }
    }

    /// <summary>Replaces all focus groups and restores focus to the named group and element index.
    /// Announces the restored position with queued speech (won't interrupt prior announcements).
    /// Used after dialog close to return to the pre-dialog position.</summary>
    public void SetGroupsRestoring(List<FocusGroup> groups, string groupName, int elementIndex = 0)
    {
        _groups.Clear();
        _groups.AddRange(groups);
        _currentGroupIndex = 0;
        _currentElementIndex = 0;
        _currentInfoRow = 0;

        bool groupFound = false;
        for (int i = 0; i < _groups.Count; i++)
        {
            if (_groups[i].Name == groupName)
            {
                _currentGroupIndex = i;
                groupFound = true;
                break;
            }
        }

        // Only restore element index when the target group was actually found;
        // otherwise start at element 0 to avoid applying a stale index to an unrelated group.
        if (groupFound && _groups.Count > 0)
        {
            var group = _groups[_currentGroupIndex];
            _currentElementIndex = Math.Min(elementIndex, Math.Max(0, group.Elements.Count - 1));
        }

        // Announce restored position without group name prefix — the page title
        // was already announced by the menu patch, so repeating it is redundant.
        var element = CurrentElement;
        if (element != null)
        {
            string elementText;
            if (element.InfoRows != null && element.InfoRows.Count > 0)
                elementText = element.InfoRows[0];
            else
                elementText = element.GetDescription();

            ScreenReader.Instance.Say(elementText);
        }

        FocusChanged?.Invoke(CurrentElement);
    }

    /// <summary>Replaces focus groups while preserving the current position (for refreshes).
    /// Matches group by name and clamps element index.</summary>
    public void ReplaceGroupsSilent(List<FocusGroup> groups)
    {
        string? prevGroupName = CurrentGroup?.Name;
        int prevElementIdx = _currentElementIndex;
        int prevInfoRow = _currentInfoRow;

        _groups.Clear();
        _groups.AddRange(groups);

        // Try to restore to same group by name
        _currentGroupIndex = 0;
        if (prevGroupName != null)
        {
            for (int i = 0; i < _groups.Count; i++)
            {
                if (_groups[i].Name == prevGroupName)
                {
                    _currentGroupIndex = i;
                    break;
                }
            }
        }

        // Clamp element index to new group size
        if (_groups.Count > 0)
        {
            var group = _groups[_currentGroupIndex];
            _currentElementIndex = Math.Min(prevElementIdx, Math.Max(0, group.Elements.Count - 1));
        }
        else
        {
            _currentElementIndex = 0;
        }

        _currentInfoRow = prevInfoRow;
    }

    /// <summary>Clears all focus groups.</summary>
    public void Clear()
    {
        _groups.Clear();
        _currentGroupIndex = 0;
        _currentElementIndex = 0;
        _currentInfoRow = 0;
    }

    /// <summary>Called each frame to advance the dwell timer for tooltip announcements.</summary>
    public void Tick(float deltaTime)
    {
        var element = CurrentElement;
        if (element != _dwellElement)
        {
            // Focus changed — reset dwell timer
            _dwellElement = element;
            _dwellTimer = 0f;
            _dwellAnnounced = false;
            return;
        }

        if (_dwellAnnounced || element == null) return;
        if (string.IsNullOrEmpty(element.Tooltip)) return;

        _dwellTimer += deltaTime;
        if (_dwellTimer >= DwellDelay)
        {
            _dwellAnnounced = true;
            ScreenReader.Instance.Say(element.Tooltip!);
            _log.LogDebug($"Dwell tooltip: {element.Tooltip}");
        }
    }

    /// <summary>Move focus left (previous element in current group). Resets info row to 0.</summary>
    public void MoveLeft()
    {
        if (_groups.Count == 0) return;
        var group = _groups[_currentGroupIndex];
        if (group.Elements.Count == 0) return;

        if (_currentElementIndex <= 0) return; // Already at first element

        _currentElementIndex--;
        _currentInfoRow = 0;
        AnnounceFocus();
    }

    /// <summary>Jump to the first element in the current group.</summary>
    public void MoveToFirst()
    {
        if (_groups.Count == 0) return;
        var group = _groups[_currentGroupIndex];
        if (group.Elements.Count == 0) return;
        if (_currentElementIndex == 0) { AnnounceFocus(); return; }

        _currentElementIndex = 0;
        _currentInfoRow = 0;
        AnnounceFocus();
    }

    /// <summary>Jump to the last element in the current group.</summary>
    public void MoveToLast()
    {
        if (_groups.Count == 0) return;
        var group = _groups[_currentGroupIndex];
        if (group.Elements.Count == 0) return;
        int last = group.Elements.Count - 1;
        if (_currentElementIndex == last) { AnnounceFocus(); return; }

        _currentElementIndex = last;
        _currentInfoRow = 0;
        AnnounceFocus();
    }

    /// <summary>Move focus right (next element in current group). Resets info row to 0.</summary>
    public void MoveRight()
    {
        if (_groups.Count == 0) return;
        var group = _groups[_currentGroupIndex];
        if (group.Elements.Count == 0) return;

        if (_currentElementIndex >= group.Elements.Count - 1) return; // Already at last element

        _currentElementIndex++;
        _currentInfoRow = 0;
        AnnounceFocus();
    }

    /// <summary>Move focus up (previous group). For menu navigation.</summary>
    public void MoveUp()
    {
        if (_groups.Count <= 1) return;
        if (_currentGroupIndex <= 0) return; // Already at first group

        SwitchToGroup(_currentGroupIndex - 1);
        _currentInfoRow = 0;
        AnnounceGroupAndFocus();
    }

    /// <summary>Move focus down (next group). For menu navigation.</summary>
    public void MoveDown()
    {
        if (_groups.Count <= 1) return;
        if (_currentGroupIndex >= _groups.Count - 1) return; // Already at last group

        SwitchToGroup(_currentGroupIndex + 1);
        _currentInfoRow = 0;
        AnnounceGroupAndFocus();
    }

    /// <summary>Scroll up through info rows of the current element.</summary>
    public void InfoUp()
    {
        var element = CurrentElement;
        if (element?.InfoRows == null || element.InfoRows.Count == 0) return;

        if (_currentInfoRow <= 0)
        {
            // Already at top, announce we're at the top
            AnnounceInfoRow();
            return;
        }

        _currentInfoRow--;
        AnnounceInfoRow();
    }

    /// <summary>Scroll down through info rows of the current element.</summary>
    public void InfoDown()
    {
        var element = CurrentElement;
        if (element?.InfoRows == null || element.InfoRows.Count == 0) return;

        if (_currentInfoRow >= element.InfoRows.Count - 1)
        {
            // Already at bottom, announce we're at the bottom
            AnnounceInfoRow();
            return;
        }

        _currentInfoRow++;
        AnnounceInfoRow();
    }

    /// <summary>Switch focus to a group by name (e.g., "Shop", "Team").</summary>
    public void FocusGroupByName(string groupName)
    {
        for (int i = 0; i < _groups.Count; i++)
        {
            if (_groups[i].Name == groupName)
            {
                SwitchToGroup(i);
                _currentInfoRow = 0;
                AnnounceGroupAndFocus();
                return;
            }
        }

        ScreenReader.Instance.Say($"{groupName} is empty.");
    }

    /// <summary>Cycle to the next focus group (Tab key). Wraps around.</summary>
    public void CycleGroup()
    {
        if (_groups.Count <= 1) return;

        int next = _currentGroupIndex + 1;
        if (next >= _groups.Count)
            next = 0;

        SwitchToGroup(next);
        _currentInfoRow = 0;
        AnnounceGroupAndFocus();
    }

    /// <summary>Announces the currently focused element (info row 0 if InfoRows exist, else full description).
    /// Uses Interrupt so navigation gives instant feedback.</summary>
    public void AnnounceFocus()
    {
        var element = CurrentElement;
        if (element == null) return;

        _currentInfoRow = 0;

        if (element.InfoRows != null && element.InfoRows.Count > 0)
        {
            ScreenReader.Instance.Interrupt(element.InfoRows[0]);
        }
        else
        {
            string text = element.GetDescription();
            ScreenReader.Instance.Interrupt(text);
        }

        FocusChanged?.Invoke(element);
        _log.LogDebug($"Focus: {element.Label}");
    }

    private void AnnounceInfoRow()
    {
        var element = CurrentElement;
        if (element?.InfoRows == null || _currentInfoRow >= element.InfoRows.Count) return;

        ScreenReader.Instance.Interrupt(element.InfoRows[_currentInfoRow]);
        _log.LogDebug($"InfoRow [{_currentInfoRow}]: {element.InfoRows[_currentInfoRow]}");
    }

    private void AnnounceGroupAndFocus()
    {
        var group = CurrentGroup;
        var element = CurrentElement;
        if (group == null) return;

        _currentInfoRow = 0;

        if (element != null)
        {
            string elementText;
            if (element.InfoRows != null && element.InfoRows.Count > 0)
                elementText = element.InfoRows[0];
            else
                elementText = element.GetDescription();

            ScreenReader.Instance.Interrupt($"{group.Name}. {elementText}");
        }
        else
        {
            ScreenReader.Instance.Interrupt(group.Name);
        }

        FocusChanged?.Invoke(element);
        _log.LogDebug($"Focus: {group.Name}");
    }

    /// <summary>Saves element index to departing group, switches to new group index,
    /// and restores that group's saved element index (clamped).</summary>
    private void SwitchToGroup(int newGroupIndex)
    {
        // Save position in current group
        if (_groups.Count > 0 && _currentGroupIndex < _groups.Count)
            _groups[_currentGroupIndex].SavedElementIndex = _currentElementIndex;

        _currentGroupIndex = newGroupIndex;

        // Restore position from new group
        if (_groups.Count > 0 && _currentGroupIndex < _groups.Count)
        {
            var group = _groups[_currentGroupIndex];
            _currentElementIndex = Math.Min(group.SavedElementIndex, Math.Max(0, group.Elements.Count - 1));
        }
        else
        {
            _currentElementIndex = 0;
        }
    }

    private void ClampElementIndex()
    {
        if (_groups.Count == 0) return;
        var group = _groups[_currentGroupIndex];
        if (_currentElementIndex >= group.Elements.Count)
            _currentElementIndex = Math.Max(0, group.Elements.Count - 1);
    }
}

/// <summary>A named group of focusable elements (e.g., "Team", "Shop", "Actions").</summary>
public class FocusGroup
{
    public string Name { get; }
    public List<FocusElement> Elements { get; } = new();

    /// <summary>Remembered element index so each group keeps its own cursor position.</summary>
    public int SavedElementIndex { get; set; }

    public FocusGroup(string name)
    {
        Name = name;
    }
}

/// <summary>A single focusable element with a label and optional action.</summary>
public class FocusElement
{
    public string Label { get; set; }
    public string? Detail { get; set; }
    public string? Type { get; set; }
    public int SlotIndex { get; set; }
    public Action? OnActivate { get; set; }
    public object? Tag { get; set; }
    public Func<string?>? DynamicDetail { get; set; }

    /// <summary>
    /// Info rows for the buffer system. Row 0 = name, Row 1 = stats, Row 2+ = extra info.
    /// When set, Up/Down scrolls through these rows instead of switching groups.
    /// </summary>
    public List<string>? InfoRows { get; set; }

    /// <summary>
    /// Tooltip text announced after dwelling on this element for 2 seconds.
    /// Pulled from the game's own tooltip data. Null/empty = no tooltip.
    /// </summary>
    public string? Tooltip { get; set; }

    public FocusElement(string label, int slotIndex = -1)
    {
        Label = label;
        SlotIndex = slotIndex;
    }

    public string GetDescription()
    {
        string desc = Label;
        if (!string.IsNullOrEmpty(Detail))
            desc += $", {Detail}";

        // Append dynamic detail (e.g. current input field value)
        string? dynDetail = null;
        try { dynDetail = DynamicDetail?.Invoke(); } catch { }
        if (!string.IsNullOrEmpty(dynDetail))
            desc += $", {dynDetail}";

        // Strip trailing periods to avoid double-period before type suffix
        desc = desc.TrimEnd('.', ' ');

        if (Type == "editbox")
            desc += ". Edit box. Press Enter to edit.";
        else if (Type == "slider")
            desc += ". Slider. Press Enter to adjust.";
        else if (Type == "button")
            desc += ". Button.";
        return desc;
    }
}
