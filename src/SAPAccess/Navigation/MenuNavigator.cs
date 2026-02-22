using System.Collections.Generic;
using BepInEx.Logging;
using SAPAccess.GameState;
using SAPAccess.NVDA;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SAPAccess.Navigation;

/// <summary>
/// Scans the active menu page for ButtonBase and TMP_InputField components,
/// populates FocusManager so the user can navigate menus with arrow keys.
/// Manages an editing mode for text input fields.
/// </summary>
public class MenuNavigator : MonoBehaviour
{
    public static MenuNavigator? Instance { get; private set; }

    private static ManualLogSource? _log;
    private Spacewood.Unity.Menu? _menu;
    private Spacewood.Unity.Page? _currentPage;
    private bool _needsScan;
    private float _scanDelay;

    /// <summary>True when the user is typing into an input field.</summary>
    public bool IsEditing { get; private set; }
    private TMP_InputField? _activeInputField;
    private TMP_InputField? _pendingActivation;
    private int _editStartFrame = -1;
    private string _trackedText = "";

    public void Awake()
    {
        Instance = this;
        _log = BepInEx.Logging.Logger.CreateLogSource("SAPAccess.MenuNav");
    }

    /// <summary>Called from MenuPatches when a page opens.</summary>
    public void OnPageChanged(Spacewood.Unity.Page? page)
    {
        StopEditing(announce: false);

        _currentPage = page;
        _needsScan = true;
        _scanDelay = 0.3f;
    }

    public void Update()
    {
        // Active during menu phases and initial login (Unknown phase)
        var phase = GamePhaseTracker.Instance.CurrentPhase;
        if (phase == GamePhase.Shop || phase == GamePhase.Battle)
            return;

        // Prevent Unity's EventSystem from routing keyboard input to UI elements.
        // Without this, Enter/Space get routed to whatever button Unity has selected,
        // causing rogue button activations alongside our own input handling.
        if (!IsEditing)
        {
            try
            {
                var es = EventSystem.current;
                if (es != null && es.currentSelectedGameObject != null)
                    es.SetSelectedGameObject(null);
            }
            catch { }
        }

        if (_needsScan)
        {
            _scanDelay -= Time.deltaTime;
            if (_scanDelay <= 0f)
            {
                _needsScan = false;
                ScanCurrentPage();
            }
        }

        // Delayed activation: activate the input field one frame after Enter was pressed
        // so TMP_InputField doesn't process the same Enter as a submit
        if (_pendingActivation != null && Time.frameCount > _editStartFrame)
        {
            try
            {
                _pendingActivation.ActivateInputField();
                _pendingActivation.Select();
            }
            catch (System.Exception ex)
            {
                _log?.LogError($"Delayed activation error: {ex}");
            }
            _pendingActivation = null;
        }

        if (IsEditing)
        {
            // Track text every frame so we have the latest value
            // (TMP_InputField reverts text on Escape/cancel before we can read it)
            try
            {
                if (_activeInputField != null)
                {
                    string current = _activeInputField.text ?? "";
                    _trackedText = current;
                }
            }
            catch { }

            // Enter exits editing mode (skip the frame editing started)
            if (Time.frameCount > _editStartFrame + 1 && Input.GetKeyDown(KeyCode.Return))
            {
                StopEditing(announce: true);
            }
        }
    }

    /// <summary>Triggers a rescan of the current page for buttons.</summary>
    public void RequestRescan()
    {
        _needsScan = true;
        _scanDelay = 0.2f;
    }

