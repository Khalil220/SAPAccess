using System;
using System.Collections.Generic;
using BepInEx.Logging;
using SAPAccess.NVDA;

namespace SAPAccess.Navigation;

/// <summary>
/// Manages a virtual focus cursor that tracks the currently focused UI element.
/// Elements are organized into groups (rows), navigated with arrow keys and Tab.
/// </summary>
public class FocusManager
{
    public static FocusManager? Instance { get; private set; }

    private readonly ManualLogSource _log;
    private readonly List<FocusGroup> _groups = new();
    private int _currentGroupIndex;
    private int _currentElementIndex;

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

    public event Action<FocusElement?>? FocusChanged;

    public FocusManager()
    {
        Instance = this;
        _log = Logger.CreateLogSource("SAPAccess.Focus");
    }

    /// <summary>Replaces all focus groups with a new set (e.g., on phase change).</summary>
    public void SetGroups(List<FocusGroup> groups)
    {
        _groups.Clear();
        _groups.AddRange(groups);
        _currentGroupIndex = 0;
        _currentElementIndex = 0;

        if (CurrentElement != null)
        {
            AnnounceFocus();
        }
    }

    /// <summary>Clears all focus groups.</summary>
    public void Clear()
    {
        _groups.Clear();
        _currentGroupIndex = 0;
        _currentElementIndex = 0;
    }

    /// <summary>Move focus left (previous element in current group).</summary>
    public void MoveLeft()
    {
        if (_groups.Count == 0) return;
        var group = _groups[_currentGroupIndex];
        if (group.Elements.Count == 0) return;

        _currentElementIndex--;
        if (_currentElementIndex < 0)
            _currentElementIndex = group.Elements.Count - 1;

        AnnounceFocus();
    }

    /// <summary>Move focus right (next element in current group).</summary>
    public void MoveRight()
    {
        if (_groups.Count == 0) return;
        var group = _groups[_currentGroupIndex];
        if (group.Elements.Count == 0) return;

        _currentElementIndex++;
        if (_currentElementIndex >= group.Elements.Count)
            _currentElementIndex = 0;

        AnnounceFocus();
    }

    /// <summary>Move focus up (previous group).</summary>
    public void MoveUp()
    {
        if (_groups.Count <= 1) return;

        _currentGroupIndex--;
        if (_currentGroupIndex < 0)
            _currentGroupIndex = _groups.Count - 1;

        ClampElementIndex();
        AnnounceGroupAndFocus();
    }

    /// <summary>Move focus down (next group).</summary>
    public void MoveDown()
    {
        if (_groups.Count <= 1) return;

        _currentGroupIndex++;
        if (_currentGroupIndex >= _groups.Count)
            _currentGroupIndex = 0;

        ClampElementIndex();
        AnnounceGroupAndFocus();
    }

    /// <summary>Cycle to the next focus group (Tab key).</summary>
    public void CycleGroup()
    {
        MoveDown();
    }

    /// <summary>Announces the currently focused element.</summary>
    public void AnnounceFocus()
    {
        var element = CurrentElement;
        if (element == null) return;

        string text = element.GetDescription();
        ScreenReader.Instance.Say(text);
        FocusChanged?.Invoke(element);
        _log.LogDebug($"Focus: {text}");
    }

    private void AnnounceGroupAndFocus()
    {
        var group = CurrentGroup;
        var element = CurrentElement;
        if (group == null) return;

        string text = element != null
            ? $"{group.Name}. {element.GetDescription()}"
            : group.Name;

        ScreenReader.Instance.Say(text);
        FocusChanged?.Invoke(element);
        _log.LogDebug($"Focus: {text}");
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

        if (Type == "editbox")
            desc += ". Edit box. Press Enter to edit.";
        else if (Type == "button")
            desc += ". Button.";
        return desc;
    }
}