    /// <summary>Begins editing the given input field.</summary>
    public void StartEditing(TMP_InputField inputField)
    {
        if (inputField == null) return;

        IsEditing = true;
        _activeInputField = inputField;
        _editStartFrame = Time.frameCount;

        // Delay the actual ActivateInputField() by one frame so the Enter key
        // that triggered this isn't processed by TMP_InputField as a submit
        _pendingActivation = inputField;

        try
        {
            string currentText = inputField.text ?? "";
            _trackedText = currentText;
            if (string.IsNullOrEmpty(currentText))
                ScreenReader.Instance.Say("Editing. Field is empty.");
            else
                ScreenReader.Instance.Say($"Editing. Current text: {currentText}");
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"StartEditing error: {ex}");
        }
    }

    /// <summary>Stops editing and reads back the field value.</summary>
    public void StopEditing(bool announce = true)
    {
        if (!IsEditing) return;

        IsEditing = false;
        _pendingActivation = null;

        if (_activeInputField != null)
        {
            try
            {
                // Use our tracked text — the field may have already reverted
                string text = _trackedText;

                _activeInputField.DeactivateInputField();

                // Restore text in case TMP_InputField reverted it on cancel
                _activeInputField.text = text;

                if (announce)
                    ScreenReader.Instance.Say($"Done editing. Value: {text}");
            }
            catch (System.Exception ex)
            {
                _log?.LogError($"StopEditing error: {ex}");
            }
        }

        _activeInputField = null;
        _trackedText = "";
    }

    private void ScanCurrentPage()
    {
        if (_menu == null)
        {
            _menu = Object.FindObjectOfType<Spacewood.Unity.Menu>();
            if (_menu == null)
            {
                _log?.LogWarning("Menu not found in scene");
                return;
            }
        }

        // Determine which page to scan
        Spacewood.Unity.Page? page = _currentPage;
        if (page == null)
        {
            page = _menu.PageManager?.CurrentPage;
        }

        if (page == null)
        {
            _log?.LogDebug("No active page to scan");
            return;
        }

        var group = new FocusGroup(page.gameObject?.name ?? "Menu");

        // Collect all interactive elements with their vertical positions for sorting
        var elements = new List<(float y, FocusElement element)>();

        // Scan for TMP_InputField components
        var inputFields = page.GetComponentsInChildren<TMP_InputField>(false);
        if (inputFields != null)
        {
            foreach (var field in inputFields)
            {
                if (field == null) continue;
                if (!field.interactable) continue;

                string label;
                try
                {
                    var placeholder = field.placeholder as TMP_Text;
                    label = placeholder?.text ?? field.gameObject?.name ?? "Text field";
                }
                catch
                {
                    label = field.gameObject?.name ?? "Text field";
                }

                if (string.IsNullOrWhiteSpace(label))
                    label = field.gameObject?.name ?? "Text field";

                float yPos = 0f;
                try
                {
                    yPos = field.transform.position.y;
                }
                catch { }

                var capturedField = field;
                var capturedLabel = label;
                var element = new FocusElement(label)
                {
                    Type = "editbox",
                    Tag = capturedField,
                    DynamicDetail = () =>
                    {
                        try
                        {
                            string val = capturedField.text;
                            return string.IsNullOrEmpty(val) ? null : val;
                        }
                        catch { return null; }
                    },
                    OnActivate = () =>
                    {
                        _log?.LogInfo($"Activating input field: {capturedLabel}");
                        StartEditing(capturedField);
                    }
                };

                elements.Add((yPos, element));
            }
        }

        // Scan for ButtonBase components
        var buttons = page.GetComponentsInChildren<Spacewood.Unity.UI.ButtonBase>(false);
        if (buttons != null)
        {
            foreach (var button in buttons)
            {
                if (button == null) continue;

                try
                {
                    if (!button.GetInteractable()) continue;
                }
                catch { continue; }

                string label;
                try
                {
                    label = button.Label?.text ?? button.gameObject?.name ?? "Button";
                }
                catch
                {
                    label = button.gameObject?.name ?? "Button";
                }

                if (string.IsNullOrWhiteSpace(label))
                    label = button.gameObject?.name ?? "Button";

                float yPos = 0f;
                try
                {
                    yPos = button.transform.position.y;
                }
                catch { }

                var capturedButton = button;
                var capturedLabel = label;
                var element = new FocusElement(label)
                {
                    Type = "button",
                    Tag = capturedButton,
                    OnActivate = () =>
                    {
                        try
                        {
                            _log?.LogInfo($"Activating button: {capturedLabel}");
                            capturedButton.Click();
                        }
                        catch (System.Exception ex)
                        {
                            _log?.LogError($"Button click error: {ex}");
                        }
                    }
                };

                elements.Add((yPos, element));
            }
        }

        if (elements.Count == 0)
        {
            _log?.LogDebug("No interactable elements found");
            return;
        }

        // Sort top-to-bottom (higher Y = higher on screen in Unity UI)
        elements.Sort((a, b) => b.y.CompareTo(a.y));

        for (int i = 0; i < elements.Count; i++)
        {
            elements[i].element.SlotIndex = i;
            group.Elements.Add(elements[i].element);
        }

        var groups = new List<FocusGroup> { group };
        FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"Menu scan: {group.Elements.Count} elements on {page.gameObject?.name}");
    }

    /// <summary>Navigates back on the current page.</summary>
    public void GoBack()
    {
        if (IsEditing)
        {
            StopEditing();
            return;
        }

        try
        {
            if (_currentPage != null)
            {
                _currentPage.Back();
                return;
            }

            if (_menu?.PageManager?.CurrentPage != null)
            {
                _menu.PageManager.CurrentPage.Back();
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"GoBack error: {ex}");
        }
    }
}
