using System.Collections.Generic;
using BepInEx.Logging;
using SAPAccess.Announcements;
using SAPAccess.GameState;
using SAPAccess.NVDA;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SAPAccess.Navigation;

/// <summary>
/// Scans the active menu page for ButtonBase, TMP_InputField, and text components,
/// populates FocusManager so the user can navigate menus with arrow keys.
/// Manages an editing mode for text input fields.
/// Handles Picker dialogs (choice prompts, confirmations) across all game phases.
/// Builds shop-phase focus groups for navigating shop pets, food, and team.
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

    /// <summary>True when a dialog (Alert2 or Picker) is open.</summary>
    public bool IsDialogOpen { get; private set; }
    private bool _needsPickerScan;
    private float _pickerScanDelay;
    private GamePhase _lastPhase = GamePhase.Unknown;

    // Alert2 polling state
    private Spacewood.Unity.Alert2? _cachedAlert;
    private bool _alertWasOpen;

    // SubscriptionCart polling state
    private Spacewood.Unity.SubscriptionCart? _cachedSubCart;
    private bool _subCartWasOpen;

    // Picker polling state
    private Spacewood.Unity.UI.Picker? _cachedPicker;
    private bool _pickerWasActive;

    // Dock (team naming) polling state
    private Spacewood.Unity.MonoBehaviours.Build.Dock? _cachedDock;
    private bool _dockWasActive;

    // TallyArena (battle result screen) polling state
    private Spacewood.Unity.TallyArena? _cachedTally;
    private bool _tallyWasActive;
    private bool _tallyAnnouncedResult;
    private bool _needsTallyRead;
    private float _tallyReadDelay;

    // Post-game screens polling state (appear after final battle)
    private Spacewood.Unity.TallyArenaFinale? _cachedFinale;
    private bool _finaleWasActive;
    private Spacewood.Unity.TallyArenaReward? _cachedReward;
    private bool _rewardWasActive;
    private Spacewood.Unity.TallyArenaMenu? _cachedTallyMenu;
    private bool _tallyMenuWasActive;

    // IconAlert (tier rank-up / turn milestone) polling state
    private Spacewood.Unity.IconAlert? _cachedIconAlert;
    private bool _iconAlertWasOpen;

    // SideBar (match menu / pause menu) state
    private Spacewood.Unity.SideBar? _cachedSideBar;
    private bool _sideBarWasOpen;

    // Pending food targeting: user selected a food item, now needs to pick a team pet
    private Spacewood.Core.Models.SpellModel? _pendingFood;
    private string? _pendingFoodName;

    // Pending pet placement: user selected a shop pet, now needs to pick a team position
    private Spacewood.Core.Models.MinionModel? _pendingPet;
    private string? _pendingPetName;

    // Fallback game-start detection (HangarPatches may not fire for async methods)
    private float _hangarCheckTimer;

    // Cached HangarMain reference for shop actions
    private Spacewood.Unity.MonoBehaviours.Build.HangarMain? _cachedHangar;

    // Shop refresh state
    private bool _needsShopRefresh;
    private float _shopRefreshDelay;

    // Post-battle delayed setup (board data is stale when HangarMain first appears)
    private bool _needsPostBattleSetup;
    private float _postBattleDelay;

    // Pre-battle snapshots for detecting battle result (PreviousOutcome is unreliable)
    private int _preBattleLives;
    private int _preBattleVictories;

    // Flag to announce gold after refresh (for roll, where gold is stale at call time)
    private bool _announceGoldAfterRefresh;

    // DeckViewer (pack preview) polling state
    private Spacewood.Unity.MonoBehaviours.Build.DeckViewer? _cachedDeckViewer;
    private bool _deckViewerWasActive;

    // Chooser (cursed toys / relic selection) polling state
    private Spacewood.Scripts.MonoBehaviours.Build.Hangar.Chooser? _cachedChooser;
    private bool _chooserWasActive;

    // DesyncAlert polling state
    private Spacewood.Unity.DesyncAlert? _cachedDesyncAlert;
    private bool _desyncAlertWasOpen;

    // VersusLobby periodic rescan state
    private Spacewood.Unity.VersusLobby? _cachedVersusLobby;
    private int _versusLobbyPlayerCount;
    private float _versusLobbyPollTimer;

    // When set, the next rescan will restore focus to this group and element instead of resetting
    private string? _restoreGroupName;
    private int _restoreElementIndex;

    // Saved per-page focus positions: when leaving a page, save position; when returning, restore it.
    private readonly Dictionary<string, (string groupName, int elementIndex)> _pagePositions = new();

    // Parent focus position saved when entering MinionPicker, restored when returning to Advanced Rules
    private string? _minionPickerReturnGroup;
    private int _minionPickerReturnIndex;

    // Saved BrowseShops position for returning from a sub-shop within Customize.
    // Stored separately from _pagePositions so it survives phase transitions (e.g. arena preview battles).
    private string? _customizeReturnGroup;
    private int _customizeReturnIndex;

    public void Awake()
    {
        Instance = this;
        _log = BepInEx.Logging.Logger.CreateLogSource("SAPAccess.MenuNav");
    }

    /// <summary>Called from MenuPatches when a page opens.</summary>
    public void OnPageChanged(Spacewood.Unity.Page? page)
    {
        StopEditing(announce: false);

        // If we're inside Customize (sub-page tracking is active), page changes from
        // Customize's internal PageManager should be handled as sub-page switches,
        // NOT as top-level page changes. Keep _currentPage as Customize and just
        // update the sub-page pointer to trigger a ScanCustomize() on the next frame.
        if (_customizeSubPagePtr != System.IntPtr.Zero && page != null)
        {
            try
            {
                // Don't intercept if the incoming page IS the Customize page itself —
                // that's the main PageManager re-opening Customize, not a sub-page change.
                bool isCustomizePage = false;
                try { isCustomizePage = page.TryCast<Spacewood.Unity.Customize>() != null; } catch { }

                if (!isCustomizePage)
                {
                    var customize = _currentPage?.TryCast<Spacewood.Unity.Customize>();
                    if (customize != null)
                    {
                        System.IntPtr newPtr = System.IntPtr.Zero;
                        try { newPtr = page.Pointer; } catch { }

                        if (newPtr != System.IntPtr.Zero && newPtr != _customizeSubPagePtr)
                        {
                            // Read return position BEFORE overwriting (for going back)
                            string? savedReturnGroup = _customizeReturnGroup;
                            int savedReturnIndex = _customizeReturnIndex;

                            // Save current position as the new return point
                            string? leavingGroup = FocusManager.Instance?.CurrentGroup?.Name;
                            int leavingIdx = FocusManager.Instance?.CurrentElementIndex ?? 0;
                            _customizeReturnGroup = leavingGroup;
                            _customizeReturnIndex = leavingIdx;

                            _customizeSubPagePtr = newPtr;
                            _needsScan = true;
                            _scanDelay = 0.2f;

                            // Restore from the previously saved return fields
                            if (savedReturnGroup != null)
                            {
                                _restoreGroupName = savedReturnGroup;
                                _restoreElementIndex = savedReturnIndex;
                            }
                            else
                            {
                                _restoreGroupName = null;
                            }
                        }
                        return;
                    }
                }
            }
            catch { }
        }

        // Some pages trigger multiple OnPageChanged calls (e.g., Lobby fires both
        // PageManager.Open and Menu.StartLobby patches). If a scan is already pending
        // from the first call, don't re-run position save/restore logic — just update
        // _currentPage if needed so the correct page reference is used.
        if (_needsScan)
        {
            if (page != null)
                _currentPage = page;
            return;
        }

        // Save focus position for the page we're leaving so we can restore it if we come back
        try
        {
            string? leavingPageName = _currentPage?.gameObject?.name;
            if (leavingPageName != null)
            {
                string? groupName = FocusManager.Instance?.CurrentGroup?.Name;
                int elementIndex = FocusManager.Instance?.CurrentElementIndex ?? 0;
                _pagePositions[leavingPageName] = (groupName ?? "", elementIndex);
            }
        }
        catch { }

        _currentPage = page;
        _needsScan = true;
        _scanDelay = 0.3f;

        // Check if we have a saved position for the page we're entering (i.e., going back)
        try
        {
            string? newPageName = page?.gameObject?.name;
            if (newPageName != null && _pagePositions.TryGetValue(newPageName, out var saved))
            {
                _restoreGroupName = saved.groupName;
                _restoreElementIndex = saved.elementIndex;
                _pagePositions.Remove(newPageName);
            }
            else
            {
                _restoreGroupName = null;
            }
        }
        catch
        {
            _restoreGroupName = null;
        }
    }

    public void Update()
    {
        // Tick dwell timer for tooltip announcements
        FocusManager.Instance?.Tick(Time.deltaTime);

        var phase = GamePhaseTracker.Instance.CurrentPhase;

        // Clear stale menu focus groups on phase transition to Shop/Battle
        if (phase != _lastPhase)
        {
            // Phase transitions invalidate any saved focus restore position
            // and clear dialog/sidebar state
            _restoreGroupName = null;
            _pagePositions.Clear();
            _customizeSubPagePtr = System.IntPtr.Zero;
            _sideBarWasOpen = false;
            IsDialogOpen = false;

            if (phase == GamePhase.Shop || phase == GamePhase.Battle)
            {
                FocusManager.Instance?.Clear();
                _needsScan = false;
                _log?.LogInfo($"Cleared menu focus groups on phase transition to {phase}");
            }

            // Snapshot lives/victories before battle for result detection
            if (phase == GamePhase.Battle)
            {
                _preBattleLives = ShopStateReader.Instance.Lives;
                _preBattleVictories = ShopStateReader.Instance.Victories;
                _log?.LogInfo($"Pre-battle snapshot: Lives={_preBattleLives}, Victories={_preBattleVictories}");
            }

            // Clear cached UI references on scene transitions to avoid
            // accessing destroyed IL2CPP objects (causes native crashes)
            if (phase == GamePhase.Battle)
            {
                _cachedAlert = null;
                _alertWasOpen = false;
                _cachedPicker = null;
                _pickerWasActive = false;
                _cachedDock = null;
                _dockWasActive = false;
                _cachedTally = null;
                _tallyWasActive = false;
                _tallyAnnouncedResult = false;
                _needsTallyRead = false;
                _cachedFinale = null;
                _finaleWasActive = false;
                _cachedReward = null;
                _rewardWasActive = false;
                _cachedTallyMenu = null;
                _tallyMenuWasActive = false;
                _cachedDeckViewer = null;
                _deckViewerWasActive = false;
                _cachedIconAlert = null;
                _iconAlertWasOpen = false;
                _cachedChooser = null;
                _chooserWasActive = false;
                _cachedSubCart = null;
                _subCartWasOpen = false;
                _cachedDonationBuyer = null;
                _donationBuyerWasOpen = false;
                _donationBuyerPhase = null;
                IsDialogOpen = false;
            }

            _lastPhase = phase;
        }

        // Poll for DesyncAlert in any phase (desync can happen during shop or battle)
        PollForDesyncAlert();

        // Poll for active dialogs (Alert2, Picker, Dock, DeckViewer, SideBar) — skip during battle
        // (scene is different, these objects don't exist and stale refs crash)
        if (phase != GamePhase.Battle)
        {
            PollForAlert();
            PollForPicker();
            PollForDock();
            PollForDeckViewer();
            PollForIconAlert();
            PollForSideBar();
            PollForChooser();
            PollForSubscriptionCart();
            PollForDonationBuyer();
        }
        // If the Dock was open when the timer ran out and phase transitioned to Battle,
        // keep polling so OnDockClosed fires and clears the stuck state
        else if (_dockWasActive)
        {
            PollForDock();
        }

        // Poll for TallyArena and post-game screens during battle phase
        if (phase == GamePhase.Battle)
        {
            PollForTally();
            PollForFinale();
            PollForReward();
            PollForTallyMenu();
        }

        // Delayed TallyArena read (wait for ShowStatus to activate containers)
        if (_needsTallyRead)
        {
            _tallyReadDelay -= Time.deltaTime;
            if (_tallyReadDelay <= 0f)
            {
                _needsTallyRead = false;
                OnTallyOpened();
            }
        }

        // Handle picker scans regardless of game phase
        // Only re-scan if dialog is still open (multi-select pickers stay open;
        // single-select pickers close after Pick() and PollForPicker handles that)
        if (_needsPickerScan)
        {
            _pickerScanDelay -= Time.deltaTime;
            if (_pickerScanDelay <= 0f)
            {
                _needsPickerScan = false;
                if (IsDialogOpen)
                    ScanPicker();
            }
        }

        // Detect HangarMain in scene — handles initial game start (from menus)
        // AND return to shop after battle (phase is still Battle but shop scene reloaded)
        if (phase != GamePhase.Shop)
        {
            _hangarCheckTimer -= Time.deltaTime;
            if (_hangarCheckTimer <= 0f)
            {
                _hangarCheckTimer = 0.5f;
                try
                {
                    var hangar = Object.FindObjectOfType<Spacewood.Unity.MonoBehaviours.Build.HangarMain>();
                    if (hangar != null && hangar.BuildModel != null)
                    {
                        bool returningFromBattle = (phase == GamePhase.Battle);
                        _log?.LogInfo($"HangarMain detected in scene — entering Shop phase (fromBattle={returningFromBattle})");
                        GamePhaseTracker.Instance.CurrentPhase = GamePhase.Shop;
                        phase = GamePhase.Shop; // Update local var

                        _cachedHangar = hangar;
                        if (ShopAnnouncer.Instance != null)
                            ShopAnnouncer.Instance.Hangar = hangar;

                        _needsScan = false;
                        _lastPhase = phase;

                        if (returningFromBattle)
                        {
                            // Delay post-battle setup: BoardModel data (turn, gold,
                            // PreviousOutcome) is stale when HangarMain first appears.
                            // Wait for the server to populate updated data.
                            _needsPostBattleSetup = true;
                            _postBattleDelay = 1.5f;
                            _log?.LogInfo("Post-battle setup deferred (1.5s delay)");
                        }
                        // If a dialog (IconAlert, Dock, etc.) is active, defer shop setup
                        // until it closes — don't overwrite the dialog's focus groups
                        else if (!_dockWasActive && !IsDialogOpen)
                        {
                            var board = hangar.BuildModel?.Board;
                            if (board != null)
                            {
                                ShopStateReader.Instance.ReadFromBoard(board);
                                TeamStateReader.Instance.ReadFromBoard(board);
                            }
                            ShopAnnouncer.Instance?.OnTurnStart();
                            BuildShopFocusGroups(hangar);
                        }
                        else
                        {
                            _log?.LogInfo("Dock is active — deferring shop setup");
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    _log?.LogError($"HangarMain detection error: {ex}");
                }
            }
        }

        // Post-battle delayed setup (wait for server to populate new turn data)
        // Defer if a dialog (IconAlert) is open — it will trigger shop refresh on close
        if (_needsPostBattleSetup && phase == GamePhase.Shop)
        {
            _postBattleDelay -= Time.deltaTime;
            if (_postBattleDelay <= 0f)
            {
                if (!IsDialogOpen)
                {
                    _needsPostBattleSetup = false;
                    PerformPostBattleSetup();
                }
            }
        }

        // Shop-phase refresh (after roll, buy, sell, freeze)
        // Defer if a dialog (IconAlert) is open — it will trigger shop refresh on close
        if (_needsShopRefresh && phase == GamePhase.Shop)
        {
            _shopRefreshDelay -= Time.deltaTime;
            if (_shopRefreshDelay <= 0f)
            {
                if (!IsDialogOpen)
                {
                    _needsShopRefresh = false;
                    RefreshShopState();
                }
            }
        }

        // Delayed reorder after pet placement
        if (_needsReorder && phase == GamePhase.Shop)
        {
            _reorderDelay -= Time.deltaTime;
            if (_reorderDelay <= 0f)
                PerformPendingReorder();
        }

        bool inMenu = phase != GamePhase.Shop && phase != GamePhase.Battle;

        // Prevent Unity's EventSystem from routing keyboard input to UI elements.
        // During Battle phase, only clear when TallyArenaMenu is active — earlier
        // post-game screens (TallyArenaFinale, TallyArenaReward) rely on the
        // EventSystem to wait for user input and auto-advance if we clear it.
        // TallyArenaMenu needs clearing because the game auto-selects "Start New Game"
        // which conflicts with our focus system.
        bool shouldClearEventSystem = !IsEditing &&
            (inMenu || IsDialogOpen || phase == GamePhase.Shop || _tallyMenuWasActive);
        if (shouldClearEventSystem)
        {
            try
            {
                var es = EventSystem.current;
                if (es != null && es.currentSelectedGameObject != null)
                    es.SetSelectedGameObject(null);
            }
            catch { }
        }

        // During Shop/Battle, only dialog/refresh logic runs (handled above)
        if (!inMenu)
            return;

        // ── Menu-phase logic below ──

        // Fallback page-change detection: if PageManager.CurrentPage differs from
        // _currentPage (e.g. VersusCreator → VersusLobby transition that bypassed
        // the PageManager.Open Harmony patch), trigger a rescan.
        if (!_needsScan)
        {
            try
            {
                var currentPageFromManager = _menu?.PageManager?.CurrentPage;
                if (currentPageFromManager != null && _currentPage != null
                    && currentPageFromManager.Pointer != _currentPage.Pointer)
                {
                    _log?.LogInfo($"Fallback page change detected: {_currentPage.gameObject?.name} → {currentPageFromManager.gameObject?.name}");
                    _currentPage = currentPageFromManager;
                    _needsScan = true;
                    _scanDelay = 0.3f;
                    _restoreGroupName = null;
                }
            }
            catch { }
        }

        // Customize sub-page change detection: the Customize page has its own
        // internal PageManager that doesn't trigger our main PageManager.Open patch.
        // Poll for sub-page changes when on the Customize page.
        if (!_needsScan && _customizeSubPagePtr != System.IntPtr.Zero)
        {
            try
            {
                var customize = _currentPage?.TryCast<Spacewood.Unity.Customize>();
                if (customize != null)
                {
                    var subPage = customize.PageManager?.CurrentPage;
                    System.IntPtr currentPtr = System.IntPtr.Zero;
                    try { currentPtr = subPage?.Pointer ?? System.IntPtr.Zero; } catch { }

                    if (currentPtr != _customizeSubPagePtr)
                    {
                        _log?.LogInfo("Customize sub-page change detected");
                        _customizeSubPagePtr = currentPtr;
                        _needsScan = true;
                        _scanDelay = 0.2f;
                        _restoreGroupName = null;
                    }
                }
                else
                {
                    _customizeSubPagePtr = System.IntPtr.Zero;
                }
            }
            catch { _customizeSubPagePtr = System.IntPtr.Zero; }
        }

        // VersusLobby: periodic poll for player count changes
        if (_cachedVersusLobby != null && !IsDialogOpen)
        {
            _versusLobbyPollTimer -= Time.deltaTime;
            if (_versusLobbyPollTimer <= 0f)
            {
                _versusLobbyPollTimer = 1.5f;
                try
                {
                    int currentCount = 0;
                    try
                    {
                        var lobbyItems = _cachedVersusLobby.ItemContainer?.GetComponentsInChildren<Spacewood.Unity.VersusLobbyItem>(false);
                        currentCount = lobbyItems?.Count ?? 0;
                    }
                    catch { }
                    if (currentCount != _versusLobbyPlayerCount)
                    {
                        _log?.LogInfo($"VersusLobby player count changed: {_versusLobbyPlayerCount} → {currentCount}");
                        RequestRescan();
                    }
                }
                catch { }
            }
        }

        if (_needsScan)
        {
            // Don't rescan the page while a dialog is open — it would overwrite
            // the dialog's focus groups (e.g. alert Confirm/Cancel buttons)
            if (IsDialogOpen)
            {
                _needsScan = false;
            }
            else
            {
                _scanDelay -= Time.deltaTime;
                if (_scanDelay <= 0f)
                {
                    _needsScan = false;
                    ScanCurrentPage();
                }
            }
        }

        // Delayed activation: activate the input field one frame after Enter
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
            try
            {
                if (_activeInputField != null)
                    _trackedText = _activeInputField.text ?? "";
            }
            catch { }

            if (Time.frameCount > _editStartFrame + 1 && Input.GetKeyDown(KeyCode.Return))
            {
                StopEditing(announce: true);
            }
        }
    }

    public void RequestRescan()
    {
        // Save current focus position so SetGroupsWithRestore can restore it after rescan.
        // Don't overwrite if already saved (e.g., by OnPickerOpened before a dialog).
        if (_restoreGroupName == null)
        {
            _restoreGroupName = FocusManager.Instance?.CurrentGroup?.Name;
            _restoreElementIndex = FocusManager.Instance?.CurrentElementIndex ?? 0;
        }
        _needsScan = true;
        _scanDelay = 0.2f;
    }

    /// <summary>Schedules a shop state refresh after a short delay (for roll, buy, sell, freeze).
    /// If announceGold is true, the refreshed gold value will be spoken after the refresh.</summary>
    public void ScheduleShopRefresh(bool announceGold = false)
    {
        _needsShopRefresh = true;
        _shopRefreshDelay = 0.5f;
        if (announceGold)
            _announceGoldAfterRefresh = true;
    }

    /// <summary>Ensures _cachedHangar points to a live HangarMain.
    /// Re-finds it in the scene if null or if the IL2CPP object was destroyed.</summary>
    private void EnsureHangar()
    {
        bool needsFind = _cachedHangar == null;
        if (!needsFind)
        {
            try { needsFind = _cachedHangar!.gameObject == null; }
            catch { needsFind = true; }
        }

        if (needsFind)
        {
            try
            {
                _cachedHangar = Object.FindObjectOfType<Spacewood.Unity.MonoBehaviours.Build.HangarMain>();
                if (_cachedHangar != null)
                {
                    _log?.LogInfo("HangarMain re-detected via EnsureHangar");
                    if (ShopAnnouncer.Instance != null)
                        ShopAnnouncer.Instance.Hangar = _cachedHangar;
                }
            }
            catch { _cachedHangar = null; }
        }
    }

    private void RefreshShopState()
    {
        EnsureHangar();
        if (_cachedHangar == null) return;

        try
        {
            var board = _cachedHangar.BuildModel?.Board;
            if (board != null)
            {
                ShopStateReader.Instance.ReadFromBoard(board);
                TeamStateReader.Instance.ReadFromBoard(board);
                BuildShopFocusGroups(_cachedHangar, silent: true);

                if (_announceGoldAfterRefresh)
                {
                    _announceGoldAfterRefresh = false;
                    ScreenReader.Instance.Say($"Rolled. {ShopStateReader.Instance.Gold} gold remaining.");
                }

                // Reset short-lived action guards (roll, sell) after refresh completes
                if (ShopAnnouncer.Instance != null)
                {
                    ShopAnnouncer.Instance.ResetRollAndSellGuards();
                }

                _log?.LogInfo("Shop state refreshed");
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Shop refresh error: {ex}");
        }
    }

    // ── Shop Focus Groups ────────────────────────────────────────────

    /// <summary>Builds focus groups for navigating shop pets, food, and team during shop phase.
    /// Each element uses InfoRows for the buffer system (name → stats → extras).</summary>
    private void BuildShopFocusGroups(Spacewood.Unity.MonoBehaviours.Build.HangarMain hangar, bool silent = false)
    {
        var board = hangar.BuildModel?.Board;
        if (board == null) return;

        var groups = new List<FocusGroup>();

        // ── Shop group (pets + food) ──
        var shopGroup = new FocusGroup("Shop");
        try
        {
            if (board.MinionShop != null)
            {
                for (int i = 0; i < board.MinionShop.Count; i++)
                {
                    var minion = board.MinionShop[i];
                    if (minion == null) continue;

                    string name = GetMinionName(minion);
                    int atk = minion.Attack?.Total ?? 0;
                    int hp = minion.Health?.Total ?? 0;

                    // Build info rows
                    var rows = new List<string>();
                    rows.Add(name); // Row 0: Name
                    rows.Add($"{atk} attack, {hp} health"); // Row 1: Stats
                    string costRow = $"{minion.Price} gold";
                    if (minion.Frozen) costRow += ", frozen";
                    rows.Add(costRow); // Row 2: Cost + frozen

                    // Row 3: Ability description
                    string? ability = null;
                    try { ability = Spacewood.Unity.Extensions.MinionModelExtensions.GetAbilityLocalized(minion); } catch { }
                    if (!string.IsNullOrWhiteSpace(ability))
                        rows.Add(StripRichText(ability!));

                    var capturedMinion = minion;
                    var capturedHangar = hangar;
                    var capturedName = name;
                    var element = new FocusElement(name, i)
                    {
                        Type = "button",
                        Tag = capturedMinion,
                        InfoRows = rows,
                        OnActivate = () => BuyPet(capturedHangar, capturedMinion, capturedName)
                    };
                    shopGroup.Elements.Add(element);
                }
            }
        }
        catch (System.Exception ex) { _log?.LogError($"Shop pets scan error: {ex}"); }

        try
        {
            if (board.SpellShop != null)
            {
                for (int i = 0; i < board.SpellShop.Count; i++)
                {
                    var spell = board.SpellShop[i];
                    if (spell == null) continue;

                    string name = GetSpellName(spell);

                    // Build info rows
                    var rows = new List<string>();
                    rows.Add(name); // Row 0: Name
                    string costRow = $"{spell.Price} gold";
                    if (spell.Frozen) costRow += ", frozen";
                    rows.Add(costRow); // Row 1: Cost + frozen

                    // Row 2: Ability description
                    string? spellAbility = null;
                    try { spellAbility = Spacewood.Unity.Extensions.SpellModelExtensions.GetAbilityLocalized(spell); } catch { }
                    if (!string.IsNullOrWhiteSpace(spellAbility))
                        rows.Add(StripRichText(spellAbility!));

                    // Row 3: Perk effect (for perk-granting foods like Garlic, Honey, Meat Bone, etc.)
                    // Match SpellEnum name to Perk enum name — they match for perk-granting foods.
                    try
                    {
                        string spellEnumName = spell.Enum.ToString();
                        if (System.Enum.TryParse<Spacewood.Core.Enums.Perk>(spellEnumName, out var perkEnum))
                        {
                            var perkTemplate = Spacewood.Core.Enums.PerkConstants.GetPerk(perkEnum);
                            if (perkTemplate != null)
                            {
                                string? perkName = perkTemplate.Name;
                                string? perkAbout = perkTemplate.GetAbout();
                                if (!string.IsNullOrWhiteSpace(perkName) && !string.IsNullOrWhiteSpace(perkAbout))
                                    rows.Add($"Perk: {StripRichText(perkName!)}. {StripRichText(perkAbout!)}");
                                else if (!string.IsNullOrWhiteSpace(perkName))
                                    rows.Add($"Perk: {StripRichText(perkName!)}");
                            }
                        }
                    }
                    catch { }

                    var capturedSpell = spell;
                    var capturedHangar = hangar;
                    var capturedName = name;
                    var element = new FocusElement(name, shopGroup.Elements.Count + i)
                    {
                        Type = "button",
                        Tag = capturedSpell,
                        InfoRows = rows,
                        OnActivate = () => BuyFood(capturedHangar, capturedSpell, capturedName)
                    };
                    shopGroup.Elements.Add(element);
                }
            }
        }
        catch (System.Exception ex) { _log?.LogError($"Shop food scan error: {ex}"); }

        if (shopGroup.Elements.Count > 0)
            groups.Add(shopGroup);

        // ── Team group (sorted by position so cycling order matches battle order) ──
        var teamGroup = new FocusGroup("Team");
        try
        {
            if (board.Minions?.Items != null)
            {
                // Collect team pets with their positions, then sort by x (front to back)
                var teamPets = new List<(Spacewood.Core.Models.MinionModel minion, int x)>();
                for (int i = 0; i < board.Minions.Items.Count; i++)
                {
                    var m = board.Minions.Items[i];
                    if (m == null || m.Dead) continue;
                    teamPets.Add((m, m.Point.x));
                }
                teamPets.Sort((a, b) => a.x.CompareTo(b.x));

                for (int i = 0; i < teamPets.Count; i++)
                {
                    var minion = teamPets[i].minion;
                    string name = GetMinionName(minion);
                    int atk = minion.Attack?.Total ?? 0;
                    int hp = minion.Health?.Total ?? 0;

                    // Build info rows
                    var rows = new List<string>();
                    rows.Add(name); // Row 0: Name
                    rows.Add($"{atk} attack, {hp} health"); // Row 1: Stats

                    // Row 2: Level + XP progress + held item
                    int level = minion.Level;
                    int exp = 0;
                    try { exp = minion.Exp; } catch { }
                    // SAP XP thresholds: Level 1 needs 2 XP, Level 2 needs 3 XP, max Level 3
                    string levelText;
                    if (level >= 3)
                        levelText = "Level 3 (max)";
                    else
                    {
                        int needed = level == 1 ? 2 : 3; // XP needed for next level
                        levelText = $"Level {level}, {exp}/{needed} XP";
                    }
                    string extraRow = levelText;
                    string? perkTitle = null;
                    try { perkTitle = Spacewood.Unity.Extensions.MinionModelExtensions.GetPerkTitleLocalized(minion); } catch { }
                    if (string.IsNullOrWhiteSpace(perkTitle))
                    {
                        // Fallback to enum name
                        try { if (minion.Perk.HasValue) perkTitle = minion.Perk.Value.ToString(); } catch { }
                    }
                    if (!string.IsNullOrWhiteSpace(perkTitle))
                        extraRow += $", holding {perkTitle}";
                    rows.Add(extraRow);

                    // Row 3: Ability description
                    string? ability = null;
                    try { ability = Spacewood.Unity.Extensions.MinionModelExtensions.GetAbilityLocalized(minion); } catch { }
                    if (!string.IsNullOrWhiteSpace(ability))
                        rows.Add(StripRichText(ability!));

                    // Row 4: Held item effect (if has perk)
                    if (!string.IsNullOrWhiteSpace(perkTitle))
                    {
                        string? perkAbility = null;
                        try { perkAbility = Spacewood.Unity.Extensions.MinionModelExtensions.GetPerkAbilityLocalized(minion); } catch { }
                        if (!string.IsNullOrWhiteSpace(perkAbility))
                            rows.Add($"{perkTitle}: {StripRichText(perkAbility!)}");
                    }

                    var element = new FocusElement(name, i)
                    {
                        Type = "button",
                        Tag = minion,
                        InfoRows = rows
                    };
                    teamGroup.Elements.Add(element);
                }
            }
        }
        catch (System.Exception ex) { _log?.LogError($"Team scan error: {ex}"); }

        // When in pet placement mode, replace team elements with positional slot choices.
        // Tags use "place:<index>" where index is the desired position in the team (0=front).
        if (_pendingPet != null && teamGroup.Elements.Count > 0)
        {
            // Collect pet names in order before replacing elements
            var petNames = new List<string>();
            for (int i = 0; i < teamGroup.Elements.Count; i++)
                petNames.Add(teamGroup.Elements[i].Label);

            teamGroup.Elements.Clear();
            int count = petNames.Count;

            // Front: before first pet (index 0)
            teamGroup.Elements.Add(new FocusElement($"Front, before {petNames[0]}", 0)
            {
                Type = "button",
                Tag = "place:0"
            });

            // Between each pair of adjacent pets
            for (int i = 0; i < count - 1; i++)
            {
                teamGroup.Elements.Add(new FocusElement($"Between {petNames[i]} and {petNames[i + 1]}", i + 1)
                {
                    Type = "button",
                    Tag = $"place:{i + 1}"
                });
            }

            // Back: after last pet
            teamGroup.Elements.Add(new FocusElement($"Back, after {petNames[count - 1]}", count)
            {
                Type = "button",
                Tag = $"place:{count}"
            });
        }

        if (teamGroup.Elements.Count > 0)
            groups.Add(teamGroup);

        if (groups.Count > 0)
        {
            if (silent)
                FocusManager.Instance?.ReplaceGroupsSilent(groups);
            else
                FocusManager.Instance?.SetGroups(groups);
        }

        _log?.LogInfo($"Shop focus groups built: {shopGroup.Elements.Count} shop items, {teamGroup.Elements.Count} team pets");
    }

    private void BuyPet(
        Spacewood.Unity.MonoBehaviours.Build.HangarMain hangar,
        Spacewood.Core.Models.MinionModel minion,
        string name)
    {
        // Guard against double-buy while async is in progress
        if (ShopAnnouncer.Instance?.BuyInProgress == true)
            return;

        try
        {
            var board = hangar.BuildModel?.Board;
            if (board == null)
            {
                ScreenReader.Instance.Say("Cannot buy.");
                return;
            }

            // Check gold
            if (board.Gold < minion.Price)
            {
                ScreenReader.Instance.Say($"Not enough gold. Need {minion.Price}, have {board.Gold}.");
                return;
            }

            // Count current team size
            int teamSize = 0;
            if (board.Minions?.Items != null)
            {
                for (int i = 0; i < board.Minions.Items.Count; i++)
                {
                    var m = board.Minions.Items[i];
                    if (m != null && !m.Dead) teamSize++;
                }
            }

            if (teamSize >= 5)
            {
                ScreenReader.Instance.Say("Team is full.");
                return;
            }

            // Empty team: place directly at position 0
            if (teamSize == 0)
            {
                if (ShopAnnouncer.Instance != null)
                    ShopAnnouncer.Instance.BuyInProgress = true;
                var targetPoint = new Spacewood.Core.System.Point(0, 0);
                hangar.PlayMinionAsync(minion, targetPoint, null);
                ScreenReader.Instance.Say($"Bought {name}.");
                _log?.LogInfo($"Buy pet: {name} at position 0 (empty team)");
                ScheduleShopRefresh();
                return;
            }

            // Enter placement mode — user picks a team position
            _pendingPet = minion;
            _pendingPetName = name;
            ScreenReader.Instance.Say($"Place {name}. Choose a team position, then press Enter.");
            _log?.LogInfo($"Pet placement started: {name}");

            // Rebuild focus groups so Team includes "End of team" element, then focus Team
            BuildShopFocusGroups(hangar, silent: false);
            FocusManager.Instance?.FocusGroupByName("Team");
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Buy pet error: {ex}");
            ScreenReader.Instance.Say("Buy failed.");
        }
    }

    private void BuyFood(
        Spacewood.Unity.MonoBehaviours.Build.HangarMain hangar,
        Spacewood.Core.Models.SpellModel spell,
        string name)
    {
        try
        {
            var board = hangar.BuildModel?.Board;
            if (board == null)
            {
                ScreenReader.Instance.Say("Cannot buy.");
                return;
            }

            // Check gold
            if (board.Gold < spell.Price)
            {
                ScreenReader.Instance.Say($"Not enough gold. Need {spell.Price}, have {board.Gold}.");
                return;
            }

            // Check if this food requires manual targeting
            bool needsTarget = false;
            try
            {
                needsTarget = Spacewood.Core.Actions.Board.BoardPartExtensions.CanAimSpell(board, spell);
            }
            catch (System.Exception ex)
            {
                _log?.LogWarning($"CanAimSpell check failed, assuming targeted: {ex.Message}");
                needsTarget = true;
            }

            if (needsTarget)
            {
                // Enter food targeting mode — user must select a team pet next
                _pendingFood = spell;
                _pendingFoodName = name;
                ScreenReader.Instance.Say($"{name} selected. Press B, choose a target pet, then press Enter.");
                _log?.LogInfo($"Food targeting started (targeted): {name}");
            }
            else
            {
                // Non-targeted food — apply directly without asking for a target
                hangar.PlaySpellAsync(spell, (Spacewood.Core.Models.MinionModel?)null);
                ScreenReader.Instance.Say($"Bought {name}.");
                _log?.LogInfo($"Food bought (non-targeted): {name}");
                ScheduleShopRefresh();
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Buy food error: {ex}");
            ScreenReader.Instance.Say("Buy failed.");
        }
    }

    /// <summary>Applies the pending food item to the specified team pet.</summary>
    public void ApplyPendingFood(Spacewood.Core.Models.MinionModel target, string targetName)
    {
        if (_pendingFood == null || _cachedHangar == null)
        {
            ScreenReader.Instance.Say("No food selected.");
            return;
        }

        try
        {
            var spell = _pendingFood;
            var foodName = _pendingFoodName ?? "food";
            _pendingFood = null;
            _pendingFoodName = null;

            _cachedHangar.PlaySpellAsync(spell, target);
            ScreenReader.Instance.Say($"Used {foodName} on {targetName}.");
            _log?.LogInfo($"Food applied: {foodName} → {targetName}");
            ScheduleShopRefresh();
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Apply food error: {ex}");
            ScreenReader.Instance.Say("Failed to use food.");
            _pendingFood = null;
            _pendingFoodName = null;
        }
    }

    /// <summary>Cancels the pending food targeting mode.</summary>
    public void CancelPendingFood()
    {
        if (_pendingFood != null)
        {
            _pendingFood = null;
            _pendingFoodName = null;
            ScreenReader.Instance.Say("Food cancelled.");
        }
    }

    /// <summary>Whether there is a pending food item waiting for a target pet.</summary>
    public bool HasPendingFood => _pendingFood != null;

    /// <summary>Whether there is a pending pet waiting for a placement position.</summary>
    public bool HasPendingPet => _pendingPet != null;

    /// <summary>Places the pending pet at the specified team index.
    /// Uses PlayMinionAsync to buy, then OrderMinion to reorder to the exact index.</summary>
    public void PlacePendingPet(int desiredIndex, string positionLabel)
    {
        if (_pendingPet == null || _cachedHangar == null)
        {
            ScreenReader.Instance.Say("No pet selected.");
            return;
        }

        try
        {
            var pet = _pendingPet;
            var petName = _pendingPetName ?? "pet";
            _pendingPet = null;
            _pendingPetName = null;

            // Buy the pet — place at position 0 (front). The exact position doesn't
            // matter because we'll reorder after buying.
            var targetPoint = new Spacewood.Core.System.Point(0, 0);
            _cachedHangar.PlayMinionAsync(pet, targetPoint, null);

            // Schedule a delayed reorder to move the pet to the correct index.
            _pendingReorderPetId = pet.Id.Unique;
            _pendingReorderIndex = desiredIndex;
            _pendingReorderName = petName;
            _pendingReorderLabel = positionLabel;
            _needsReorder = true;
            _reorderDelay = 0.4f;

            _log?.LogInfo($"Pet bought: {petName}, will reorder to index {desiredIndex}");
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Place pet error: {ex}");
            ScreenReader.Instance.Say("Placement failed.");
            _pendingPet = null;
            _pendingPetName = null;
        }
    }

    // Pending reorder state (after buying a pet, reorder to correct index)
    private bool _needsReorder;
    private float _reorderDelay;
    private long _pendingReorderPetId;
    private int _pendingReorderIndex;
    private string? _pendingReorderName;
    private string? _pendingReorderLabel;

    /// <summary>Reorders the recently placed pet to the desired team index by
    /// performing sequential swaps (same mechanism as ShiftPet).</summary>
    private void PerformPendingReorder()
    {
        _needsReorder = false;

        if (_cachedHangar == null) goto announce;
        try
        {
            var board = _cachedHangar.BuildModel?.Board;
            if (board?.Minions?.Items == null) goto announce;

            // Build sorted team list (same as ShiftPet)
            var teamPets = new List<(Spacewood.Core.Models.MinionModel model, int x)>();
            for (int i = 0; i < board.Minions.Items.Count; i++)
            {
                var m = board.Minions.Items[i];
                if (m != null && !m.Dead && m.Location == Spacewood.Core.Enums.Location.Team)
                    teamPets.Add((m, m.Point.x));
            }
            teamPets.Sort((a, b) => a.x.CompareTo(b.x));

            // Find the placed pet's current index in the sorted list
            int currentIdx = -1;
            for (int i = 0; i < teamPets.Count; i++)
            {
                if (teamPets[i].model.Id.Unique == _pendingReorderPetId)
                {
                    currentIdx = i;
                    break;
                }
            }

            if (currentIdx < 0)
            {
                _log?.LogWarning("Reorder: placed pet not found on team");
                goto announce;
            }

            int targetIdx = System.Math.Min(_pendingReorderIndex, teamPets.Count - 1);
            _log?.LogInfo($"Reorder: {_pendingReorderName} currentIdx={currentIdx}, targetIdx={targetIdx}");

            if (currentIdx == targetIdx)
            {
                _log?.LogInfo("Reorder: already at correct index");
                goto announce;
            }

            var minionArmy = _cachedHangar.MinionArmy;
            var state = _cachedHangar.StateMachine?.State;
            if (minionArmy?.Spaces?.Items == null || state == null)
            {
                _log?.LogWarning("Reorder: MinionArmy or StateMachine not available");
                goto announce;
            }

            // Swap step by step toward the target index
            int step = currentIdx < targetIdx ? 1 : -1;
            for (int idx = currentIdx; idx != targetIdx; idx += step)
            {
                int neighborIdx = idx + step;
                var neighborPoint = teamPets[neighborIdx].model.Point;

                // Find the Space at the neighbor's position
                Spacewood.Unity.Views.Space? targetSpace = null;
                for (int s = 0; s < minionArmy.Spaces.Items.Count; s++)
                {
                    var space = minionArmy.Spaces.Items[s];
                    if (space != null && space.Point.x == neighborPoint.x && space.Point.y == neighborPoint.y)
                    {
                        targetSpace = space;
                        break;
                    }
                }

                if (targetSpace == null)
                {
                    _log?.LogWarning($"Reorder: no Space at ({neighborPoint.x},{neighborPoint.y})");
                    break;
                }

                // Swap our pet with the neighbor
                // After each swap, our pet is now at neighborIdx and the neighbor moved to idx
                var petToMove = teamPets[idx].model;
                bool result = state.OrderMinion(petToMove, targetSpace, false);
                _log?.LogInfo($"Reorder swap: idx {idx} → {neighborIdx}, result={result}");

                // Update the list to reflect the swap
                var temp = teamPets[idx];
                teamPets[idx] = teamPets[neighborIdx];
                teamPets[neighborIdx] = temp;
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Reorder error: {ex}");
        }

        announce:
        ScreenReader.Instance.Say($"Placed {_pendingReorderName}. {_pendingReorderLabel}.");
        ScheduleShopRefresh();
    }

    /// <summary>Cancels the pending pet placement mode.</summary>
    public void CancelPendingPet()
    {
        if (_pendingPet != null)
        {
            _pendingPet = null;
            _pendingPetName = null;
            ScreenReader.Instance.Say("Placement cancelled.");
            ScheduleShopRefresh();
        }
    }

    /// <summary>Merges the focused pet onto a matching pet.
    /// Shop pet focused → buys and merges onto a matching team pet (PlayMinionAsync).
    /// Team pet focused → merges with a matching teammate (StackMinionAsync).</summary>
    public void MergePet()
    {
        if (_cachedHangar == null)
        {
            ScreenReader.Instance.Say("Cannot merge.");
            return;
        }

        var groupName = FocusManager.Instance?.CurrentGroup?.Name;
        var element = FocusManager.Instance?.CurrentElement;

        if (element?.Tag is not Spacewood.Core.Models.MinionModel focusedMinion)
        {
            ScreenReader.Instance.Say("Focus a pet to merge.");
            return;
        }

        if (groupName == "Shop")
            MergeFromShop(focusedMinion, element.Label);
        else if (groupName == "Team")
            MergeOnTeam(focusedMinion, element.Label);
        else
            ScreenReader.Instance.Say("Focus a pet to merge.");
    }

    private void MergeFromShop(Spacewood.Core.Models.MinionModel shopMinion, string shopName)
    {
        try
        {
            var board = _cachedHangar!.BuildModel?.Board;
            if (board?.Minions?.Items == null)
            {
                ScreenReader.Instance.Say("No team pets to merge with.");
                return;
            }

            if (board.Gold < shopMinion.Price)
            {
                ScreenReader.Instance.Say($"Not enough gold. Need {shopMinion.Price}, have {board.Gold}.");
                return;
            }

            // Find a matching team pet (same species)
            Spacewood.Core.Models.MinionModel? target = null;
            string targetName = "";
            for (int i = 0; i < board.Minions.Items.Count; i++)
            {
                var m = board.Minions.Items[i];
                if (m == null || m.Dead) continue;
                if (m.Enum == shopMinion.Enum)
                {
                    target = m;
                    targetName = GetMinionName(m);
                    break;
                }
            }

            if (target == null)
            {
                ScreenReader.Instance.Say($"No matching {shopName} on your team to merge with.");
                return;
            }

            // PlayMinionAsync onto the target's point — game auto-merges onto matching pet
            _cachedHangar.PlayMinionAsync(shopMinion, target.Point, (Spacewood.Core.Models.MinionModel?)null);

            ScreenReader.Instance.Say($"Merged {shopName} onto {targetName}.");
            _log?.LogInfo($"Shop merge: {shopName} onto {targetName} at point ({target.Point.x},{target.Point.y})");
            ScheduleShopRefresh();
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Shop merge error: {ex}");
            ScreenReader.Instance.Say("Merge failed.");
        }
    }

    private void MergeOnTeam(Spacewood.Core.Models.MinionModel sourceMinion, string sourceName)
    {
        try
        {
            var board = _cachedHangar!.BuildModel?.Board;
            if (board?.Minions?.Items == null)
            {
                ScreenReader.Instance.Say("No team pets to merge with.");
                return;
            }

            // Find another team pet of the same species (not the same instance)
            Spacewood.Core.Models.MinionModel? target = null;
            string targetName = "";
            for (int i = 0; i < board.Minions.Items.Count; i++)
            {
                var m = board.Minions.Items[i];
                if (m == null || m.Dead) continue;
                if (m.Enum == sourceMinion.Enum && m.Id.Unique != sourceMinion.Id.Unique)
                {
                    target = m;
                    targetName = GetMinionName(m);
                    break;
                }
            }

            if (target == null)
            {
                ScreenReader.Instance.Say($"No other {sourceName} on your team to merge with.");
                return;
            }

            _cachedHangar.StackMinionAsync(sourceMinion, target.Id);
            ScreenReader.Instance.Say($"Merged {sourceName} onto {targetName}.");
            _log?.LogInfo($"Team merge: {sourceName} → {targetName}");
            ScheduleShopRefresh();
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Team merge error: {ex}");
            ScreenReader.Instance.Say("Merge failed.");
        }
    }

    /// <summary>Moves the currently focused team pet left or right by one position.</summary>
    public void ShiftPet(int direction)
    {
        if (_cachedHangar == null) return;

        // Only allow shifting team pets
        if (FocusManager.Instance?.CurrentGroup?.Name != "Team")
        {
            ScreenReader.Instance.Say("No team pet focused.");
            return;
        }

        var element = FocusManager.Instance?.CurrentElement;
        if (element?.Tag is not Spacewood.Core.Models.MinionModel minion)
        {
            ScreenReader.Instance.Say("No team pet focused.");
            return;
        }

        try
        {
            var board = _cachedHangar.BuildModel?.Board;
            if (board?.Minions?.Items == null) return;

            // Build sorted list of team pets by x position (left = front, right = back)
            var teamPets = new List<(Spacewood.Core.Models.MinionModel model, string name, int x)>();
            for (int i = 0; i < board.Minions.Items.Count; i++)
            {
                var m = board.Minions.Items[i];
                if (m == null || m.Dead) continue;
                teamPets.Add((m, GetMinionName(m), m.Point.x));
            }
            teamPets.Sort((a, b) => a.x.CompareTo(b.x));

            // Find current pet in sorted list
            int currentIdx = -1;
            for (int i = 0; i < teamPets.Count; i++)
            {
                if (teamPets[i].model.Id.Unique == minion.Id.Unique)
                {
                    currentIdx = i;
                    break;
                }
            }
            if (currentIdx < 0) return;

            int targetIdx = currentIdx + direction;
            if (targetIdx < 0 || targetIdx >= teamPets.Count)
            {
                ScreenReader.Instance.Say(direction < 0 ? "Already at front." : "Already at back.");
                return;
            }

            // Use the game's built-in OrderMinion through the state machine
            // This handles both the local board update and server sync
            var minionA = teamPets[currentIdx].model;
            var pointB = teamPets[targetIdx].model.Point;

            // Find the target Space object from the MinionArmy grid
            var minionArmy = _cachedHangar.MinionArmy;
            if (minionArmy?.Spaces?.Items == null)
            {
                _log?.LogError("MinionArmy.Spaces not available for reorder");
                ScreenReader.Instance.Say("Shift failed.");
                return;
            }

            Spacewood.Unity.Views.Space? targetSpace = null;
            for (int i = 0; i < minionArmy.Spaces.Items.Count; i++)
            {
                var space = minionArmy.Spaces.Items[i];
                if (space != null && space.Point.x == pointB.x && space.Point.y == pointB.y)
                {
                    targetSpace = space;
                    break;
                }
            }

            if (targetSpace == null)
            {
                _log?.LogError($"No Space found at Point({pointB.x},{pointB.y})");
                ScreenReader.Instance.Say("Shift failed.");
                return;
            }

            // Call the game's OrderMinion through the hangar state machine
            var stateMachine = _cachedHangar.StateMachine;
            var state = stateMachine?.State;
            if (state == null)
            {
                _log?.LogError("HangarStateMachine.State is null");
                ScreenReader.Instance.Say("Shift failed.");
                return;
            }

            bool result = state.OrderMinion(minionA, targetSpace, false);
            _log?.LogInfo($"OrderMinion via state machine: {teamPets[currentIdx].name} to Point({pointB.x},{pointB.y}), result={result}");

            // Build announcement based on new position
            // After swap: our pet is at targetIdx, displaced pet is at currentIdx
            string petName = teamPets[currentIdx].name;
            string displacedName = teamPets[targetIdx].name;
            string posAnnouncement;

            if (targetIdx == 0)
            {
                posAnnouncement = $"{petName} shifted to front.";
            }
            else if (targetIdx == teamPets.Count - 1)
            {
                posAnnouncement = $"{petName} shifted to back.";
            }
            else
            {
                // Our pet is now at targetIdx. Figure out neighbors in the new order.
                // The displaced pet moved to currentIdx.
                string leftName, rightName;
                if (direction < 0)
                {
                    // Shifted left: left neighbor is unchanged, right neighbor is the displaced pet
                    leftName = teamPets[targetIdx - 1].name;
                    rightName = displacedName;
                }
                else
                {
                    // Shifted right: left neighbor is the displaced pet, right neighbor is unchanged
                    leftName = displacedName;
                    rightName = teamPets[targetIdx + 1].name;
                }
                posAnnouncement = $"{petName} shifted between {leftName} and {rightName}.";
            }

            ScreenReader.Instance.Say(posAnnouncement);
            _log?.LogInfo($"Pet shifted: {petName} to position {targetIdx}");

            ScheduleShopRefresh();
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Shift pet error: {ex}");
            ScreenReader.Instance.Say("Shift failed.");
        }
    }

    /// <summary>Called after a delay when returning from battle. Board data should be populated by now.</summary>
    private void PerformPostBattleSetup()
    {
        if (_cachedHangar == null) return;

        try
        {
            // Skip if TallyArena already announced the result (avoids duplicate/conflicting announcements)
            if (_tallyAnnouncedResult)
            {
                _log?.LogInfo("Skipping post-battle announcement (TallyArena already announced)");
                _tallyAnnouncedResult = false;
            }
            else
            {
                AnnounceBattleResult(_cachedHangar);
            }

            if (IsDialogOpen)
            {
                _log?.LogInfo("Dialog is open — deferring shop setup after battle");
            }
            else if (!_dockWasActive)
            {
                var board = _cachedHangar.BuildModel?.Board;
                if (board != null)
                {
                    ShopStateReader.Instance.ReadFromBoard(board);
                    TeamStateReader.Instance.ReadFromBoard(board);
                }
                ShopAnnouncer.Instance?.OnTurnStart();
                BuildShopFocusGroups(_cachedHangar);
            }
            else
            {
                _log?.LogInfo("Dock is active — deferring shop setup after battle");
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Post-battle setup error: {ex}");
        }
    }

    /// <summary>Announces the battle result by comparing lives/victories before and after.</summary>
    private void AnnounceBattleResult(Spacewood.Unity.MonoBehaviours.Build.HangarMain hangar)
    {
        try
        {
            var board = hangar.BuildModel?.Board;
            if (board == null) return;

            int newLives = board.Lives;
            int newVictories = board.Victories;
            _log?.LogInfo($"Post-battle: Lives={newLives} (was {_preBattleLives}), Victories={newVictories} (was {_preBattleVictories})");

            string result;
            if (newVictories > _preBattleVictories)
            {
                result = "You won!";
            }
            else if (newLives < _preBattleLives)
            {
                int livesLost = _preBattleLives - newLives;
                result = $"You lost. Lost {livesLost} {(livesLost == 1 ? "life" : "lives")}. {newLives} remaining.";
            }
            else
            {
                result = "Draw.";
            }

            ScreenReader.Instance.Say(result);
            _log?.LogInfo($"Battle result announced: {result}");
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Battle result error: {ex}");
        }
    }

    /// <summary>Strips Unity rich text tags (e.g. &lt;color&gt;, &lt;b&gt;) from a string for screen reader output.</summary>
    private static string StripRichText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Remove all XML-style tags: <tag>, </tag>, <tag=value>, <tag="value">
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
        // Remove icon placeholders: {AttackIcon}, {HealthIcon}, etc.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\{[^}]+\}", "");
        // Collapse multiple spaces left behind by removed tokens
        text = System.Text.RegularExpressions.Regex.Replace(text, @"  +", " ");
        return text.Trim();
    }

    private static string GetMinionName(Spacewood.Core.Models.MinionModel minion)
    {
        try
        {
            return Spacewood.Unity.Extensions.MinionModelExtensions.GetNameLocalized(minion)
                ?? minion.Enum.ToString();
        }
        catch { return minion.Enum.ToString(); }
    }

    private static string GetSpellName(Spacewood.Core.Models.SpellModel spell)
    {
        try
        {
            return Spacewood.Unity.Extensions.SpellModelExtensions.GetNameLocalized(spell)
                ?? spell.Enum.ToString();
        }
        catch { return spell.Enum.ToString(); }
    }

    // ── Alert2 Dialog Polling ────────────────────────────────────────

    /// <summary>Polls each frame for Alert2 dialog visibility.</summary>
    private void PollForAlert()
    {
        try
        {
            if (_cachedAlert == null)
            {
                _cachedAlert = Object.FindObjectOfType<Spacewood.Unity.Alert2>();
                if (_cachedAlert == null) return;
            }

            bool isOpen = false;
            try { isOpen = _cachedAlert.IsOpen; } catch { }

            if (isOpen && !_alertWasOpen)
            {
                _alertWasOpen = true;
                OnAlertOpened();
            }
            else if (!isOpen && _alertWasOpen)
            {
                _alertWasOpen = false;
                OnAlertClosed();
            }
        }
        catch { }
    }

    private void OnAlertOpened()
    {
        _restoreGroupName = FocusManager.Instance?.CurrentGroup?.Name;
        _restoreElementIndex = FocusManager.Instance?.CurrentElementIndex ?? 0;
        IsDialogOpen = true;

        string text = "";
        try { text = _cachedAlert!.TextMesh?.text ?? ""; } catch { }

        var group = new FocusGroup("Dialog");
        var elements = new List<(float y, FocusElement element)>();

        // Confirm button
        try
        {
            var confirmBtn = _cachedAlert!.ConfirmButton;
            if (confirmBtn != null)
            {
                string label = "";
                try { label = confirmBtn.Label?.text ?? "Confirm"; } catch { label = "Confirm"; }
                float yPos = 0f;
                try { yPos = confirmBtn.transform.position.y; } catch { }

                var capturedBtn = confirmBtn;
                elements.Add((yPos, new FocusElement(label)
                {
                    Type = "button",
                    Tag = capturedBtn,
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch (System.Exception ex) { _log?.LogError($"Alert button click error: {ex}"); }
                    }
                }));
            }
        }
        catch { }

        // Cancel button
        try
        {
            var cancelBtn = _cachedAlert!.CancelButton;
            if (cancelBtn != null)
            {
                string label = "";
                try { label = cancelBtn.Label?.text ?? "Cancel"; } catch { label = "Cancel"; }
                float yPos = 0f;
                try { yPos = cancelBtn.transform.position.y; } catch { }

                var capturedBtn = cancelBtn;
                elements.Add((yPos, new FocusElement(label)
                {
                    Type = "button",
                    Tag = capturedBtn,
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch (System.Exception ex) { _log?.LogError($"Alert button click error: {ex}"); }
                    }
                }));
            }
        }
        catch { }

        if (elements.Count == 0) return;

        elements.Sort((a, b) => b.y.CompareTo(a.y));
        for (int i = 0; i < elements.Count; i++)
        {
            elements[i].element.SlotIndex = i;
            group.Elements.Add(elements[i].element);
        }

        // Announce dialog text BEFORE setting focus groups (which announces the first button)
        if (!string.IsNullOrWhiteSpace(text))
            ScreenReader.Instance.Say(text);

        FocusManager.Instance?.SetGroups(new List<FocusGroup> { group });

        _log?.LogInfo($"Alert dialog detected: {text}");
    }

    private void OnAlertClosed()
    {
        if (!IsDialogOpen) return;
        IsDialogOpen = false;

        var phase = GamePhaseTracker.Instance.CurrentPhase;
        if (phase == GamePhase.Shop || phase == GamePhase.Battle)
        {
            // Rebuild shop focus groups instead of just clearing
            if (phase == GamePhase.Shop && _cachedHangar != null)
                ScheduleShopRefresh();
            else
                FocusManager.Instance?.Clear();
        }
        else
        {
            RequestRescan();
        }

        _log?.LogInfo("Alert dialog closed");
    }

    // ── IconAlert (Tier Rank-Up / Turn Milestone) Polling ────────────

    /// <summary>Polls each frame for IconAlert visibility (tier rank-up overlays).</summary>
    private void PollForIconAlert()
    {
        try
        {
            // Get the IconAlert from the cached HangarMain if available, otherwise find it
            if (_cachedIconAlert == null)
            {
                if (_cachedHangar != null)
                {
                    try { _cachedIconAlert = _cachedHangar.iconAlert; } catch { }
                }
                if (_cachedIconAlert == null)
                    _cachedIconAlert = Object.FindObjectOfType<Spacewood.Unity.IconAlert>();
                if (_cachedIconAlert == null) return;
            }

            bool isOpen = false;
            try
            {
                // IconAlert.gameObject is disabled by default, enabled when Open() is called
                isOpen = _cachedIconAlert.gameObject.activeSelf;
            }
            catch { }

            if (isOpen && !_iconAlertWasOpen)
            {
                _iconAlertWasOpen = true;
                OnIconAlertOpened();
            }
            else if (!isOpen && _iconAlertWasOpen)
            {
                _iconAlertWasOpen = false;
                OnIconAlertClosed();
            }
        }
        catch { }
    }

    private void OnIconAlertOpened()
    {
        IsDialogOpen = true;

        // Save focus position for restore after dismiss
        _restoreGroupName = FocusManager.Instance?.CurrentGroup?.Name;
        _restoreElementIndex = FocusManager.Instance?.CurrentElementIndex ?? 0;

        // Read the tier rank-up text
        string above = "";
        string below = "";
        try { above = _cachedIconAlert!.TextMeshAbove?.text ?? ""; } catch { }
        try { below = _cachedIconAlert!.TextMeshBelow?.text ?? ""; } catch { }

        above = StripRichText(above).Trim();
        below = StripRichText(below).Trim();

        // Build announcement
        string announcement = "";
        if (!string.IsNullOrEmpty(above))
            announcement = above;
        if (!string.IsNullOrEmpty(below))
        {
            if (announcement.Length > 0)
            {
                string sep = announcement.EndsWith(".") || announcement.EndsWith("!") || announcement.EndsWith("?")
                    ? " " : ". ";
                announcement += sep;
            }
            announcement += below;
        }
        if (string.IsNullOrEmpty(announcement))
            announcement = "Tier rank up";

        // Set up a simple focus group with a confirm button
        var group = new FocusGroup("Dialog");
        var confirmElement = new FocusElement("Continue", 0)
        {
            Type = "button",
            OnActivate = () => DismissIconAlert()
        };
        group.Elements.Add(confirmElement);

        ScreenReader.Instance.Say($"{announcement}. Press Enter to continue.");
        FocusManager.Instance?.SetGroups(new List<FocusGroup> { group });
        _log?.LogInfo($"IconAlert opened: {announcement}");
    }

    private void OnIconAlertClosed()
    {
        if (!IsDialogOpen) return;
        IsDialogOpen = false;

        var phase = GamePhaseTracker.Instance.CurrentPhase;
        if (phase == GamePhase.Shop && _cachedHangar != null)
            ScheduleShopRefresh();
        else
            FocusManager.Instance?.Clear();

        _log?.LogInfo("IconAlert closed");
    }

    /// <summary>Dismisses the IconAlert by calling its Confirm method.</summary>
    public void DismissIconAlert()
    {
        if (_cachedIconAlert == null) return;

        try
        {
            _cachedIconAlert.Confirm();
            _log?.LogInfo("IconAlert dismissed via Confirm()");
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"IconAlert dismiss error: {ex}");
        }
    }

    // ── SideBar (Match Menu) ─────────────────────────────────────────

    /// <summary>Whether the sidebar (match menu) is currently open.</summary>
    public bool IsSideBarOpen => _sideBarWasOpen;

    /// <summary>Whether a picker or alert sub-dialog is currently open on top of another layer.</summary>
    public bool HasSubDialog => _pickerWasActive || _alertWasOpen;

    /// <summary>Whether the MinionPicker modal is currently open.</summary>
    public bool IsMinionPickerOpen
    {
        get
        {
            try
            {
                if (_currentPage == null) return false;
                var vc = _currentPage.TryCast<Spacewood.Unity.VersusCreator>();
                if (vc == null) return false;
                var mp = vc.MinionPicker;
                return mp != null && mp.Modal != null
                    && mp.Modal.gameObject != null && mp.Modal.gameObject.activeSelf;
            }
            catch { return false; }
        }
    }

    /// <summary>Closes the MinionPicker modal.</summary>
    public void CloseMinionPicker()
    {
        try
        {
            var vc = _currentPage?.TryCast<Spacewood.Unity.VersusCreator>();
            var mp = vc?.MinionPicker;
            if (mp != null)
            {
                mp.Close();
                RequestRescan();
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Error closing MinionPicker: {ex}");
        }
    }

    /// <summary>Opens the sidebar match menu during shop phase.</summary>
    public void OpenSideBar()
    {
        try
        {
            if (_cachedSideBar == null)
            {
                if (_menu != null)
                    try { _cachedSideBar = _menu.SideBar; } catch { }
                if (_cachedSideBar == null)
                    _cachedSideBar = Object.FindObjectOfType<Spacewood.Unity.SideBar>();
            }

            if (_cachedSideBar == null)
            {
                ScreenReader.Instance.Say("Menu not available.");
                return;
            }

            _cachedSideBar.Open();
            _log?.LogInfo("SideBar opened via Escape");
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"SideBar open error: {ex}");
        }
    }

    /// <summary>Closes the sidebar match menu.</summary>
    public void CloseSideBar()
    {
        try
        {
            _cachedSideBar?.Close();
            _log?.LogInfo("SideBar closed via Escape");
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"SideBar close error: {ex}");
        }
    }

    /// <summary>Polls for SideBar visibility and builds focus groups when it opens.</summary>
    private void PollForSideBar()
    {
        try
        {
            if (_cachedSideBar == null)
            {
                if (_menu != null)
                    try { _cachedSideBar = _menu.SideBar; } catch { }
                if (_cachedSideBar == null)
                    _cachedSideBar = Object.FindObjectOfType<Spacewood.Unity.SideBar>();
                if (_cachedSideBar == null) return;
            }

            bool isOpen = false;
            try
            {
                // SideBar Container is shown/hidden when opened/closed
                isOpen = _cachedSideBar.Container != null
                    && _cachedSideBar.Container.gameObject.activeSelf;
            }
            catch { }

            if (isOpen && !_sideBarWasOpen)
            {
                _sideBarWasOpen = true;
                IsDialogOpen = true;
                OnSideBarOpened();
            }
            else if (!isOpen && _sideBarWasOpen)
            {
                _sideBarWasOpen = false;
                IsDialogOpen = false;
                OnSideBarClosed();
            }
        }
        catch { }
    }

    private void OnSideBarOpened()
    {
        // Save current focus for restore
        _restoreGroupName = FocusManager.Instance?.CurrentGroup?.Name;
        _restoreElementIndex = FocusManager.Instance?.CurrentElementIndex ?? 0;

        var groups = new List<FocusGroup>();
        var menuGroup = new FocusGroup("Menu");
        int idx = 0;

        AddSideBarButton(menuGroup, _cachedSideBar!.Resume, "Resume", ref idx);
        AddSideBarButton(menuGroup, _cachedSideBar!.List, "Pet List", ref idx);
        AddSideBarButton(menuGroup, _cachedSideBar!.Tips, "Tips", ref idx);
        AddSideBarButton(menuGroup, _cachedSideBar!.Account, "Account", ref idx);
        AddSideBarButton(menuGroup, _cachedSideBar!.Feedback, "Feedback", ref idx);
        AddSideBarButton(menuGroup, _cachedSideBar!.Credits, "Credits", ref idx);
        AddSideBarButton(menuGroup, _cachedSideBar!.Abandon, "Abandon", ref idx);
        AddSideBarButton(menuGroup, _cachedSideBar!.Return, "Return to Menu", ref idx);
        AddSideBarButton(menuGroup, _cachedSideBar!.LogOut, "Log Out", ref idx);
        AddSideBarButton(menuGroup, _cachedSideBar!.Quit, "Quit", ref idx);

        if (menuGroup.Elements.Count > 0)
        {
            groups.Add(menuGroup);
            // Say title first (interrupt), then SetGroups queues the first button after it
            ScreenReader.Instance.Say("Match menu.");
            FocusManager.Instance?.SetGroups(groups);
        }

        _log?.LogInfo($"SideBar opened: {menuGroup.Elements.Count} buttons");
    }

    private void AddSideBarButton(FocusGroup group, Spacewood.Unity.UI.ButtonBase? button, string label, ref int idx)
    {
        if (button == null) return;
        try { if (!button.gameObject.activeInHierarchy) return; } catch { return; }

        // Try to read the button's own label
        string displayLabel = label;
        try
        {
            string? title = button.GetTitle();
            if (!string.IsNullOrWhiteSpace(title))
                displayLabel = title!;
            else
            {
                string? labelText = button.Label?.text;
                if (!string.IsNullOrWhiteSpace(labelText))
                    displayLabel = labelText!;
            }
        }
        catch { }

        var capturedButton = button;
        var element = new FocusElement(displayLabel, idx++)
        {
            Type = "button",
            OnActivate = () =>
            {
                try
                {
                    _log?.LogInfo($"SideBar button activated: {displayLabel}");
                    capturedButton.Click();
                }
                catch (System.Exception ex)
                {
                    _log?.LogError($"SideBar button error: {ex}");
                }
            }
        };
        group.Elements.Add(element);
    }

    private void OnSideBarClosed()
    {
        // Clear saved restore position — sidebar actions (Abandon, Return to Menu)
        // cause scene transitions where the old position is meaningless
        _restoreGroupName = null;

        // Don't refresh shop if another dialog (alert, picker) is still open
        if (_alertWasOpen || _pickerWasActive)
        {
            _log?.LogInfo($"SideBar closed — dialog active (alert={_alertWasOpen} picker={_pickerWasActive}), skipping shop refresh");
            return;
        }

        var phase = GamePhaseTracker.Instance.CurrentPhase;
        if (phase == GamePhase.Shop)
        {
            EnsureHangar();
            if (_cachedHangar != null)
                ScheduleShopRefresh();
            else
                FocusManager.Instance?.Clear();
        }
        else
        {
            FocusManager.Instance?.Clear();
        }

        _log?.LogInfo("SideBar closed");
    }

    // ── Picker Dialog Polling ────────────────────────────────────────

    /// <summary>Polls each frame for Picker visibility changes.</summary>
    private void PollForPicker()
    {
        try
        {
            if (_cachedPicker == null)
            {
                _cachedPicker = Object.FindObjectOfType<Spacewood.Unity.UI.Picker>();
                if (_cachedPicker == null) return;
            }

            bool isActive = false;
            try
            {
                // Check if Picker's Canvas is enabled (more reliable than Container)
                var canvas = _cachedPicker.GetComponentInParent<Canvas>();
                if (canvas != null)
                    isActive = canvas.enabled && canvas.gameObject.activeInHierarchy;
                else
                    isActive = _cachedPicker.Container != null
                        && _cachedPicker.Container.gameObject != null
                        && _cachedPicker.Container.gameObject.activeInHierarchy;
            }
            catch { }

            if (isActive && !_pickerWasActive)
            {
                _pickerWasActive = true;
                OnPickerOpened();
            }
            else if (!isActive && _pickerWasActive)
            {
                _pickerWasActive = false;
                if (IsDialogOpen)
                    OnPickerClosed();
            }
        }
        catch { }
    }

    /// <summary>Called when a Picker dialog becomes visible.</summary>
    public void OnPickerOpened()
    {
        if (IsDialogOpen) return;

        _restoreGroupName = FocusManager.Instance?.CurrentGroup?.Name;
        _restoreElementIndex = FocusManager.Instance?.CurrentElementIndex ?? 0;
        IsDialogOpen = true;

        _needsPickerScan = true;
        _pickerScanDelay = 0.15f;
        _log?.LogInfo("Picker dialog detected");
    }

    /// <summary>Called from MenuPatches when Picker closes (Pick or backdrop).</summary>
    public void OnPickerClosed(string? chosenLabel = null)
    {
        if (!IsDialogOpen) return;
        IsDialogOpen = false;
        _needsPickerScan = false;

        if (!string.IsNullOrWhiteSpace(chosenLabel))
            ScreenReader.Instance.Say($"Selected: {chosenLabel}");

        var phase = GamePhaseTracker.Instance.CurrentPhase;
        if (phase == GamePhase.Shop || phase == GamePhase.Battle)
        {
            // Rebuild shop focus groups instead of just clearing
            if (phase == GamePhase.Shop && _cachedHangar != null)
                ScheduleShopRefresh();
            else
                FocusManager.Instance?.Clear();
        }
        else
        {
            RequestRescan();
        }

        _log?.LogInfo("Picker dialog closed");
    }

    private void ScanPicker()
    {
        Spacewood.Unity.UI.Picker? picker = null;
        try { picker = Object.FindObjectOfType<Spacewood.Unity.UI.Picker>(); } catch { }
        if (picker == null)
        {
            _log?.LogWarning("Picker not found for scan");
            IsDialogOpen = false;
            return;
        }

        string title = "";
        try { title = picker.Title?.text ?? ""; } catch { }
        string note = "";
        try { note = picker.Note?.text ?? ""; } catch { }

        var group = new FocusGroup("Dialog");
        var elements = new List<(float y, FocusElement element)>();

        // Check if this picker has associated PickerItems (for reading state)
        Spacewood.Unity.UI.ButtonBase? buttonTarget = null;
        Il2CppSystem.Collections.Generic.List<Spacewood.Unity.UI.PickerItem>? pickerItems = null;
        try
        {
            buttonTarget = picker.ButtonTarget;
            if (buttonTarget != null)
                pickerItems = buttonTarget.PickerItems;
        }
        catch { }

        // Scan picker buttons
        try
        {
            var pickerButtons = picker.Buttons;
            if (pickerButtons != null)
            {
                for (int i = 0; i < pickerButtons.Count; i++)
                {
                    var pb = pickerButtons[i];
                    if (pb?.Primary == null) continue;

                    string label = "";
                    try { label = pb.Primary.GetTitle(); } catch { }
                    if (string.IsNullOrWhiteSpace(label))
                        try { label = pb.Primary.Label?.text ?? ""; } catch { }
                    if (string.IsNullOrWhiteSpace(label)) continue;

                    // Read right-side label (shows current state for some items)
                    string? rightLabel = null;
                    try { rightLabel = pb.Primary.LabelRight?.text; } catch { }

                    // Check if this button is the currently selected item.
                    // Match by label text rather than index, since the Buttons list
                    // and PickerItems list may have different orderings.
                    bool isSelected = false;
                    try
                    {
                        if (pickerItems != null)
                        {
                            for (int j = 0; j < pickerItems.Count; j++)
                            {
                                var pItem = pickerItems[j];
                                if (pItem != null && pItem.Picked)
                                {
                                    string pickedLabel = "";
                                    try { pickedLabel = pItem.Label?.GetLocalizedString() ?? ""; } catch { }
                                    if (string.IsNullOrEmpty(pickedLabel))
                                        try { pickedLabel = pItem.Value ?? ""; } catch { }
                                    isSelected = (label == pickedLabel);
                                    break;
                                }
                            }
                        }
                    }
                    catch { }

                    // Build descriptive label: show "selected" for the active item.
                    // When pickerItems are available, suppress rightLabel (e.g. "On")
                    // for unselected items to avoid misleading announcements.
                    string fullLabel = label;
                    if (isSelected)
                        fullLabel += ", selected";
                    else if (pickerItems == null && !string.IsNullOrWhiteSpace(rightLabel))
                        fullLabel += $", {rightLabel}";

                    // Check for tooltip/description
                    string? tooltip = null;
                    try { tooltip = pb.Tooltip?.Text?.text; } catch { }

                    float yPos = 0f;
                    try { yPos = pb.transform.position.y; } catch { }

                    var capturedPb = pb;
                    var capturedLabel = label;
                    var element = new FocusElement(fullLabel)
                    {
                        Type = "button",
                        Tag = capturedPb,
                        OnActivate = () =>
                        {
                            try
                            {
                                _log?.LogInfo($"Activating picker button: {capturedLabel}");
                                // Use Click() which goes through the OnSubmitEvent path —
                                // the same path the Picker uses to wire up its pick handler.
                                capturedPb.Primary.Click();
                            }
                            catch (System.Exception ex)
                            {
                                _log?.LogError($"Picker button click error: {ex}");
                            }
                        }
                    };

                    if (!string.IsNullOrWhiteSpace(tooltip))
                        element.Detail = tooltip;

                    elements.Add((yPos, element));
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Picker button scan error: {ex}");
        }

        if (elements.Count == 0)
        {
            _log?.LogWarning("No picker buttons found");
            return;
        }

        // Sort top-to-bottom
        elements.Sort((a, b) => b.y.CompareTo(a.y));

        for (int i = 0; i < elements.Count; i++)
        {
            elements[i].element.SlotIndex = i;
            group.Elements.Add(elements[i].element);
        }

        // Announce dialog title BEFORE setting focus groups (which announces the first button)
        string announcement = title;
        if (!string.IsNullOrWhiteSpace(note))
            announcement += ". " + note;
        if (!string.IsNullOrWhiteSpace(announcement))
            ScreenReader.Instance.Say(announcement);

        var groups = new List<FocusGroup> { group };
        FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"Picker scan: {group.Elements.Count} options. Title: {title}");
    }

    // ── TallyArena (Battle Result) Polling ─────────────────────────

    /// <summary>Polls for the TallyArena (battle result screen) during battle phase.</summary>
    private void PollForTally()
    {
        try
        {
            if (_cachedTally == null)
            {
                _cachedTally = Object.FindObjectOfType<Spacewood.Unity.TallyArena>();
                if (_cachedTally == null) return;
            }

            bool isActive = false;
            try
            {
                isActive = _cachedTally.Canvas != null
                    && _cachedTally.Canvas.enabled
                    && _cachedTally.Canvas.gameObject.activeInHierarchy;
            }
            catch { }

            if (isActive && !_tallyWasActive)
            {
                _tallyWasActive = true;
                // Delay reading — ShowStatus() hasn't activated the containers yet
                _needsTallyRead = true;
                _tallyReadDelay = 0.8f;
                _log?.LogInfo("TallyArena Canvas detected, waiting for status containers...");
            }
            else if (!isActive && _tallyWasActive)
            {
                _tallyWasActive = false;
                _cachedTally = null;
            }
        }
        catch { }
    }

    /// <summary>Called after a delay when the TallyArena battle result screen is visible.</summary>
    private void OnTallyOpened()
    {
        _log?.LogInfo("TallyArena reading result...");

        var tally = _cachedTally!;

        // Primary: read the Outcome enum directly from the TallyArena object
        // BattleOutcome: Incomplete=0, PlayerWon=1, EnemyWon=2, Draw=3, TimeoutDraw=4
        string result = "";
        try
        {
            int outcome = (int)tally.Outcome;
            _log?.LogInfo($"TallyArena Outcome field: {outcome}");
            result = outcome switch
            {
                1 => "Victory",
                2 => "Defeat",
                3 => "Draw",
                4 => "Draw",
                _ => ""
            };
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"Could not read TallyArena.Outcome: {ex.Message}");
        }

        // Fallback: check which status container is active in the hierarchy
        if (string.IsNullOrEmpty(result))
        {
            try
            {
                if (tally.StatusVictoryContainer != null && tally.StatusVictoryContainer.gameObject.activeInHierarchy)
                    result = "Victory";
                else if (tally.StatusVictoryFinaleContainer != null && tally.StatusVictoryFinaleContainer.gameObject.activeInHierarchy)
                    result = "Victory";
                else if (tally.StatusLossContainer != null && tally.StatusLossContainer.gameObject.activeInHierarchy)
                    result = "Defeat";
                else if (tally.StatusLossFinaleContainer != null && tally.StatusLossFinaleContainer.gameObject.activeInHierarchy)
                    result = "Defeat";
                else if (tally.StatusDrawContainer != null && tally.StatusDrawContainer.gameObject.activeInHierarchy)
                    result = "Draw";
                else if (tally.StatusDrawFinaleContainer != null && tally.StatusDrawFinaleContainer.gameObject.activeInHierarchy)
                    result = "Draw";
            }
            catch { }
        }

        // Last resort: compare lives/victories before and after
        if (string.IsNullOrEmpty(result))
        {
            try
            {
                int newLives = ShopStateReader.Instance.Lives;
                int newVictories = ShopStateReader.Instance.Victories;
                if (newVictories > _preBattleVictories)
                    result = "Victory";
                else if (newLives < _preBattleLives)
                    result = "Defeat";
                else
                    result = "Draw";
                _log?.LogInfo($"TallyArena result via lives/victories comparison");
            }
            catch { }
        }

        if (string.IsNullOrEmpty(result))
            result = "Battle result";

        _tallyAnnouncedResult = true;
        ScreenReader.Instance.Say($"{result}. Press Enter to continue.");
        _log?.LogInfo($"TallyArena result: {result}");
    }

    /// <summary>Dismisses the TallyArena by clicking its continue button.</summary>
    public void DismissTally()
    {
        if (_cachedTally == null || !_tallyWasActive) return;

        try
        {
            var button = _cachedTally.Button;
            if (button != null)
            {
                // Simulate submit on the SelectableBase
                var eventData = new UnityEngine.EventSystems.BaseEventData(EventSystem.current);
                UnityEngine.EventSystems.ExecuteEvents.Execute(
                    button.gameObject,
                    eventData,
                    UnityEngine.EventSystems.ExecuteEvents.submitHandler);
                _log?.LogInfo("TallyArena dismissed via submit");
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"TallyArena dismiss error: {ex}");
        }
    }

    // ── Post-Game Screens (Finale, Reward, Menu) ────────────────────

    /// <summary>Polls for the TallyArenaFinale (game over / claim reward) screen.</summary>
    private void PollForFinale()
    {
        try
        {
            if (_cachedFinale == null)
            {
                _cachedFinale = Object.FindObjectOfType<Spacewood.Unity.TallyArenaFinale>();
                if (_cachedFinale == null) return;
            }

            bool isActive = false;
            try
            {
                isActive = _cachedFinale.Canvas != null
                    && _cachedFinale.Canvas.enabled
                    && _cachedFinale.Canvas.gameObject.activeInHierarchy;
            }
            catch { }

            if (isActive && !_finaleWasActive)
            {
                _finaleWasActive = true;
                OnFinaleOpened();
            }
            else if (!isActive && _finaleWasActive)
            {
                _finaleWasActive = false;
            }
        }
        catch { }
    }

    /// <summary>Called when the TallyArenaFinale (game over / claim reward) screen appears.</summary>
    private void OnFinaleOpened()
    {
        _log?.LogInfo("TallyArenaFinale (game over) detected");

        string announcement = "Game over.";
        try
        {
            var finale = _cachedFinale!;
            int victories = finale.Victories;
            announcement = $"Game over. {victories} {(victories == 1 ? "victory" : "victories")}.";
        }
        catch { }

        // Check for claim button
        try
        {
            var claimBtn = _cachedFinale!.ClaimButton;
            if (claimBtn != null)
            {
                announcement += " Press Enter to claim reward.";
            }
            else
            {
                var selectableBtn = _cachedFinale!.Button;
                if (selectableBtn != null)
                    announcement += " Press Enter to continue.";
            }
        }
        catch
        {
            announcement += " Press Enter to continue.";
        }

        ScreenReader.Instance.Say(announcement);
        _log?.LogInfo($"Finale: {announcement}");
    }

    /// <summary>Dismisses the TallyArenaFinale by clicking its button.</summary>
    public void DismissFinale()
    {
        if (_cachedFinale == null || !_finaleWasActive) return;

        try
        {
            // Try ClaimButton first (ButtonBase — use Click)
            var claimBtn = _cachedFinale.ClaimButton;
            if (claimBtn != null)
            {
                claimBtn.Click();
                _log?.LogInfo("TallyArenaFinale claimed via ClaimButton");
                return;
            }

            // Fallback: SelectableBase Button (use submit event)
            var button = _cachedFinale.Button;
            if (button != null)
            {
                var eventData = new UnityEngine.EventSystems.BaseEventData(EventSystem.current);
                UnityEngine.EventSystems.ExecuteEvents.Execute(
                    button.gameObject,
                    eventData,
                    UnityEngine.EventSystems.ExecuteEvents.submitHandler);
                _log?.LogInfo("TallyArenaFinale dismissed via submit");
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"TallyArenaFinale dismiss error: {ex}");
        }
    }

    /// <summary>Polls for the TallyArenaReward (unlockables) screen.</summary>
    private void PollForReward()
    {
        try
        {
            if (_cachedReward == null)
            {
                _cachedReward = Object.FindObjectOfType<Spacewood.Unity.TallyArenaReward>();
                if (_cachedReward == null) return;
            }

            bool isActive = false;
            try
            {
                isActive = _cachedReward.Canvas != null
                    && _cachedReward.Canvas.enabled
                    && _cachedReward.Canvas.gameObject.activeInHierarchy;
            }
            catch { }

            if (isActive && !_rewardWasActive)
            {
                _rewardWasActive = true;
                OnRewardOpened();
            }
            else if (!isActive && _rewardWasActive)
            {
                _rewardWasActive = false;
            }
        }
        catch { }
    }

    /// <summary>Called when the TallyArenaReward (unlockables) screen appears.</summary>
    private void OnRewardOpened()
    {
        _log?.LogInfo("TallyArenaReward (unlockables) detected");

        string announcement = "";
        try
        {
            // Read the reward label (e.g., "Unlocked")
            string? rewardLabel = null;
            try { rewardLabel = _cachedReward!.RewardLabel?.text; } catch { }

            // Read the product label (name of what was unlocked)
            string? productLabel = null;
            try { productLabel = _cachedReward!.ProductLabel?.text; } catch { }

            if (!string.IsNullOrWhiteSpace(rewardLabel) && !string.IsNullOrWhiteSpace(productLabel))
                announcement = $"{StripRichText(rewardLabel!)} {StripRichText(productLabel!)}";
            else if (!string.IsNullOrWhiteSpace(productLabel))
                announcement = $"Unlocked: {StripRichText(productLabel!)}";
            else if (!string.IsNullOrWhiteSpace(rewardLabel))
                announcement = StripRichText(rewardLabel!);
            else
                announcement = "Reward unlocked";
        }
        catch { announcement = "Reward unlocked"; }

        announcement += ". Press Enter to continue.";
        ScreenReader.Instance.Say(announcement);
        _log?.LogInfo($"Reward: {announcement}");
    }

    /// <summary>Dismisses the TallyArenaReward by clicking its button.</summary>
    public void DismissReward()
    {
        if (_cachedReward == null || !_rewardWasActive) return;

        try
        {
            var button = _cachedReward.Button;
            if (button != null)
            {
                var eventData = new UnityEngine.EventSystems.BaseEventData(EventSystem.current);
                UnityEngine.EventSystems.ExecuteEvents.Execute(
                    button.gameObject,
                    eventData,
                    UnityEngine.EventSystems.ExecuteEvents.submitHandler);
                _log?.LogInfo("TallyArenaReward dismissed via submit");
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"TallyArenaReward dismiss error: {ex}");
        }
    }

    /// <summary>Polls for the TallyArenaMenu (post-game menu: play again, return, difficulty).</summary>
    private void PollForTallyMenu()
    {
        try
        {
            if (_cachedTallyMenu == null)
            {
                _cachedTallyMenu = Object.FindObjectOfType<Spacewood.Unity.TallyArenaMenu>();
                if (_cachedTallyMenu == null) return;
            }

            bool isActive = false;
            try
            {
                isActive = _cachedTallyMenu.Canvas != null
                    && _cachedTallyMenu.Canvas.enabled
                    && _cachedTallyMenu.Canvas.gameObject.activeInHierarchy;
            }
            catch { }

            if (isActive && !_tallyMenuWasActive)
            {
                _tallyMenuWasActive = true;
                OnTallyMenuOpened();
            }
            else if (!isActive && _tallyMenuWasActive)
            {
                _tallyMenuWasActive = false;
            }
        }
        catch { }
    }

    /// <summary>Called when the TallyArenaMenu (post-game menu) appears.</summary>
    private void OnTallyMenuOpened()
    {
        _log?.LogInfo("TallyArenaMenu (post-game menu) detected");

        var groups = new List<FocusGroup>();
        var menuGroup = new FocusGroup("Post-Game");
        var elements = new List<FocusElement>();

        try
        {
            var menu = _cachedTallyMenu!;

            // Start New Game button
            AddTallyMenuButton(elements, menu.StartNewGameButton, "Start New Game");

            // Change Difficulty button
            AddTallyMenuButton(elements, menu.ChangeDifficulty, "Change Difficulty");

            // Watch Battle button
            AddTallyMenuButton(elements, menu.WatchBattleButton, "Watch Battle");

            // Spectate Match button
            AddTallyMenuButton(elements, menu.SpectateMatchButton, "Spectate Match");

            // Playback Opponent button
            AddTallyMenuButton(elements, menu.PlaybackOpponentButton, "Watch Opponent");

            // Return to Menu button
            AddTallyMenuButton(elements, menu.ReturnToMenuButton, "Return to Menu");
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"TallyArenaMenu scan error: {ex}");
        }

        for (int i = 0; i < elements.Count; i++)
        {
            elements[i].SlotIndex = i;
            menuGroup.Elements.Add(elements[i]);
        }

        if (menuGroup.Elements.Count > 0)
        {
            groups.Add(menuGroup);
            FocusManager.Instance?.SetGroups(groups);
        }

        _log?.LogInfo($"TallyArenaMenu: {menuGroup.Elements.Count} buttons");
    }

    private void AddTallyMenuButton(
        List<FocusElement> elements,
        Spacewood.Unity.UI.SelectableBase? button,
        string fallbackLabel)
    {
        if (button == null) return;
        try
        {
            if (!button.gameObject.activeInHierarchy) return;
        }
        catch { return; }

        // Try to read the button label
        string label = fallbackLabel;
        try
        {
            var btnBase = button as Spacewood.Unity.UI.ButtonBase;
            if (btnBase != null)
            {
                string? title = null;
                try { title = btnBase.GetTitle(); } catch { }
                if (!string.IsNullOrWhiteSpace(title))
                    label = title!;
                else
                {
                    try { title = btnBase.Label?.text; } catch { }
                    if (!string.IsNullOrWhiteSpace(title))
                        label = title!;
                }
            }
        }
        catch { }

        var capturedButton = button;
        var capturedLabel = label;
        elements.Add(new FocusElement(label)
        {
            Type = "button",
            Tag = capturedButton,
            OnActivate = () =>
            {
                try
                {
                    _log?.LogInfo($"Activating TallyMenu button: {capturedLabel}");
                    var eventData = new UnityEngine.EventSystems.BaseEventData(EventSystem.current);
                    UnityEngine.EventSystems.ExecuteEvents.Execute(
                        capturedButton.gameObject,
                        eventData,
                        UnityEngine.EventSystems.ExecuteEvents.submitHandler);
                }
                catch (System.Exception ex)
                {
                    _log?.LogError($"TallyMenu button error: {ex}");
                }
            }
        });
    }

    /// <summary>Handles Enter key during battle phase — dismisses active post-game screens.</summary>
    public void DismissPostGameScreen()
    {
        if (_tallyWasActive)
        {
            DismissTally();
            return;
        }
        if (_finaleWasActive)
        {
            DismissFinale();
            return;
        }
        if (_rewardWasActive)
        {
            DismissReward();
            return;
        }
        // TallyArenaMenu: handled by focus group activation (Enter on button)
    }

    // ── Dock (Team Naming) Polling ───────────────────────────────────

    /// <summary>Polls for the Dock (team naming) screen visibility.</summary>
    private void PollForDock()
    {
        try
        {
            if (_cachedDock == null)
            {
                _cachedDock = Object.FindObjectOfType<Spacewood.Unity.MonoBehaviours.Build.Dock>();
                if (_cachedDock == null) return;
            }

            bool isActive = false;
            try
            {
                // Dock is active when its Overlay canvas is enabled
                isActive = _cachedDock.Overlay != null
                    && _cachedDock.Overlay.enabled
                    && _cachedDock.Overlay.gameObject.activeInHierarchy;
            }
            catch { }

            // Fallback: check the Dock's own gameObject
            if (!isActive)
            {
                try { isActive = _cachedDock.gameObject.activeInHierarchy && _cachedDock.ConfirmButton != null; }
                catch { }
            }

            if (isActive && !_dockWasActive)
            {
                _dockWasActive = true;
                OnDockOpened();
            }
            else if (!isActive && _dockWasActive)
            {
                _dockWasActive = false;
                OnDockClosed();
            }
        }
        catch { }
    }

    /// <summary>Called when the Dock (team naming) screen becomes visible.</summary>
    private void OnDockOpened()
    {
        IsDialogOpen = true;
        _log?.LogInfo("Dock (team naming) screen detected");

        var dock = _cachedDock;
        if (dock == null) return;

        var groups = new List<FocusGroup>();

        // ── Adjectives group ──
        var adjGroup = new FocusGroup("Adjective");
        try
        {
            if (dock.AdjectiveContainer != null)
            {
                var boxes = dock.AdjectiveContainer.GetComponentsInChildren<Spacewood.Unity.MonoBehaviours.Build.DockBox>(false);
                if (boxes != null)
                {
                    foreach (var box in boxes)
                    {
                        if (box == null) continue;
                        string? text = null;
                        try { text = box.TextMesh?.text; } catch { }
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        var capturedBox = box;
                        var capturedDock = dock;
                        var element = new FocusElement(text!)
                        {
                            Type = "button",
                            Tag = capturedBox,
                            OnActivate = () =>
                            {
                                try
                                {
                                    capturedDock.FocusAdjective(capturedBox);
                                    string? currentName = null;
                                    try { currentName = capturedDock.NameTextMesh?.text; } catch { }
                                    if (!string.IsNullOrWhiteSpace(currentName))
                                        ScreenReader.Instance.Say($"Selected. Team name: {currentName}");
                                    else
                                        ScreenReader.Instance.Say($"Selected {text}.");
                                }
                                catch (System.Exception ex) { _log?.LogError($"Dock adjective error: {ex}"); }
                            }
                        };
                        adjGroup.Elements.Add(element);
                    }
                }
            }
        }
        catch (System.Exception ex) { _log?.LogError($"Dock adjective scan error: {ex}"); }

        if (adjGroup.Elements.Count > 0)
            groups.Add(adjGroup);

        // ── Nouns group ──
        var nounGroup = new FocusGroup("Noun");
        try
        {
            if (dock.NounContainer != null)
            {
                var boxes = dock.NounContainer.GetComponentsInChildren<Spacewood.Unity.MonoBehaviours.Build.DockBox>(false);
                if (boxes != null)
                {
                    foreach (var box in boxes)
                    {
                        if (box == null) continue;
                        string? text = null;
                        try { text = box.TextMesh?.text; } catch { }
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        var capturedBox = box;
                        var capturedDock = dock;
                        var element = new FocusElement(text!)
                        {
                            Type = "button",
                            Tag = capturedBox,
                            OnActivate = () =>
                            {
                                try
                                {
                                    capturedDock.FocusNoun(capturedBox);
                                    string? currentName = null;
                                    try { currentName = capturedDock.NameTextMesh?.text; } catch { }
                                    if (!string.IsNullOrWhiteSpace(currentName))
                                        ScreenReader.Instance.Say($"Selected. Team name: {currentName}");
                                    else
                                        ScreenReader.Instance.Say($"Selected {text}.");
                                }
                                catch (System.Exception ex) { _log?.LogError($"Dock noun error: {ex}"); }
                            }
                        };
                        nounGroup.Elements.Add(element);
                    }
                }
            }
        }
        catch (System.Exception ex) { _log?.LogError($"Dock noun scan error: {ex}"); }

        if (nounGroup.Elements.Count > 0)
            groups.Add(nounGroup);

        // ── Confirm button ──
        try
        {
            var confirmBtn = dock.ConfirmButton;
            if (confirmBtn != null)
            {
                var confirmGroup = new FocusGroup("Confirm");
                string label = "Confirm";
                try { label = confirmBtn.Label?.text ?? "Confirm"; } catch { }

                var capturedBtn = confirmBtn;
                confirmGroup.Elements.Add(new FocusElement(label)
                {
                    Type = "button",
                    Tag = capturedBtn,
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch (System.Exception ex) { _log?.LogError($"Dock confirm error: {ex}"); }
                    }
                });
                groups.Add(confirmGroup);
            }
        }
        catch { }

        // Announce the screen BEFORE setting focus groups (which announces the first element)
        string nameText = "";
        try { nameText = dock.NameTextMesh?.text ?? ""; } catch { }
        string announcement = "Name your team. Use Left and Right to browse adjectives and nouns. Tab to switch. Enter to select.";
        if (!string.IsNullOrWhiteSpace(nameText))
            announcement += $" Current name: {nameText}.";

        ScreenReader.Instance.Say(announcement);

        if (groups.Count > 0)
            FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"Dock scan: {adjGroup.Elements.Count} adjectives, {nounGroup.Elements.Count} nouns");
    }

    /// <summary>Called when the Dock (team naming) screen closes.</summary>
    private void OnDockClosed()
    {
        if (!IsDialogOpen) return;
        IsDialogOpen = false;

        var phase = GamePhaseTracker.Instance.CurrentPhase;
        if (phase == GamePhase.Shop && _cachedHangar != null)
        {
            // Initialize shop now that the naming screen is gone
            try
            {
                var board = _cachedHangar.BuildModel?.Board;
                if (board != null)
                {
                    ShopStateReader.Instance.ReadFromBoard(board);
                    TeamStateReader.Instance.ReadFromBoard(board);
                }
                ShopAnnouncer.Instance?.OnTurnStart();
                BuildShopFocusGroups(_cachedHangar);
            }
            catch (System.Exception ex)
            {
                _log?.LogError($"Deferred shop setup error: {ex}");
            }
        }
        else
        {
            FocusManager.Instance?.Clear();
        }

        _log?.LogInfo("Dock (team naming) closed");
    }

    // ── Chooser (Cursed Toys / Relic Selection) Polling ──────────────

    /// <summary>Polls for the Chooser (toy/relic selection) screen visibility.</summary>
    private void PollForChooser()
    {
        try
        {
            if (_cachedChooser == null)
            {
                if (_cachedHangar == null) return;
                try { _cachedChooser = _cachedHangar.Chooser; } catch { }
                if (_cachedChooser == null) return;
            }

            bool isActive = false;
            try
            {
                // Chooser is active when it has items and its Visible rect is active
                isActive = _cachedChooser.Visible != null
                    && _cachedChooser.Visible.gameObject.activeInHierarchy
                    && _cachedChooser.Items != null
                    && _cachedChooser.Items.Count > 0;
            }
            catch { }

            if (isActive && !_chooserWasActive)
            {
                _chooserWasActive = true;
                OnChooserOpened();
            }
            else if (!isActive && _chooserWasActive)
            {
                _chooserWasActive = false;
                OnChooserClosed();
            }
        }
        catch { }
    }

    /// <summary>Called when the Chooser (toy/relic selection) screen becomes visible.</summary>
    private void OnChooserOpened()
    {
        IsDialogOpen = true;
        _log?.LogInfo("Chooser (toy/relic selection) detected");

        var chooser = _cachedChooser;
        if (chooser == null) return;

        var groups = new List<FocusGroup>();
        var choicesGroup = new FocusGroup("Choices");

        try
        {
            var items = chooser.Items;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null) continue;

                    // Get name from AbilityCard or ChoiceOptionModel
                    string name = "Choice";
                    var rows = new List<string>();

                    try
                    {
                        var model = item.Model;
                        // Check Spell first — toys/relics are spells and may also have a
                        // placeholder Minion with bogus attack/health (e.g. 1000/1).
                        if (model?.Spell != null)
                        {
                            name = GetSpellName(model.Spell);
                            rows.Add(name);

                            string? spellAbility = null;
                            try { spellAbility = Spacewood.Unity.Extensions.SpellModelExtensions.GetAbilityLocalized(model.Spell); } catch { }
                            if (!string.IsNullOrWhiteSpace(spellAbility))
                                rows.Add(StripRichText(spellAbility!));
                        }
                        else if (model?.Minion != null)
                        {
                            name = GetMinionName(model.Minion);
                            rows.Add(name);

                            int atk = model.Minion.Attack?.Total ?? 0;
                            int hp = model.Minion.Health?.Total ?? 0;
                            rows.Add($"{atk} attack, {hp} health");

                            string? ability = null;
                            try { ability = Spacewood.Unity.Extensions.MinionModelExtensions.GetAbilityLocalized(model.Minion); } catch { }
                            if (!string.IsNullOrWhiteSpace(ability))
                                rows.Add(StripRichText(ability!));
                        }
                    }
                    catch { }

                    // Fallback: read from AbilityCard text fields
                    if (rows.Count == 0)
                    {
                        try
                        {
                            if (item.Ability != null)
                            {
                                string? cardName = item.Ability.TextName?.text;
                                if (!string.IsNullOrWhiteSpace(cardName))
                                {
                                    name = cardName!;
                                    rows.Add(name);
                                }
                                string? cardBody = item.Ability.TextBody?.text;
                                if (!string.IsNullOrWhiteSpace(cardBody))
                                    rows.Add(StripRichText(cardBody!));
                            }
                        }
                        catch { }
                    }

                    if (rows.Count == 0)
                        rows.Add(name);

                    var capturedItem = item;
                    var capturedName = name;
                    var element = new FocusElement(name, i)
                    {
                        Type = "button",
                        Tag = capturedItem,
                        InfoRows = rows,
                        OnActivate = () =>
                        {
                            try
                            {
                                _log?.LogInfo($"Chooser: selecting {capturedName}");
                                // Click the ChooserItem's button — the game's Chooser wires
                                // HandleChoice as the click handler during SetModel
                                capturedItem.Button.Click();
                            }
                            catch (System.Exception ex)
                            {
                                _log?.LogError($"Chooser selection error: {ex}");
                            }
                        }
                    };
                    choicesGroup.Elements.Add(element);
                }
            }
        }
        catch (System.Exception ex) { _log?.LogError($"Chooser items scan error: {ex}"); }

        if (choicesGroup.Elements.Count > 0)
            groups.Add(choicesGroup);

        // Skip button (if available)
        try
        {
            var skipBtn = chooser.SkipButton;
            if (skipBtn != null && skipBtn.gameObject.activeInHierarchy)
            {
                var skipGroup = new FocusGroup("Actions");
                var capturedSkipBtn = skipBtn;
                skipGroup.Elements.Add(new FocusElement("Skip")
                {
                    Type = "button",
                    Tag = capturedSkipBtn,
                    OnActivate = () =>
                    {
                        try { capturedSkipBtn.Click(); }
                        catch (System.Exception ex) { _log?.LogError($"Chooser skip error: {ex}"); }
                    }
                });
                groups.Add(skipGroup);
            }
        }
        catch { }

        // Announce the chooser
        string label = "";
        try { label = chooser.Label?.text ?? ""; } catch { }
        string guide = "";
        try { guide = chooser.Guide?.text ?? ""; } catch { }
        string announcement = !string.IsNullOrWhiteSpace(label) ? label : "Choose an option.";
        if (!string.IsNullOrWhiteSpace(guide))
            announcement += $" {StripRichText(guide!)}";

        ScreenReader.Instance.Say(announcement);

        if (groups.Count > 0)
            FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"Chooser scan: {choicesGroup.Elements.Count} items");
    }

    /// <summary>Called when the Chooser closes.</summary>
    private void OnChooserClosed()
    {
        if (!IsDialogOpen) return;
        IsDialogOpen = false;

        var phase = GamePhaseTracker.Instance.CurrentPhase;
        if (phase == GamePhase.Shop && _cachedHangar != null)
        {
            try
            {
                var board = _cachedHangar.BuildModel?.Board;
                if (board != null)
                {
                    ShopStateReader.Instance.ReadFromBoard(board);
                    TeamStateReader.Instance.ReadFromBoard(board);
                }
                BuildShopFocusGroups(_cachedHangar, silent: true);
            }
            catch (System.Exception ex)
            {
                _log?.LogError($"Chooser close shop setup error: {ex}");
            }
        }
        else
        {
            FocusManager.Instance?.Clear();
        }

        _log?.LogInfo("Chooser closed");
    }

    // ── SubscriptionCart Polling ─────────────────────────────────────

    private void PollForSubscriptionCart()
    {
        try
        {
            if (_cachedSubCart == null)
            {
                _cachedSubCart = Object.FindObjectOfType<Spacewood.Unity.SubscriptionCart>();
                if (_cachedSubCart == null) return;
            }

            bool isOpen = false;
            try { isOpen = _cachedSubCart.IsOpen; } catch { }

            if (isOpen && !_subCartWasOpen)
            {
                _subCartWasOpen = true;
                OnSubscriptionCartOpened();
            }
            else if (!isOpen && _subCartWasOpen)
            {
                _subCartWasOpen = false;
                OnSubscriptionCartClosed();
            }
        }
        catch { }
    }

    private void OnSubscriptionCartOpened()
    {
        _restoreGroupName = FocusManager.Instance?.CurrentGroup?.Name;
        _restoreElementIndex = FocusManager.Instance?.CurrentElementIndex ?? 0;
        IsDialogOpen = true;
        ScanSubscriptionCart(announce: true);
    }

    /// <summary>Builds focus groups for the SubscriptionCart. Called on open and after Details/Back toggle.</summary>
    private void ScanSubscriptionCart(bool announce)
    {
        var cart = _cachedSubCart;
        if (cart == null) return;

        bool showingDetails = false;
        try { showingDetails = cart.ShowDetails; } catch { }

        string title = "";
        try { title = cart.TitleLabel?.text ?? ""; } catch { }
        string price = "";
        try { price = cart.PriceLabel?.text ?? ""; } catch { }
        string about = "";
        try { about = cart.AboutLabel?.text ?? ""; } catch { }

        var group = new FocusGroup("Subscription");

        if (showingDetails)
        {
            // Details view: read all TextMeshProUGUI children from the Details RectTransform
            var infoRows = new List<string>();
            try
            {
                var detailsRect = cart.Details;
                if (detailsRect != null)
                {
                    var texts = detailsRect.GetComponentsInChildren<TMPro.TextMeshProUGUI>(false);
                    if (texts != null)
                    {
                        for (int i = 0; i < texts.Count; i++)
                        {
                            try
                            {
                                string? t = texts[i]?.text;
                                if (!string.IsNullOrWhiteSpace(t))
                                    infoRows.Add(t!);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            if (infoRows.Count > 0)
            {
                group.Elements.Add(new FocusElement("Details")
                {
                    InfoRows = infoRows
                });
            }

            // Back button to return to front view
            try
            {
                var backBtn = cart.BackButton;
                if (backBtn != null)
                {
                    string label = "Back";
                    try { label = backBtn.Label?.text ?? "Back"; } catch { }
                    var captured = backBtn;
                    group.Elements.Add(new FocusElement(label)
                    {
                        Type = "button",
                        OnActivate = () =>
                        {
                            try { captured.Click(); } catch { }
                            ScanSubscriptionCart(announce: true);
                        }
                    });
                }
            }
            catch { }
        }
        else
        {
            // Front view: title, price, about
            var infoRows = new List<string>();
            if (!string.IsNullOrWhiteSpace(title))
                infoRows.Add(title);
            if (!string.IsNullOrWhiteSpace(price))
                infoRows.Add(price);
            if (!string.IsNullOrWhiteSpace(about))
                infoRows.Add(about);

            if (infoRows.Count > 0)
            {
                group.Elements.Add(new FocusElement(title ?? "Subscription")
                {
                    InfoRows = infoRows
                });
            }

            // Confirm button
            try
            {
                var confirmBtn = cart.ConfirmButton;
                if (confirmBtn != null)
                {
                    string label = "Subscribe";
                    try { label = confirmBtn.Label?.text ?? "Subscribe"; } catch { }
                    var captured = confirmBtn;
                    group.Elements.Add(new FocusElement(label)
                    {
                        Type = "button",
                        OnActivate = () => { try { captured.Click(); } catch { } }
                    });
                }
            }
            catch { }

            // Details button
            try
            {
                var detailsBtn = cart.DetailsButton;
                if (detailsBtn != null)
                {
                    bool active = false;
                    try { active = detailsBtn.gameObject.activeInHierarchy; } catch { }
                    if (active)
                    {
                        string label = "Details";
                        try { label = detailsBtn.Label?.text ?? "Details"; } catch { }
                        var captured = detailsBtn;
                        group.Elements.Add(new FocusElement(label)
                        {
                            Type = "button",
                            OnActivate = () =>
                            {
                                try { captured.Click(); } catch { }
                                ScanSubscriptionCart(announce: true);
                            }
                        });
                    }
                }
            }
            catch { }

            // Cancel button
            try
            {
                var cancelBtn = cart.CancelButton;
                if (cancelBtn != null)
                {
                    string label = "Cancel";
                    try { label = cancelBtn.Label?.text ?? "Cancel"; } catch { }
                    var captured = cancelBtn;
                    group.Elements.Add(new FocusElement(label)
                    {
                        Type = "button",
                        OnActivate = () => { try { captured.Click(); } catch { } }
                    });
                }
            }
            catch { }
        }

        var groups = new List<FocusGroup> { group };

        if (announce)
        {
            string announcement;
            if (showingDetails)
            {
                announcement = "Details";
                if (group.Elements.Count > 0 && group.Elements[0].InfoRows?.Count > 0)
                    announcement = group.Elements[0].InfoRows![0];
            }
            else
            {
                announcement = "Subscription";
                if (!string.IsNullOrWhiteSpace(title))
                    announcement = title;
                if (!string.IsNullOrWhiteSpace(price))
                    announcement += $", {price}";
            }

            ScreenReader.Instance.Say(announcement);
            FocusManager.Instance?.SetGroups(groups);
        }
        else
        {
            FocusManager.Instance?.SetGroups(groups);
        }

        _log?.LogInfo($"SubscriptionCart scanned: showDetails={showingDetails}, {group.Elements.Count} elements");
    }

    private void OnSubscriptionCartClosed()
    {
        if (!IsDialogOpen) return;
        IsDialogOpen = false;
        _needsScan = true;
        _scanDelay = 0.3f;
        _log?.LogInfo("SubscriptionCart closed");
    }

    // ── DesyncAlert Polling ─────────────────────────────────────────

    /// <summary>Polls for the DesyncAlert (desync error popup with Reload/Menu/Abandon buttons).</summary>
    private void PollForDesyncAlert()
    {
        try
        {
            if (_cachedDesyncAlert == null)
            {
                try { _cachedDesyncAlert = Object.FindObjectOfType<Spacewood.Unity.DesyncAlert>(); } catch { }
                if (_cachedDesyncAlert == null) return;
            }

            bool isOpen = false;
            try
            {
                isOpen = _cachedDesyncAlert.IsActive;
            }
            catch { }

            if (isOpen && !_desyncAlertWasOpen)
            {
                _desyncAlertWasOpen = true;
                OnDesyncAlertOpened();
            }
            else if (!isOpen && _desyncAlertWasOpen)
            {
                _desyncAlertWasOpen = false;
                OnDesyncAlertClosed();
            }
        }
        catch { }
    }

    private void OnDesyncAlertOpened()
    {
        IsDialogOpen = true;
        _log?.LogInfo("DesyncAlert detected");

        var alert = _cachedDesyncAlert;
        if (alert == null) return;

        var groups = new List<FocusGroup>();
        var actionsGroup = new FocusGroup("Actions");

        // Reload button
        try
        {
            var reloadBtn = alert.ReloadButton;
            if (reloadBtn != null && reloadBtn.gameObject.activeInHierarchy)
            {
                var capturedBtn = reloadBtn;
                actionsGroup.Elements.Add(new FocusElement("Reload", 0)
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch (System.Exception ex) { _log?.LogError($"Reload error: {ex}"); }
                    }
                });
            }
        }
        catch { }

        // Menu button
        try
        {
            var menuBtn = alert.MenuButton;
            if (menuBtn != null && menuBtn.gameObject.activeInHierarchy)
            {
                var capturedBtn = menuBtn;
                actionsGroup.Elements.Add(new FocusElement("Return to menu", 1)
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch (System.Exception ex) { _log?.LogError($"Menu error: {ex}"); }
                    }
                });
            }
        }
        catch { }

        // Abandon button
        try
        {
            var abandonBtn = alert.AbandonButton;
            if (abandonBtn != null && abandonBtn.gameObject.activeInHierarchy)
            {
                var capturedBtn = abandonBtn;
                actionsGroup.Elements.Add(new FocusElement("Abandon match", 2)
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch (System.Exception ex) { _log?.LogError($"Abandon error: {ex}"); }
                    }
                });
            }
        }
        catch { }

        if (actionsGroup.Elements.Count > 0)
            groups.Add(actionsGroup);

        if (groups.Count > 0)
        {
            FocusManager.Instance?.SetGroups(groups);

            // Read error details if available
            string errorText = "Desync error.";
            try
            {
                var debugStr = alert.DebugString;
                if (!string.IsNullOrEmpty(debugStr))
                    errorText = $"Desync error. {debugStr}";
            }
            catch { }

            ScreenReader.Instance.Say(errorText);
        }
    }

    private void OnDesyncAlertClosed()
    {
        if (!IsDialogOpen) return;
        IsDialogOpen = false;
        FocusManager.Instance?.Clear();
        _cachedDesyncAlert = null;
        _log?.LogInfo("DesyncAlert closed");
    }

    // ── Editing Mode ─────────────────────────────────────────────────

    public void StartEditing(TMP_InputField inputField)
    {
        if (inputField == null) return;

        IsEditing = true;
        _activeInputField = inputField;
        _editStartFrame = Time.frameCount;
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

    public void StopEditing(bool announce = true)
    {
        if (!IsEditing) return;

        IsEditing = false;
        _pendingActivation = null;

        if (_activeInputField != null)
        {
            try
            {
                string text = _trackedText;
                _activeInputField.DeactivateInputField();
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

    // ── Tooltip / Description Extraction ────────────────────────────

    /// <summary>Extracts the visible description text from a VersusCreatorRow.
    /// Scans child TMP_Text elements, skipping button labels and input fields,
    /// and returns the longest remaining text (the description, not the title).</summary>
    private static string? ExtractRowDescription(Spacewood.Unity.VersusCreatorRow row)
    {
        try
        {
            var texts = row.GetComponentsInChildren<TMP_Text>(false);
            if (texts == null) return null;

            // Collect IDs of text elements that belong to buttons or inputs (skip these)
            var skipIds = new HashSet<int>();
            try
            {
                if (row.PrimaryButton != null)
                {
                    var btnTexts = row.PrimaryButton.GetComponentsInChildren<TMP_Text>(true);
                    if (btnTexts != null)
                        foreach (var bt in btnTexts)
                            if (bt != null) try { skipIds.Add(bt.gameObject.GetInstanceID()); } catch { }
                }
            }
            catch { }
            try
            {
                if (row.SecondaryButton != null)
                {
                    var btnTexts = row.SecondaryButton.GetComponentsInChildren<TMP_Text>(true);
                    if (btnTexts != null)
                        foreach (var bt in btnTexts)
                            if (bt != null) try { skipIds.Add(bt.gameObject.GetInstanceID()); } catch { }
                }
            }
            catch { }
            try
            {
                if (row.SecondaryInput != null)
                {
                    var inputTexts = row.SecondaryInput.GetComponentsInChildren<TMP_Text>(true);
                    if (inputTexts != null)
                        foreach (var it in inputTexts)
                            if (it != null) try { skipIds.Add(it.gameObject.GetInstanceID()); } catch { }
                }
            }
            catch { }

            // Collect all remaining text elements
            string? best = null;
            foreach (var t in texts)
            {
                if (t == null) continue;
                try { if (skipIds.Contains(t.gameObject.GetInstanceID())) continue; } catch { continue; }

                string? text = null;
                try { text = t.text; } catch { }
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Skip icon glyphs
                if (text!.Length <= 2 && text[0] > 127) continue;

                string cleaned = StripRichText(text);
                if (string.IsNullOrWhiteSpace(cleaned)) continue;

                // Keep the longest text — shorter ones are titles, longer ones are descriptions
                if (best == null || cleaned.Length > best.Length)
                    best = cleaned;
            }

            return best;
        }
        catch { }
        return null;
    }

    /// <summary>Extracts tooltip text from a ButtonBase using multiple strategies.
    /// Returns null if no tooltip is available.</summary>
    private static string? ExtractButtonTooltip(Spacewood.Unity.UI.ButtonBase button)
    {
        // 1. Try GetNote() — works when ButtonTooltip is attached and has text
        try
        {
            string? note = button.GetNote();
            if (!string.IsNullOrWhiteSpace(note))
                return StripRichText(note!);
        }
        catch { }

        // 2. Try the ButtonTooltip component directly
        try
        {
            var tooltip = button.Tooltip;
            if (tooltip != null && !button.TooltipDisabled)
            {
                // Try localized string first
                try
                {
                    var localized = tooltip.LocalizeTextMesh?.LocalizedString?.GetLocalizedString();
                    if (!string.IsNullOrWhiteSpace(localized))
                        return StripRichText(localized!);
                }
                catch { }

                // Fall back to raw text
                try
                {
                    string? raw = tooltip.TextMesh?.text;
                    if (!string.IsNullOrWhiteSpace(raw))
                        return StripRichText(raw!);
                }
                catch { }
            }
        }
        catch { }

        // 3. Try PickerNote (description shown in picker dialogs — ButtonBase field)
        try
        {
            var pickerNote = button.PickerNote?.GetLocalizedString();
            if (!string.IsNullOrWhiteSpace(pickerNote))
                return StripRichText(pickerNote!);
        }
        catch { }

        // 4. Try PickerNotePlaceholder (fallback non-localized)
        try
        {
            var placeholder = button.PickerNotePlaceholder;
            if (!string.IsNullOrWhiteSpace(placeholder))
                return StripRichText(placeholder!);
        }
        catch { }

        return null;
    }

    // ── Label Resolution ──────────────────────────────────────────────

    /// <summary>Gets a meaningful label for a ButtonBase, using context when the label is unhelpful.
    /// Returns null if no useful label can be determined (button should be skipped).</summary>
    private static string? ResolveButtonLabel(Spacewood.Unity.UI.ButtonBase button)
    {
        // 1. Check for social media Link components — these show icon font glyphs as labels
        try
        {
            if (button.GetComponent<Spacewood.Unity.LinkWebsite>() != null) return "Website";
            if (button.GetComponent<Spacewood.Unity.LinkDiscord>() != null) return "Discord";
            if (button.GetComponent<Spacewood.Unity.LinkTwitter>() != null) return "Twitter";
            if (button.GetComponent<Spacewood.Unity.LinkEmail>() != null) return "Email";

            // Also check parent for Link components (button might be a child)
            if (button.GetComponentInParent<Spacewood.Unity.LinkWebsite>() != null) return "Website";
            if (button.GetComponentInParent<Spacewood.Unity.LinkDiscord>() != null) return "Discord";
            if (button.GetComponentInParent<Spacewood.Unity.LinkTwitter>() != null) return "Twitter";
            if (button.GetComponentInParent<Spacewood.Unity.LinkEmail>() != null) return "Email";
        }
        catch { }

        // 2. Try ButtonBase.GetTitle()
        string? title = null;
        try { title = button.GetTitle(); } catch { }
        if (IsUsefulLabel(title)) return title!;

        // 3. Try ButtonBase.Label.text
        string? labelText = null;
        try { labelText = button.Label?.text; } catch { }
        if (IsUsefulLabel(labelText)) return labelText!;

        // 4. Check if button is inside a ProductView (PackShop items) — read product name
        try
        {
            var productView = button.GetComponentInParent<Spacewood.Unity.ProductView>();
            if (productView != null)
            {
                string? productName = null;
                try { productName = productView.Name?.text; } catch { }

                string goName = button.gameObject?.name ?? "Button";

                if (IsUsefulLabel(productName))
                {
                    // Include what action this button does
                    if (goName.Contains("Buy")) return $"{productName}, Buy";
                    if (goName.Contains("Showcase") || goName.Contains("Preview")) return $"{productName}, Preview";
                    return productName!;
                }
            }
        }
        catch { }

        // 5. Check for PackProduct band labels
        try
        {
            var packProduct = button.GetComponentInParent<Spacewood.Unity.PackProduct>();
            if (packProduct != null)
            {
                string? bandTop = null;
                try { bandTop = packProduct.BandLabelTop?.text; } catch { }
                string? bandBottom = null;
                try { bandBottom = packProduct.BandLabelBottom?.text; } catch { }

                string packName = IsUsefulLabel(bandBottom) ? bandBottom!
                    : IsUsefulLabel(bandTop) ? bandTop!
                    : null!;

                if (packName != null)
                {
                    string goName = button.gameObject?.name ?? "Button";
                    if (goName.Contains("Buy")) return $"{packName}, Buy";
                    if (goName.Contains("Showcase") || goName.Contains("Preview")) return $"{packName}, Preview";
                    return packName;
                }
            }
        }
        catch { }

        // 6. Check for ProductShopItemShared (pet/food showcase items in pack shop)
        try
        {
            var shared = button.GetComponentInParent<Spacewood.Unity.ProductShopItemShared>();
            if (shared != null)
            {
                string? itemName = null;
                try { itemName = shared.Name?.Text?.text; } catch { }
                if (IsUsefulLabel(itemName))
                {
                    string goName = button.gameObject?.name ?? "Button";
                    if (goName.Contains("Showcase") || goName.Contains("Preview")) return $"{itemName}, Preview";
                    if (goName.Contains("Buy")) return $"{itemName}, Buy";
                    return itemName!;
                }
            }
        }
        catch { }

        // 7. Look for a nearby TMP_Text sibling that might describe this button
        try
        {
            var parent = button.transform.parent;
            if (parent != null)
            {
                var texts = parent.GetComponentsInChildren<TMP_Text>(false);
                if (texts != null)
                {
                    foreach (var t in texts)
                    {
                        if (t == null) continue;
                        // Skip the button's own label
                        try { if (button.Label != null && t == (TMP_Text)button.Label) continue; } catch { }
                        string? sibText = null;
                        try { sibText = t.text; } catch { }
                        if (IsUsefulLabel(sibText)) return sibText!;
                    }
                }
            }
        }
        catch { }

        // 8. Fall back to cleaned gameObject name; return null if it's just "Button"
        string name = button.gameObject?.name ?? "Button";
        string cleaned = CleanGameObjectName(name);
        return cleaned == "Button" ? null : cleaned;
    }

    /// <summary>Checks if a label string is meaningful (not empty, not icon glyphs, not generic).</summary>
    private static bool IsUsefulLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // Icon font characters are typically single non-ASCII chars or known bad values
        if (text.Length <= 2 && text[0] > 127) return false;
        // Filter known noise labels
        if (text == "Swords" || text == "swords") return false;
        if (text == "New" || text == "NEW") return false;
        return true;
    }

    /// <summary>Cleans up a gameObject name into a readable label.</summary>
    private static string CleanGameObjectName(string name)
    {
        // "BuyButton" → "Buy", "ShowcaseButton" → "Showcase"
        if (name.EndsWith("Button"))
            name = name.Substring(0, name.Length - 6);
        if (name.EndsWith("Btn"))
            name = name.Substring(0, name.Length - 3);
        // Insert spaces before capitals: "PackShop" → "Pack Shop"
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                result.Append(' ');
            result.Append(name[i]);
        }
        string cleaned = result.ToString().Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Button" : cleaned;
    }

    // ── Page Scanning ─────────────────────────────────────────────────

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

        Spacewood.Unity.Page? page = _currentPage;
        if (page == null)
            page = _menu.PageManager?.CurrentPage;

        if (page == null)
        {
            _log?.LogDebug("No active page to scan");
            return;
        }

        // ── Special handling: PackShop in arena selection mode ──
        // When PlayBar is active, the player is selecting a pack before starting a run.
        // Use a dedicated scan that shows packs as selectable items instead of purchase buttons.
        try
        {
            var packShop = page.TryCast<Spacewood.Unity.PackShop>();
            if (packShop != null)
            {
                bool isArenaSelection = false;
                try { isArenaSelection = packShop.PlayBar != null && packShop.PlayBar.gameObject.activeSelf; } catch { }

                if (isArenaSelection)
                {
                    ScanPackShopArenaMode(packShop);
                    return;
                }
                else
                {
                    ScanPackShopStoreMode(packShop);
                    return;
                }
            }
        }
        catch { }

        // Detect ManageDonations page (gift shop)
        try
        {
            var donations = page.TryCast<Spacewood.Unity.ManageDonations>();
            if (donations != null)
            {
                ScanManageDonations(donations);
                return;
            }
        }
        catch { }

        // Detect VersusCreator page for special button handling
        Spacewood.Unity.VersusCreator? versusCreator = null;
        try { versusCreator = page.TryCast<Spacewood.Unity.VersusCreator>(); } catch { }

        // If VersusCreatorAdvanced is open, scan it separately
        if (versusCreator != null)
        {
            try
            {
                var adv = versusCreator.Advanced;
                if (adv != null && adv.gameObject != null && adv.gameObject.activeSelf)
                {
                    ScanVersusCreatorAdvanced(versusCreator, adv);
                    return;
                }
            }
            catch { }
        }

        // Detect VersusLobby page for live player updates
        try
        {
            var versusLobby = page.TryCast<Spacewood.Unity.VersusLobby>();
            if (versusLobby != null)
            {
                ScanVersusLobby(versusLobby);
                return;
            }
        }
        catch { }

        // Clear cached VersusLobby if we're on a different page
        _cachedVersusLobby = null;

        // Detect Customize page — has its own internal PageManager for sub-shops
        try
        {
            var customize = page.TryCast<Spacewood.Unity.Customize>();
            if (customize != null)
            {
                ScanCustomize(customize);
                return;
            }
        }
        catch { }

        // Detect PetCustomizer page
        try
        {
            var petCustomizer = page.TryCast<Spacewood.Unity.PetCustomizer>();
            if (petCustomizer != null)
            {
                ScanPetCustomizer(petCustomizer);
                return;
            }
        }
        catch { }

        // Detect Achievements page for InfoRows buffer
        try
        {
            var achievements = page.TryCast<Spacewood.Unity.Achievements>();
            if (achievements != null)
            {
                ScanAchievements(achievements);
                return;
            }
        }
        catch { }

        // Detect Replay page
        try
        {
            var replay = page.TryCast<Spacewood.Unity.Replay>();
            if (replay != null)
            {
                ScanReplay(replay);
                return;
            }
        }
        catch { }

        // Detect StatsSummary page
        try
        {
            var stats = page.TryCast<Spacewood.Unity.StatsSummary>();
            if (stats != null)
            {
                ScanStatsSummary(stats);
                return;
            }
        }
        catch { }

        // Detect Spectate page
        try
        {
            var spectate = page.TryCast<Spacewood.Unity.Spectate>();
            if (spectate != null)
            {
                ScanSpectate(spectate);
                return;
            }
        }
        catch { }

        var group = new FocusGroup(page.gameObject?.name ?? "Menu");
        var elements = new List<(float y, FocusElement element)>();

        // Track which GameObjects are part of interactive elements (to skip in text scan)
        var interactiveObjects = new HashSet<int>();

        // ── Input fields ──
        var inputFields = page.GetComponentsInChildren<TMP_InputField>(false);
        if (inputFields != null)
        {
            foreach (var field in inputFields)
            {
                if (field == null) continue;
                if (!field.interactable) continue;

                try { if (field.gameObject != null) interactiveObjects.Add(field.gameObject.GetInstanceID()); } catch { }

                string label;
                try
                {
                    var placeholder = field.placeholder as TMP_Text;
                    label = placeholder?.text ?? field.gameObject?.name ?? "Text field";
                }
                catch { label = field.gameObject?.name ?? "Text field"; }

                if (string.IsNullOrWhiteSpace(label))
                    label = field.gameObject?.name ?? "Text field";

                float yPos = 0f;
                try { yPos = field.transform.position.y; } catch { }

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

        // ── Buttons ──
        var buttons = page.GetComponentsInChildren<Spacewood.Unity.UI.ButtonBase>(false);
        if (buttons != null)
        {
            foreach (var button in buttons)
            {
                if (button == null) continue;
                try { if (!button.GetInteractable()) continue; } catch { continue; }
                try { interactiveObjects.Add(button.gameObject.GetInstanceID()); } catch { }

                string? label = ResolveButtonLabel(button);
                if (label == null) continue; // Skip buttons with no useful label

                // Enhance picker-enabled buttons with current selected value
                try
                {
                    if (button.PickerEnabled && button.PickerItems != null)
                    {
                        for (int pi = 0; pi < button.PickerItems.Count; pi++)
                        {
                            var item = button.PickerItems[pi];
                            if (item != null && item.Picked)
                            {
                                string pickedLabel = "";
                                try { pickedLabel = item.Label?.GetLocalizedString() ?? ""; } catch { }
                                if (string.IsNullOrEmpty(pickedLabel))
                                    try { pickedLabel = item.Value ?? ""; } catch { }
                                if (!string.IsNullOrEmpty(pickedLabel))
                                    label += $", {pickedLabel}";
                                break;
                            }
                        }
                    }
                }
                catch { }

                // On VersusCreator, give AdvancedToggle a descriptive label
                // (the game changes its text from "Toggle" to "Change rules" when toggled,
                // which duplicates the AdvancedGoto button label)
                if (versusCreator != null)
                {
                    try
                    {
                        if (button == versusCreator.AdvancedToggle)
                            label = "Advanced mode";
                    }
                    catch { }
                }

                // Improve generic "Toggle" labels using GameObject name
                if (label == "Toggle")
                {
                    string goName = button.gameObject?.name ?? "";
                    string cleaned = CleanGameObjectName(goName);
                    if (cleaned != "Button" && cleaned != "Toggle")
                        label = cleaned;
                    else
                        label = "Toggle option";
                }

                float yPos = 0f;
                try { yPos = button.transform.position.y; } catch { }

                var capturedButton = button;
                var capturedLabel = label;
                var element = new FocusElement(label)
                {
                    Type = "button",
                    Tag = capturedButton,
                    OnActivate = () =>
                    {
                        // Guard: check if button still exists (IL2CPP throws on destroyed objects)
                        try { _ = capturedButton.gameObject; }
                        catch
                        {
                            ScreenReader.Instance.Say("Button no longer available.");
                            return;
                        }

                        try
                        {
                            _log?.LogInfo($"Activating button: {capturedLabel}");
                            capturedButton.Click();
                            // Schedule rescan in case the click changed the UI layout
                            // (e.g. toggling advanced mode adds/removes buttons).
                            // But if the click navigated to a new page (OnPageChanged
                            // already set _needsScan), don't save/restore the old position.
                            if (!_needsScan)
                                RequestRescan();
                        }
                        catch (System.Exception ex)
                        {
                            _log?.LogError($"Button click error: {ex}");
                        }
                    }
                };

                // Add price and description from ProductView/ProductTemplate
                try
                {
                    var productView = capturedButton.GetComponentInParent<Spacewood.Unity.ProductView>();
                    if (productView != null)
                    {
                        var detailParts = new List<string>();

                        // Read price
                        string? price = null;
                        try { price = productView.Price?.text; } catch { }
                        if (!string.IsNullOrWhiteSpace(price))
                            detailParts.Add(price!);

                        // Read description from ProductTemplate
                        string? desc = null;
                        try { desc = productView.ProductTemplate?.Description; } catch { }
                        if (!string.IsNullOrWhiteSpace(desc))
                            detailParts.Add(desc!);

                        if (detailParts.Count > 0)
                            element.Detail = string.Join(". ", detailParts);
                    }
                }
                catch { }

                elements.Add((yPos, element));
            }
        }

        // ── Standalone text (Label components that aren't part of interactive elements) ──
        // Collect labels already used by buttons to avoid duplicates
        var usedLabels = new HashSet<string>();
        foreach (var (_, el) in elements)
            usedLabels.Add(el.Label);

        try
        {
            var labels = page.GetComponentsInChildren<Spacewood.Unity.UI.Label>(false);
            if (labels != null)
            {
                foreach (var lbl in labels)
                {
                    if (lbl == null) continue;
                    try { if (interactiveObjects.Contains(lbl.gameObject.GetInstanceID())) continue; } catch { }

                    // Skip labels that are children of interactive elements (buttons, inputs, product items)
                    bool isChildOfInteractive = false;
                    try
                    {
                        var t = lbl.transform;
                        while (t != null && t != page.transform)
                        {
                            try
                            {
                                if (t.gameObject != null && interactiveObjects.Contains(t.gameObject.GetInstanceID()))
                                {
                                    isChildOfInteractive = true;
                                    break;
                                }
                            }
                            catch { break; }
                            t = t.parent;
                        }
                    }
                    catch { }
                    if (isChildOfInteractive) continue;

                    string? text = null;
                    try { text = lbl.Text?.text; } catch { }
                    if (!IsUsefulLabel(text)) continue;

                    // Skip if this text is already used by a button label
                    if (usedLabels.Contains(text!)) continue;
                    usedLabels.Add(text!);

                    float yPos = 0f;
                    try { yPos = lbl.transform.position.y; } catch { }

                    var element = new FocusElement(text!)
                    {
                        Type = "text"
                    };
                    elements.Add((yPos, element));
                }
            }
        }
        catch { }

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
        if (!SetGroupsWithRestore(groups))
            FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"Menu scan: {group.Elements.Count} elements on {page.gameObject?.name}");
    }

    /// <summary>Maps PersistKey int values to human-readable setting names.</summary>
    private static readonly Dictionary<int, string> _persistKeyNames = new()
    {
        { 11, "Starting lives" },       // PrivateVersusStartLives
        { 13, "Life loss amount" },      // PrivateVersusLoseLives
        { 14, "Turn duration" },         // PrivateVersusTurnDuration
        { 30, "Open decks" },            // PrivateVersusOpenDecks
        { 53, "Turn duration" },         // PrivateVersusTurnDuration2
        { 69, "Life loss mode" },        // PrivateVersusLifeSystem
        { 71, "Hard mode" },             // PrivateVersusHardMode
        { 72, "Shop rewards" },          // PrivateVersusShopRewards
        { 74, "Lives" },                 // PrivateVersusLives
        { 76, "Loss cap" },              // PrivateVersusLossCap
        { 89, "Next opponent" },         // PrivateVersusNextOpponent
        { 96, "Wacky mode" },            // PrivateVersusWackyToy
    };

    /// <summary>Resolves a human-readable name for a VersusCreatorRow.</summary>
    private string ResolveRowName(Spacewood.Unity.VersusCreatorRow row, int index)
    {
        // 1. Try PersistKey mapping (most reliable)
        try
        {
            int keyValue = (int)row.PrimaryKey;
            if (_persistKeyNames.TryGetValue(keyValue, out var name))
                return name;
        }
        catch { }

        // 2. Try PrimaryButton.GetTitle() (may return localized name)
        try
        {
            string? title = row.PrimaryButton?.GetTitle();
            if (IsUsefulLabel(title) && title != "Toggle")
                return title!;
        }
        catch { }

        // 3. Try SecondaryButton label (filtered)
        try
        {
            var secBtn = row.SecondaryButton;
            if (secBtn != null)
            {
                string? secLabel = secBtn.GetTitle();
                if (IsUsefulLabel(secLabel) && secLabel != "Enter" && !secLabel!.StartsWith("Enter"))
                    return secLabel;
                secLabel = secBtn.Label?.text;
                if (IsUsefulLabel(secLabel) && secLabel != "Enter" && !secLabel!.StartsWith("Enter"))
                    return secLabel!;
            }
        }
        catch { }

        // 4. Fall back to "Rule N"
        return $"Rule {index + 1}";
    }

    /// <summary>Specialized scan for VersusLobby (private match lobby).
    /// Shows players, action buttons, and match info with periodic updates.</summary>
    private void ScanVersusLobby(Spacewood.Unity.VersusLobby lobby)
    {
        _cachedVersusLobby = lobby;
        _versusLobbyPollTimer = 1.5f;

        var groups = new List<FocusGroup>();

        // ── Players group ──
        var playersGroup = new FocusGroup("Players");
        int playerCount = 0;
        try
        {
            // Items is a private field, so scan the ItemContainer for VersusLobbyItem components
            var items = lobby.ItemContainer?.GetComponentsInChildren<Spacewood.Unity.VersusLobbyItem>(false);
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null) continue;

                    string playerName = "Player";
                    try { playerName = item.PlayerName?.text ?? "Player"; } catch { }

                    string rank = "";
                    try { rank = item.RankText?.text ?? ""; } catch { }

                    string label = playerName;
                    if (!string.IsNullOrWhiteSpace(rank))
                        label += $", rank {rank}";

                    // Check if kick button is available (host can kick other players)
                    bool canKick = false;
                    Spacewood.Unity.UI.ButtonBase? kickBtn = null;
                    try
                    {
                        kickBtn = item.KickButton;
                        canKick = kickBtn != null
                            && kickBtn.gameObject.activeInHierarchy
                            && kickBtn.GetInteractable();
                    }
                    catch { }

                    var capturedKickBtn = kickBtn;
                    var capturedPlayerName = playerName;
                    var element = new FocusElement(label, i)
                    {
                        Type = canKick ? "button" : "text",
                        OnActivate = canKick ? () =>
                        {
                            try
                            {
                                _log?.LogInfo($"Kicking player: {capturedPlayerName}");
                                capturedKickBtn!.Click();
                                ScreenReader.Instance.Say($"Kicked {capturedPlayerName}.");
                                RequestRescan();
                            }
                            catch (System.Exception ex)
                            {
                                _log?.LogError($"Kick error: {ex}");
                            }
                        } : null
                    };

                    if (canKick)
                        element.DynamicDetail = () => "Press Enter to kick";

                    playersGroup.Elements.Add(element);
                    playerCount++;
                }
            }
        }
        catch (System.Exception ex) { _log?.LogError($"VersusLobby players scan error: {ex}"); }

        if (playersGroup.Elements.Count > 0)
            groups.Add(playersGroup);

        _versusLobbyPlayerCount = playerCount;

        // ── Actions group ──
        var actionsGroup = new FocusGroup("Actions");

        // Start button
        try
        {
            var startBtn = lobby.StartButton;
            if (startBtn != null && startBtn.gameObject.activeInHierarchy)
            {
                bool canStart = false;
                try { canStart = startBtn.GetInteractable(); } catch { }

                var capturedStartBtn = startBtn;
                string startLabel = canStart ? "Start match" : "Start match (waiting for players)";
                actionsGroup.Elements.Add(new FocusElement(startLabel)
                {
                    Type = "button",
                    Tag = capturedStartBtn,
                    OnActivate = canStart ? () =>
                    {
                        try
                        {
                            _log?.LogInfo("Starting match from lobby");
                            capturedStartBtn.Click();
                        }
                        catch (System.Exception ex)
                        {
                            _log?.LogError($"Start match error: {ex}");
                        }
                    } : null
                });
            }
        }
        catch { }

        // Rules button
        try
        {
            var rulesBtn = lobby.RulesButton;
            if (rulesBtn != null && rulesBtn.gameObject.activeInHierarchy)
            {
                var capturedRulesBtn = rulesBtn;
                actionsGroup.Elements.Add(new FocusElement("Rules")
                {
                    Type = "button",
                    Tag = capturedRulesBtn,
                    OnActivate = () =>
                    {
                        try { capturedRulesBtn.Click(); }
                        catch (System.Exception ex) { _log?.LogError($"Rules button error: {ex}"); }
                    }
                });
            }
        }
        catch { }

        // Leave button
        try
        {
            var leaveBtn = lobby.LeaveButton;
            if (leaveBtn != null && leaveBtn.gameObject.activeInHierarchy)
            {
                var capturedLeaveBtn = leaveBtn;
                actionsGroup.Elements.Add(new FocusElement("Leave")
                {
                    Type = "button",
                    Tag = capturedLeaveBtn,
                    OnActivate = () =>
                    {
                        try
                        {
                            _log?.LogInfo("Leaving lobby");
                            capturedLeaveBtn.Click();
                        }
                        catch (System.Exception ex)
                        {
                            _log?.LogError($"Leave button error: {ex}");
                        }
                    }
                });
            }
        }
        catch { }

        if (actionsGroup.Elements.Count > 0)
            groups.Add(actionsGroup);

        // ── Info group ──
        var infoGroup = new FocusGroup("Info");
        AddLobbyInfoPair(infoGroup, lobby.InfoPairGameName);
        AddLobbyInfoPair(infoGroup, lobby.InfoPairPlayerCount);
        AddLobbyInfoPair(infoGroup, lobby.InfoPairTurnDuration);
        AddLobbyInfoPair(infoGroup, lobby.InfoPairPackMode);
        AddLobbyInfoPair(infoGroup, lobby.InfoPairSpectator);
        AddLobbyInfoPair(infoGroup, lobby.InfoPairCustomRules);
        AddLobbyInfoPair(infoGroup, lobby.InfoPairAutoStart);

        if (infoGroup.Elements.Count > 0)
            groups.Add(infoGroup);

        // Announce lobby
        string announcement = $"Lobby. {playerCount} player{(playerCount != 1 ? "s" : "")}.";
        ScreenReader.Instance.Say(announcement);

        if (groups.Count > 0)
            SetGroupsWithRestore(groups);

        _log?.LogInfo($"VersusLobby scan: {playerCount} players, {actionsGroup.Elements.Count} actions, {infoGroup.Elements.Count} info items");
    }

    private static void AddLobbyInfoPair(FocusGroup group, Spacewood.Unity.VersusLobbyPair? pair)
    {
        if (pair == null) return;
        try
        {
            if (pair.gameObject == null || !pair.gameObject.activeInHierarchy) return;
            string label = pair.Label?.text ?? "";
            string value = pair.Value?.text ?? "";
            if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(value)) return;

            string text = string.IsNullOrWhiteSpace(value) ? label : $"{label}: {value}";
            group.Elements.Add(new FocusElement(text) { Type = "text" });
        }
        catch { }
    }

    /// <summary>Specialized scan for VersusCreatorAdvanced (advanced rules screen).
    /// Shows each rule row with its name, toggle state, and value, plus wacky/reset buttons.</summary>
    private void ScanVersusCreatorAdvanced(
        Spacewood.Unity.VersusCreator versusCreator,
        Spacewood.Unity.VersusCreatorAdvanced advanced)
    {
        // If MinionPicker is open on top, scan it instead
        try
        {
            var minionPicker = versusCreator.MinionPicker;
            if (minionPicker != null && minionPicker.Modal != null
                && minionPicker.Modal.gameObject != null && minionPicker.Modal.gameObject.activeSelf)
            {
                ScanMinionPicker(minionPicker);
                return;
            }
        }
        catch { }

        var group = new FocusGroup("Advanced Rules");
        var elements = new List<(float y, FocusElement element)>();

        // ── Scan rule rows ──
        try
        {
            var rows = advanced.Rows;
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (row == null) continue;
                    try { if (row.gameObject == null || !row.gameObject.activeSelf) continue; } catch { continue; }

                    string rowName = ResolveRowName(row, i);

                    // Extract description: prefer button tooltip (GetNote()), fall back to row text
                    string? rowDescription = null;
                    try
                    {
                        if (row.PrimaryButton != null)
                            rowDescription = ExtractButtonTooltip(row.PrimaryButton);
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(rowDescription))
                        rowDescription = ExtractRowDescription(row);
                    _log?.LogDebug($"Advanced row {i}: PrimaryKey={(int)row.PrimaryKey}, name={rowName}, desc={rowDescription ?? "(none)"}");

                    // Primary button: toggle to enable/disable this rule override
                    try
                    {
                        var primary = row.PrimaryButton;
                        if (primary != null)
                        {
                            bool interactable = true;
                            try { interactable = primary.GetInteractable(); } catch { interactable = false; }
                            if (interactable)
                            {
                                // Read toggle state from persisted value
                                string state = "";
                                try
                                {
                                    bool isEnabled = Spacewood.Scripts.Utilities.Persist.Get<bool>(row.PrimaryKey);
                                    state = isEnabled ? ", enabled" : ", disabled";
                                }
                                catch
                                {
                                    // Some keys may not be bool; try int (0=disabled, nonzero=enabled)
                                    try
                                    {
                                        int val = Spacewood.Scripts.Utilities.Persist.Get<int>(row.PrimaryKey);
                                        state = val != 0 ? ", enabled" : ", disabled";
                                    }
                                    catch { }
                                }

                                string toggleLabel = $"{rowName}{state}";

                                float yPos = 0f;
                                try { yPos = primary.transform.position.y; } catch { }

                                var capturedPrimary = primary;
                                var capturedName = rowName;
                                var element = new FocusElement(toggleLabel)
                                {
                                    Type = "button",
                                    Tag = capturedPrimary,
                                    Tooltip = rowDescription,
                                    OnActivate = () =>
                                    {
                                        try { _ = capturedPrimary.gameObject; }
                                        catch { ScreenReader.Instance.Say("Button no longer available."); return; }
                                        try
                                        {
                                            _log?.LogInfo($"Toggling: {capturedName}");
                                            capturedPrimary.Click();
                                            RequestRescan();
                                        }
                                        catch (System.Exception ex)
                                        {
                                            _log?.LogError($"Advanced rule toggle error: {ex}");
                                        }
                                    }
                                };
                                elements.Add((yPos, element));
                            }
                        }
                    }
                    catch { }

                    // Secondary: value selector (picker button or input field)
                    try
                    {
                        bool secondaryEnabled = false;
                        try { secondaryEnabled = row.SecondaryEnabled; } catch { }

                        if (secondaryEnabled)
                        {
                            // Secondary picker button
                            var secondary = row.SecondaryButton;
                            if (secondary != null)
                            {
                                bool interactable = true;
                                try { interactable = secondary.GetInteractable(); } catch { interactable = false; }
                                if (interactable)
                                {
                                    string secLabel = rowName;

                                    // Read current picked value
                                    try
                                    {
                                        if (secondary.PickerEnabled && secondary.PickerItems != null)
                                        {
                                            for (int pi = 0; pi < secondary.PickerItems.Count; pi++)
                                            {
                                                var item = secondary.PickerItems[pi];
                                                if (item != null && item.Picked)
                                                {
                                                    string pickedLabel = "";
                                                    try { pickedLabel = item.Label?.GetLocalizedString() ?? ""; } catch { }
                                                    if (string.IsNullOrEmpty(pickedLabel))
                                                        try { pickedLabel = item.Value ?? ""; } catch { }
                                                    if (!string.IsNullOrEmpty(pickedLabel))
                                                        secLabel += $", {pickedLabel}";
                                                    break;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            // Non-picker button: read its current display text
                                            string? btnText = null;
                                            try { btnText = secondary.Label?.text; } catch { }
                                            if (IsUsefulLabel(btnText) && btnText != "Enter"
                                                && !btnText!.StartsWith("Enter") && btnText != rowName)
                                                secLabel += $", {btnText}";
                                        }
                                    }
                                    catch { }

                                    float yPos = 0f;
                                    try { yPos = secondary.transform.position.y; } catch { }

                                    var capturedSec = secondary;
                                    var capturedSecLabel = secLabel;
                                    var element = new FocusElement(secLabel)
                                    {
                                        Type = "button",
                                        Tag = capturedSec,
                                        Tooltip = rowDescription,
                                        OnActivate = () =>
                                        {
                                            try { _ = capturedSec.gameObject; }
                                            catch { ScreenReader.Instance.Say("Button no longer available."); return; }
                                            try
                                            {
                                                _log?.LogInfo($"Activating: {capturedSecLabel}");
                                                capturedSec.Click();
                                                RequestRescan();
                                            }
                                            catch (System.Exception ex)
                                            {
                                                _log?.LogError($"Advanced rule value error: {ex}");
                                            }
                                        }
                                    };
                                    elements.Add((yPos, element));
                                }
                            }

                            // Secondary input field
                            var secInput = row.SecondaryInput;
                            if (secInput != null)
                            {
                                TMP_InputField? tmpField = null;
                                try { tmpField = secInput.GetComponentInChildren<TMP_InputField>(false); } catch { }
                                if (tmpField != null && tmpField.interactable)
                                {
                                    string inputLabel = rowName;

                                    float yPos = 0f;
                                    try { yPos = tmpField.transform.position.y; } catch { }

                                    var capturedInput = tmpField;
                                    var capturedInputLabel = rowName;
                                    var element = new FocusElement(inputLabel)
                                    {
                                        Type = "editbox",
                                        Tag = capturedInput,
                                        DynamicDetail = () =>
                                        {
                                            try
                                            {
                                                string val = capturedInput.text;
                                                return string.IsNullOrEmpty(val) ? null : val;
                                            }
                                            catch { return null; }
                                        },
                                        OnActivate = () =>
                                        {
                                            _log?.LogInfo($"Editing: {capturedInputLabel}");
                                            StartEditing(capturedInput);
                                        }
                                    };
                                    elements.Add((yPos, element));
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Error scanning advanced rows: {ex}");
        }

        // ── Wacky buttons ──
        AddAdvancedButton(elements, advanced.WackyToyButton, "Wacky toy");
        AddAdvancedButton(elements, advanced.WackyParameterButton, "Wacky parameter");

        // ── Reset button ──
        AddAdvancedButton(elements, advanced.ResetButton, "Reset rules");

        if (elements.Count == 0)
        {
            _log?.LogDebug("No advanced rule elements found");
            return;
        }

        // Sort top-to-bottom
        elements.Sort((a, b) => b.y.CompareTo(a.y));

        for (int i = 0; i < elements.Count; i++)
        {
            elements[i].element.SlotIndex = i;
            group.Elements.Add(elements[i].element);
        }

        var groups = new List<FocusGroup> { group };
        // Only announce "Advanced rules" on first entry, not on every rescan
        bool isFirstEntry = true;
        try { isFirstEntry = FocusManager.Instance?.CurrentGroup?.Name != "Advanced Rules"; } catch { }

        // If returning from MinionPicker, use the saved parent position
        if (_minionPickerReturnGroup != null && _restoreGroupName == null)
        {
            _restoreGroupName = _minionPickerReturnGroup;
            _restoreElementIndex = _minionPickerReturnIndex;
            _minionPickerReturnGroup = null;
        }

        if (!SetGroupsWithRestore(groups))
            FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"Advanced rules scan: {group.Elements.Count} elements");
        if (isFirstEntry)
            ScreenReader.Instance.Say("Advanced rules.");
    }

    /// <summary>Helper: adds a ButtonBase to the advanced rules element list with fallback label.</summary>
    private void AddAdvancedButton(
        List<(float y, FocusElement element)> elements,
        Spacewood.Unity.UI.ButtonBase? button,
        string fallbackLabel)
    {
        if (button == null) return;
        try
        {
            bool interactable = true;
            try { interactable = button.GetInteractable(); } catch { }
            if (!interactable) return;

            string resolvedLabel = ResolveButtonLabel(button);
            string label;
            if (resolvedLabel == null || resolvedLabel == "Toggle"
                || resolvedLabel == "Enter" || resolvedLabel.StartsWith("Enter..."))
                label = fallbackLabel;
            else if (resolvedLabel == fallbackLabel)
                label = fallbackLabel;
            else
                label = $"{fallbackLabel}, {resolvedLabel}";

            float yPos = 0f;
            try { yPos = button.transform.position.y; } catch { }

            var capturedBtn = button;
            var capturedLabel = label;
            elements.Add((yPos, new FocusElement(label)
            {
                Type = "button",
                Tag = capturedBtn,
                OnActivate = () =>
                {
                    try { _ = capturedBtn.gameObject; }
                    catch { ScreenReader.Instance.Say("Button no longer available."); return; }
                    try
                    {
                        _log?.LogInfo($"Activating: {capturedLabel}");
                        capturedBtn.Click();
                        RequestRescan();
                    }
                    catch (System.Exception ex) { _log?.LogError($"Button click error: {ex}"); }
                }
            }));
        }
        catch { }
    }

    /// <summary>Scans the MinionPicker modal (wacky toy/parameter selection).
    /// Shows each minion as a toggleable item with selected state.</summary>
    private void ScanMinionPicker(Spacewood.Scripts.Codec.MinionPicker picker)
    {
        string modeName;
        try
        {
            modeName = picker.Mode == Spacewood.Scripts.Codec.MinionPickerMode.WackyToy
                ? "Wacky toy" : "Wacky parameter";
        }
        catch { modeName = "Wacky"; }

        var itemsGroup = new FocusGroup($"{modeName} picker");

        // ── Search field ──
        try
        {
            var searchField = picker.SearchField;
            if (searchField != null)
            {
                TMP_InputField? tmpField = null;
                try { tmpField = searchField.InputField; } catch { }
                if (tmpField != null)
                {
                    var capturedField = tmpField;
                    var element = new FocusElement("Search")
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
                            _log?.LogInfo("Editing MinionPicker search field");
                            StartEditing(capturedField);
                        }
                    };
                    element.SlotIndex = itemsGroup.Elements.Count;
                    itemsGroup.Elements.Add(element);
                }
            }
        }
        catch { }

        // ── Build selected set from Result list ──
        var selectedSet = new HashSet<int>();
        bool inverted = false;
        try { inverted = picker.Inverted; } catch { }
        try
        {
            var result = picker.Result;
            if (result != null)
            {
                for (int r = 0; r < result.Count; r++)
                {
                    try { selectedSet.Add((int)result[r]); } catch { }
                }
            }
        }
        catch { }

        // ── Minion items ──
        try
        {
            var items = picker.Items;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null) continue;
                    try { if (item.gameObject == null || !item.gameObject.activeSelf) continue; } catch { continue; }

                    // Get name from MinionEnum (readable: "Ant", "Beaver", etc.)
                    string? label = null;
                    try { label = item.MinionEnum.ToString(); } catch { }
                    if (string.IsNullOrEmpty(label) || label == ((int)item.MinionEnum).ToString())
                    {
                        // Fallback: try button label
                        try { label = ResolveButtonLabel(item.Button); } catch { }
                    }
                    if (string.IsNullOrEmpty(label))
                        label = $"Item {i + 1}";

                    // Selected state: Result contains selected enums normally,
                    // or excluded enums when Inverted is true
                    int enumVal = (int)item.MinionEnum;
                    bool inResult = selectedSet.Contains(enumVal);
                    bool isSelected = inverted ? !inResult : inResult;

                    string fullLabel = isSelected ? $"{label}, selected" : label!;

                    var capturedItem = item;
                    var capturedLabel = fullLabel;
                    var element = new FocusElement(fullLabel)
                    {
                        Type = "button",
                        Tag = capturedItem,
                        OnActivate = () =>
                        {
                            try
                            {
                                _log?.LogInfo($"Toggling minion: {capturedLabel}");
                                capturedItem.Button.Click();
                                RequestRescan();
                            }
                            catch (System.Exception ex)
                            {
                                _log?.LogError($"MinionPicker item click error: {ex}");
                            }
                        }
                    };
                    element.SlotIndex = itemsGroup.Elements.Count;
                    itemsGroup.Elements.Add(element);
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Error scanning MinionPicker items: {ex}");
        }

        // ── Action buttons ──
        var actionsGroup = new FocusGroup("Actions");
        AddMinionPickerButton(actionsGroup, picker.ShowButton, "Show selected");
        AddMinionPickerButton(actionsGroup, picker.ShowCurrentPackButton, "Show current pack");
        AddMinionPickerButton(actionsGroup, picker.ChangeSelection, "Change selection");

        var groups = new List<FocusGroup>();
        if (itemsGroup.Elements.Count > 0)
            groups.Add(itemsGroup);
        if (actionsGroup.Elements.Count > 0)
            groups.Add(actionsGroup);

        if (groups.Count == 0)
        {
            _log?.LogDebug("No MinionPicker elements found");
            return;
        }

        // Only announce on first entry, not on rescan after toggle/filter
        bool isFirstEntry = true;
        try
        {
            var curName = FocusManager.Instance?.CurrentGroup?.Name;
            isFirstEntry = curName != itemsGroup.Name && curName != "Actions";
        }
        catch { }

        // On first entry, save the parent (Advanced Rules) position for return
        if (isFirstEntry && _restoreGroupName != null)
        {
            _minionPickerReturnGroup = _restoreGroupName;
            _minionPickerReturnIndex = _restoreElementIndex;
        }

        if (!SetGroupsWithRestore(groups))
            FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"MinionPicker scan: {itemsGroup.Elements.Count} items");
        if (isFirstEntry)
            ScreenReader.Instance.Say($"{modeName} picker.");
    }

    /// <summary>Helper: adds a ButtonBase to the MinionPicker actions group.</summary>
    private void AddMinionPickerButton(FocusGroup group, Spacewood.Unity.UI.ButtonBase? button, string label)
    {
        if (button == null) return;
        try
        {
            bool interactable = true;
            try { interactable = button.GetInteractable(); } catch { }
            if (!interactable) return;
            try { if (button.gameObject == null || !button.gameObject.activeInHierarchy) return; } catch { return; }

            var capturedBtn = button;
            var capturedLabel = label;
            var element = new FocusElement(label)
            {
                Type = "button",
                Tag = capturedBtn,
                SlotIndex = group.Elements.Count,
                OnActivate = () =>
                {
                    try
                    {
                        _log?.LogInfo($"Activating: {capturedLabel}");
                        capturedBtn.Click();
                        RequestRescan();
                    }
                    catch (System.Exception ex)
                    {
                        _log?.LogError($"MinionPicker button error: {ex}");
                    }
                }
            };
            group.Elements.Add(element);
        }
        catch { }
    }

    /// <summary>Specialized scan for PackShop when in arena selection mode (PlayBar visible).
    /// Shows packs as selectable items with equipped state, plus Start/options buttons.</summary>
    private void ScanPackShopArenaMode(Spacewood.Unity.PackShop packShop)
    {
        var packsGroup = new FocusGroup("Packs");
        var optionsGroup = new FocusGroup("Options");

        // ── Scan available packs ──
        try
        {
            var products = packShop.Products;
            if (products != null)
            {
                var equipped = packShop.EquippedProduct;
                for (int i = 0; i < products.Count; i++)
                {
                    var product = products[i];
                    if (product == null) continue;

                    // Skip products that aren't visible
                    try { if (product.gameObject != null && !product.gameObject.activeInHierarchy) continue; } catch { continue; }

                    // Get pack name — prefer ProductTemplate.Name (the actual pack name)
                    // over band labels (which often show generic text like "New Pets")
                    string? name = null;
                    try { name = product.ProductTemplate?.Name; } catch { }
                    if (!IsUsefulLabel(name))
                    {
                        try { name = product.Name?.text; } catch { }
                    }
                    if (string.IsNullOrWhiteSpace(name))
                        name = $"Pack {i + 1}";

                    // Check owned/equipped state
                    bool isOwned = false;
                    try { isOwned = product.IsOwned; } catch { }
                    bool isEquipped = false;
                    try { isEquipped = (equipped != null && product == equipped); } catch { }

                    string label = name!;
                    if (isEquipped)
                        label += ", selected";
                    else if (!isOwned)
                        label += ", not owned";

                    var capturedProduct = product;
                    var capturedShop = packShop;
                    var element = new FocusElement(label, i)
                    {
                        Type = "button",
                        Tag = capturedProduct,
                        OnActivate = () =>
                        {
                            try
                            {
                                _log?.LogInfo($"Selecting pack: {name}");
                                // Click the product's main button to select/equip it
                                var btn = capturedProduct.Button;
                                if (btn != null)
                                    btn.Click();
                                // Rescan after selection to update equipped state
                                RequestRescan();
                            }
                            catch (System.Exception ex)
                            {
                                _log?.LogError($"Pack select error: {ex}");
                            }
                        }
                    };

                    // Add description from ProductTemplate
                    try
                    {
                        string? desc = capturedProduct.ProductTemplate?.Description;
                        if (!string.IsNullOrWhiteSpace(desc))
                            element.Detail = desc;
                    }
                    catch { }

                    packsGroup.Elements.Add(element);
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"PackShop arena scan error: {ex}");
        }

        // ── Options buttons (Start, Difficulty, etc.) ──
        int optIdx = 0;
        try
        {
            // Start/Continue button
            var continueBtn = packShop.ContinueButton;
            if (continueBtn != null)
            {
                try { if (!continueBtn.GetInteractable()) continueBtn = null; } catch { continueBtn = null; }
            }
            if (continueBtn != null)
            {
                string? contLabel = null;
                try { contLabel = continueBtn.Label?.text; } catch { }
                if (!IsUsefulLabel(contLabel)) contLabel = "Start";

                var capturedBtn = continueBtn;
                optionsGroup.Elements.Add(new FocusElement(contLabel!, optIdx++)
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try
                        {
                            _log?.LogInfo($"Activating: {contLabel}");
                            capturedBtn.Click();
                        }
                        catch (System.Exception ex) { _log?.LogError($"Continue click error: {ex}"); }
                    }
                });
            }

            // Difficulty button
            AddPackShopOption(optionsGroup, packShop.DifficultyButton, "Difficulty", ref optIdx);

            // Create custom pack button
            AddPackShopOption(optionsGroup, packShop.CreateCustomButton, "Create custom pack", ref optIdx);
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"PackShop options scan error: {ex}");
        }

        // ── Set focus groups ──
        var groups = new List<FocusGroup>();
        if (packsGroup.Elements.Count > 0) groups.Add(packsGroup);
        if (optionsGroup.Elements.Count > 0) groups.Add(optionsGroup);

        if (groups.Count == 0)
        {
            _log?.LogDebug("PackShop arena scan: no elements found, falling back to generic scan");
            return; // Will not reach here normally; fallback handled by caller
        }

        if (SetGroupsWithRestore(groups))
        {
            // Focus restored to pre-dialog group — no extra announcement needed
        }
        else
        {
            FocusManager.Instance?.SetGroups(groups);

            // Announce the equipped pack only on fresh scans (not after dialog close)
            string? equippedName = null;
            try
            {
                var eq = packShop.EquippedProduct;
                if (eq != null)
                {
                    try { equippedName = eq.ProductTemplate?.Name; } catch { }
                    if (!IsUsefulLabel(equippedName))
                    {
                        try { equippedName = eq.Name?.text; } catch { }
                    }
                }
            }
            catch { }

            string announcement = "Pack selection.";
            if (IsUsefulLabel(equippedName))
                announcement += $" Current pack: {equippedName}.";

            ScreenReader.Instance.Say(announcement);
        }

        _log?.LogInfo($"PackShop arena scan: {packsGroup.Elements.Count} packs, {optionsGroup.Elements.Count} options");
    }

    private void AddPackShopOption(FocusGroup group, Spacewood.Unity.UI.ButtonBase? button, string fallbackLabel, ref int index)
    {
        if (button == null) return;
        try { if (!button.GetInteractable()) return; } catch { return; }

        string? label = null;
        try { label = button.Label?.text; } catch { }
        if (!IsUsefulLabel(label))
        {
            try { label = button.GetTitle(); } catch { }
        }
        if (!IsUsefulLabel(label)) label = fallbackLabel;

        var capturedBtn = button;
        var capturedLabel = label;
        group.Elements.Add(new FocusElement(label!, index++)
        {
            Type = "button",
            OnActivate = () =>
            {
                try
                {
                    _log?.LogInfo($"Activating: {capturedLabel}");
                    capturedBtn.Click();
                }
                catch (System.Exception ex) { _log?.LogError($"Option click error: {ex}"); }
            }
        });
    }

    /// <summary>Specialized scan for PackShop in store/browse mode (accessed via Pets button).
    /// Shows each pack as a single entry with Preview action for owned packs,
    /// Buy for unowned. Filters out DeckViewer ghost buttons.</summary>
    private void ScanPackShopStoreMode(Spacewood.Unity.PackShop packShop)
    {
        var group = new FocusGroup("Packs");

        try
        {
            var products = packShop.Products;
            if (products != null)
            {
                for (int i = 0; i < products.Count; i++)
                {
                    var product = products[i];
                    if (product == null) continue;
                    try { if (product.gameObject != null && !product.gameObject.activeInHierarchy) continue; } catch { continue; }

                    // Get pack name from ProductTemplate
                    string? name = null;
                    try { name = product.ProductTemplate?.Name; } catch { }
                    if (!IsUsefulLabel(name))
                    {
                        try { name = product.Name?.text; } catch { }
                    }
                    if (string.IsNullOrWhiteSpace(name))
                        name = $"Pack {i + 1}";

                    bool isOwned = false;
                    try { isOwned = product.IsOwned; } catch { }

                    // Build label: "Pack name, Preview" or "Pack name, Buy, $X.XX"
                    string label = name!;
                    if (isOwned)
                    {
                        label += ", Preview";
                    }
                    else
                    {
                        string? price = null;
                        try { price = product.Price?.text; } catch { }
                        if (IsUsefulLabel(price))
                            label += $", {price}";
                        else
                            label += ", Buy";
                    }

                    var capturedProduct = product;
                    var capturedName = name;
                    var capturedOwned = isOwned;
                    var element = new FocusElement(label, i)
                    {
                        Type = "button",
                        Tag = capturedProduct,
                        OnActivate = () =>
                        {
                            try
                            {
                                if (capturedOwned)
                                {
                                    // Open preview — click the PreviewButton if available, else main Button
                                    _log?.LogInfo($"Previewing pack: {capturedName}");
                                    var previewBtn = capturedProduct.PreviewButton;
                                    if (previewBtn != null)
                                        previewBtn.Click();
                                    else
                                        capturedProduct.Button?.Click();
                                }
                                else
                                {
                                    _log?.LogInfo($"Buying pack: {capturedName}");
                                    capturedProduct.Button?.Click();
                                }
                            }
                            catch (System.Exception ex)
                            {
                                _log?.LogError($"Pack action error: {ex}");
                            }
                        }
                    };

                    // Add description
                    try
                    {
                        string? desc = capturedProduct.ProductTemplate?.Description;
                        if (!string.IsNullOrWhiteSpace(desc))
                            element.Detail = desc;
                    }
                    catch { }

                    group.Elements.Add(element);
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"PackShop store scan error: {ex}");
        }

        // Add the Create Custom Pack button if present
        try
        {
            var createBtn = packShop.CreateCustomButton;
            if (createBtn != null)
            {
                try { if (!createBtn.GetInteractable()) createBtn = null; } catch { createBtn = null; }
            }
            if (createBtn != null)
            {
                string? createLabel = null;
                try { createLabel = createBtn.Label?.text; } catch { }
                if (!IsUsefulLabel(createLabel)) createLabel = "Create custom pack";

                var capturedBtn = createBtn;
                group.Elements.Add(new FocusElement(createLabel!, group.Elements.Count)
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch (System.Exception ex) { _log?.LogError($"Create pack click error: {ex}"); }
                    }
                });
            }
        }
        catch { }

        // Add the Manage Donations / Gift button if present
        try
        {
            var donateBtn = packShop.ManageDonationsButton;
            if (donateBtn != null)
            {
                try { if (!donateBtn.gameObject.activeInHierarchy) donateBtn = null; } catch { donateBtn = null; }
            }
            if (donateBtn != null)
            {
                string? donateLabel = null;
                try { donateLabel = donateBtn.Label?.text; } catch { }
                if (!IsUsefulLabel(donateLabel)) donateLabel = "Manage gifts";

                var capturedBtn = donateBtn;
                group.Elements.Add(new FocusElement(donateLabel!, group.Elements.Count)
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch (System.Exception ex) { _log?.LogError($"Donations button click error: {ex}"); }
                    }
                });
            }
        }
        catch { }

        if (group.Elements.Count == 0)
        {
            _log?.LogDebug("PackShop store scan: no elements found");
            return;
        }

        var groups = new List<FocusGroup> { group };
        if (!SetGroupsWithRestore(groups))
        {
            FocusManager.Instance?.SetGroups(groups);
            ScreenReader.Instance.Say("Pets.");
        }
        _log?.LogInfo($"PackShop store scan: {group.Elements.Count} packs");
    }

    // ── DeckViewer (pack preview) polling ─────────────────────────────

    private void PollForDeckViewer()
    {
        try
        {
            if (_cachedDeckViewer == null)
            {
                _cachedDeckViewer = Object.FindObjectOfType<Spacewood.Unity.MonoBehaviours.Build.DeckViewer>();
                if (_cachedDeckViewer == null) return;
            }

            bool isActive = false;
            try
            {
                isActive = _cachedDeckViewer.gameObject != null
                    && _cachedDeckViewer.gameObject.activeInHierarchy;
            }
            catch { _cachedDeckViewer = null; return; }

            if (isActive && !_deckViewerWasActive)
            {
                _deckViewerWasActive = true;
                OnDeckViewerOpened();
            }
            else if (!isActive && _deckViewerWasActive)
            {
                _deckViewerWasActive = false;
                OnDeckViewerClosed();
            }
        }
        catch { }
    }

    private void OnDeckViewerOpened()
    {
        IsDialogOpen = true;
        _log?.LogInfo("DeckViewer (pack preview) opened");

        var dv = _cachedDeckViewer!;
        var group = new FocusGroup("Preview");

        // Read tier rows and their items
        try
        {
            var rows = dv.ItemRows;
            if (rows != null)
            {
                int idx = 0;
                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    if (row == null) continue;
                    try { if (!row.gameObject.activeInHierarchy) continue; } catch { continue; }

                    // Check if this tier is concealed (locked)
                    bool concealed = false;
                    try { concealed = row.Conceal != null && row.Conceal.gameObject.activeSelf; } catch { }

                    // Tier/Turn label
                    string? tierLabel = null;
                    try { tierLabel = row.Text?.text; } catch { }
                    if (!IsUsefulLabel(tierLabel)) tierLabel = $"Tier {r + 1}";

                    if (concealed)
                    {
                        group.Elements.Add(new FocusElement($"{tierLabel}, locked", idx++) { Type = "text" });
                        continue;
                    }

                    // Scan pets in this row
                    try
                    {
                        var petContainer = row.PetContainer;
                        if (petContainer != null)
                            ScanDeckViewerContainer(petContainer, tierLabel!, "Pet", group, ref idx);
                    }
                    catch { }

                    // Scan food in this row
                    try
                    {
                        var foodContainer = row.FoodContainer;
                        if (foodContainer != null)
                            ScanDeckViewerContainer(foodContainer, tierLabel!, "Food", group, ref idx);
                    }
                    catch { }
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"DeckViewer row scan error: {ex}");
        }

        // Add Close button
        try
        {
            var closeBtn = dv.CloseButton;
            if (closeBtn != null)
            {
                var capturedBtn = closeBtn;
                group.Elements.Add(new FocusElement("Close", group.Elements.Count)
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch (System.Exception ex) { _log?.LogError($"Close preview error: {ex}"); }
                    }
                });
            }
        }
        catch { }

        if (group.Elements.Count == 0)
        {
            _log?.LogDebug("DeckViewer scan: no items found");
            return;
        }

        FocusManager.Instance?.SetGroups(new List<FocusGroup> { group });
        _log?.LogInfo($"DeckViewer scan: {group.Elements.Count} items");
    }

    private void OnDeckViewerClosed()
    {
        _log?.LogInfo("DeckViewer closed");
        IsDialogOpen = false;

        // Rescan the underlying page
        RequestRescan();
    }

    /// <summary>Try to resolve the name of a DeckViewerItem from its button tooltip,
    /// button label, or icon sprite name.</summary>
    /// <summary>Scans DeckViewerItems in a container (pet or food), filtering out
    /// non-interactable items (achievement/perk placeholders).</summary>
    private void ScanDeckViewerContainer(
        RectTransform container, string tierLabel, string category,
        FocusGroup group, ref int idx)
    {
        var items = container.GetComponentsInChildren<Spacewood.Unity.MonoBehaviours.Build.DeckViewerItem>(false);
        if (items == null) return;

        foreach (var item in items)
        {
            if (item == null) continue;
            try { if (!item.gameObject.activeInHierarchy) continue; } catch { continue; }

            // Skip items with no interactable button
            bool hasButton = false;
            try { hasButton = item.Button != null && item.Button.GetInteractable(); } catch { }
            if (!hasButton) continue;

            // Skip prefab template items — real items are instantiated as "(Clone)",
            // while the 3 prefab originals have names like "DeckViewerItem", "DeckViewerItem (1)", etc.
            try
            {
                string? goName = item.gameObject?.name;
                if (goName != null && !goName.Contains("(Clone)")) continue;
            }
            catch { continue; }

            string? itemName = ResolveViewerItemName(item, category);
            string name = itemName ?? category;

            // Build info rows inline (avoid instance method with List<string> return type —
            // IL2CPP interop doesn't support complex return types on MonoBehaviour methods)
            var rows = new List<string>();
            rows.Add($"{tierLabel}: {name}"); // Row 0: tier + name

            if (category == "Pet")
            {
                var template = TryGetMinionTemplate(item, name);
                if (template != null)
                {
                    try { rows.Add($"{template.Attack} attack, {template.Health} health"); } catch { }
                    try
                    {
                        var abilities = template.Abilities;
                        if (abilities != null && abilities.Count > 0)
                        {
                            var abilityTemplate = abilities[0].Template;
                            if (abilityTemplate != null)
                            {
                                string? about = abilityTemplate.About;
                                if (!string.IsNullOrWhiteSpace(about))
                                    rows.Add(StripRichText(about!));
                            }
                        }
                    }
                    catch { }
                }
            }
            else // Food/Spell
            {
                var spell = TryGetSpellTemplate(item, name);
                if (spell != null)
                {
                    string? abilityText = null;
                    try
                    {
                        var model = spell.Model;
                        if (model != null)
                            abilityText = Spacewood.Unity.Extensions.SpellModelExtensions.GetAbilityLocalized(model);
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(abilityText))
                    {
                        try { abilityText = spell.About; } catch { }
                    }
                    if (!string.IsNullOrWhiteSpace(abilityText))
                        rows.Add(StripRichText(abilityText!));

                    try
                    {
                        string? finePrint = spell.FinePrint;
                        if (!string.IsNullOrWhiteSpace(finePrint))
                            rows.Add(StripRichText(finePrint!));
                    }
                    catch { }

                    // If the spell grants a perk, describe the perk effect.
                    // We can't use spell.Perk directly (IL2CPP Nullable<enum> marshalling is broken,
                    // always returns value 0). Instead, try matching the SpellEnum name to a Perk enum name
                    // — they match for perk-granting foods (Honey→Honey, Garlic→Garlic, etc.).
                    try
                    {
                        string spellEnumName = spell.Enum.ToString();
                        if (System.Enum.TryParse<Spacewood.Core.Enums.Perk>(spellEnumName, out var perkEnum))
                        {
                            var perkTemplate = Spacewood.Core.Enums.PerkConstants.GetPerk(perkEnum);
                            if (perkTemplate != null)
                            {
                                string? perkName = perkTemplate.Name;
                                string? perkAbout = perkTemplate.GetAbout();
                                if (!string.IsNullOrWhiteSpace(perkName) && !string.IsNullOrWhiteSpace(perkAbout))
                                    rows.Add($"Perk: {StripRichText(perkName!)}. {StripRichText(perkAbout!)}");
                                else if (!string.IsNullOrWhiteSpace(perkName))
                                    rows.Add($"Perk: {StripRichText(perkName!)}");
                            }
                        }
                    }
                    catch { }
                }
            }

            group.Elements.Add(new FocusElement(name, idx++)
            {
                Type = "text",
                Tag = item,
                InfoRows = rows
            });
        }
    }

    /// <summary>Attempts to resolve a DeckViewerItem to a MinionTemplate by parsing
    /// the sprite name as a MinionEnum and looking up via MinionConstants.</summary>
    private static Spacewood.Core.Enums.MinionTemplate? TryGetMinionTemplate(
        Spacewood.Unity.MonoBehaviours.Build.DeckViewerItem item, string resolvedName)
    {
        // Try parsing sprite name as MinionEnum (sprite names like "Duck_2x" → "Duck" = MinionEnum.Duck)
        try
        {
            string? spriteName = item.Icon?.sprite?.name;
            if (!string.IsNullOrWhiteSpace(spriteName))
            {
                string clean = spriteName!;
                if (clean.EndsWith("_2x") || clean.EndsWith("_1x"))
                    clean = clean.Substring(0, clean.Length - 3);
                if (clean.StartsWith("pet-")) clean = clean.Substring(4);

                if (System.Enum.TryParse<Spacewood.Core.Enums.MinionEnum>(clean, true, out var minionEnum))
                {
                    var template = Spacewood.Core.Enums.MinionConstants.TryGetMinion(minionEnum);
                    if (template != null) return template;
                }
            }
        }
        catch { }

        // Fallback: try resolved name as enum
        try
        {
            if (System.Enum.TryParse<Spacewood.Core.Enums.MinionEnum>(resolvedName, true, out var minionEnum))
            {
                var template = Spacewood.Core.Enums.MinionConstants.TryGetMinion(minionEnum);
                if (template != null) return template;
            }
        }
        catch { }

        return null;
    }

    /// <summary>Attempts to resolve a DeckViewerItem to a Spell template by parsing
    /// the sprite name as a SpellEnum and looking up via SpellConstants.</summary>
    private static Spacewood.Core.Models.Spells.Spell? TryGetSpellTemplate(
        Spacewood.Unity.MonoBehaviours.Build.DeckViewerItem item, string resolvedName)
    {
        // Try parsing sprite name as SpellEnum
        try
        {
            string? spriteName = item.Icon?.sprite?.name;
            if (!string.IsNullOrWhiteSpace(spriteName))
            {
                string clean = spriteName!;
                if (clean.EndsWith("_2x") || clean.EndsWith("_1x"))
                    clean = clean.Substring(0, clean.Length - 3);
                if (clean.StartsWith("spell-")) clean = clean.Substring(6);
                if (clean.StartsWith("food-")) clean = clean.Substring(5);
                if (clean.StartsWith("perk-")) clean = clean.Substring(5);

                if (System.Enum.TryParse<Spacewood.Core.Enums.SpellEnum>(clean, true, out var spellEnum))
                {
                    var template = Spacewood.Core.Enums.SpellConstants.GetSpell(spellEnum);
                    if (template != null) return template;
                }
            }
        }
        catch { }

        // Fallback: try resolved name as enum
        try
        {
            if (System.Enum.TryParse<Spacewood.Core.Enums.SpellEnum>(resolvedName, true, out var spellEnum))
            {
                var template = Spacewood.Core.Enums.SpellConstants.GetSpell(spellEnum);
                if (template != null) return template;
            }
        }
        catch { }

        return null;
    }

    private string ResolveViewerItemName(Spacewood.Unity.MonoBehaviours.Build.DeckViewerItem item, string fallback)
    {
        // Try button tooltip text
        try
        {
            var tooltip = item.Button?.Tooltip;
            if (tooltip != null)
            {
                string? text = tooltip.TextMesh?.text;
                if (IsUsefulLabel(text))
                    return StripRichText(text!);
            }
        }
        catch { }

        // Try button label text
        try
        {
            string? label = item.Button?.Label?.text;
            if (IsUsefulLabel(label))
                return StripRichText(label!);
        }
        catch { }

        // Try button GetTitle
        try
        {
            string? title = item.Button?.GetTitle();
            if (IsUsefulLabel(title))
                return StripRichText(title!);
        }
        catch { }

        // Try icon sprite name (often contains pet/spell enum name)
        try
        {
            string? spriteName = item.Icon?.sprite?.name;
            if (IsUsefulLabel(spriteName))
            {
                string clean = spriteName!;
                // Strip resolution suffixes like _2x, _1x
                if (clean.EndsWith("_2x") || clean.EndsWith("_1x"))
                    clean = clean.Substring(0, clean.Length - 3);
                // Clean up common prefixes
                if (clean.StartsWith("pet-")) clean = clean.Substring(4);
                if (clean.StartsWith("spell-")) clean = clean.Substring(6);
                if (clean.StartsWith("food-")) clean = clean.Substring(5);
                if (clean.StartsWith("perk-")) clean = clean.Substring(5);
                // Convert underscores to spaces and capitalize
                clean = clean.Replace('_', ' ');
                if (clean.Length > 0)
                    clean = char.ToUpper(clean[0]) + clean.Substring(1);
                return clean;
            }
        }
        catch { }

        // Try GameObject name
        try
        {
            string? goName = item.gameObject?.name;
            if (IsUsefulLabel(goName) && goName != "DeckViewerItem")
                return goName!;
        }
        catch { }

        return fallback;
    }


    /// <summary>Closes the currently open dialog (DeckViewer, Alert, Picker).</summary>
    public void DismissCurrentDialog()
    {
        // Chooser (toy/relic selection) — cannot be dismissed, selection is mandatory
        if (_chooserWasActive)
        {
            _log?.LogInfo("Chooser is active — Escape blocked (selection is mandatory)");
            return;
        }

        // DeckViewer (pack preview)
        try
        {
            if (_cachedDeckViewer != null && _deckViewerWasActive)
            {
                var closeBtn = _cachedDeckViewer.CloseButton;
                if (closeBtn != null)
                {
                    closeBtn.Click();
                    _log?.LogInfo("DeckViewer dismissed via Escape");
                    return;
                }
            }
        }
        catch { }

        // IconAlert (tier rank-up) — confirm to dismiss
        try
        {
            if (_cachedIconAlert != null && _iconAlertWasOpen)
            {
                DismissIconAlert();
                return;
            }
        }
        catch { }

        // DonationBuyer — close the modal
        try
        {
            if (_cachedDonationBuyer != null && _donationBuyerWasOpen)
            {
                // In receiver phase, click cancel to go back to decision
                if (_donationBuyerPhase == "receiver")
                {
                    var cancelBtn = _cachedDonationBuyer.Receiver?.CancelButton;
                    if (cancelBtn != null)
                    {
                        cancelBtn.Click();
                        _log?.LogInfo("DonationBuyer receiver cancelled via Escape");
                        return;
                    }
                }
                // In decision phase, click cancel to close
                var decisionCancel = _cachedDonationBuyer.Decision?.CancelButton;
                if (decisionCancel != null)
                {
                    decisionCancel.Click();
                    _log?.LogInfo("DonationBuyer decision cancelled via Escape");
                    return;
                }
                // Fallback: close the modal directly
                _cachedDonationBuyer.Close();
                _log?.LogInfo("DonationBuyer closed via Escape (fallback)");
                return;
            }
        }
        catch { }

        // SubscriptionCart — click Cancel button
        try
        {
            if (_cachedSubCart != null && _subCartWasOpen)
            {
                var cancelBtn = _cachedSubCart.CancelButton;
                if (cancelBtn != null)
                {
                    cancelBtn.Click();
                    _log?.LogInfo("SubscriptionCart dismissed via Escape");
                    return;
                }
            }
        }
        catch { }

        // Alert2 dialog — click Cancel button
        try
        {
            if (_cachedAlert != null && _alertWasOpen)
            {
                var cancelBtn = _cachedAlert.CancelButton;
                if (cancelBtn != null)
                {
                    cancelBtn.Click();
                    _log?.LogInfo("Alert dismissed via Escape");
                    return;
                }
            }
        }
        catch { }

        // Picker dialog — click backdrop button to dismiss
        try
        {
            if (_cachedPicker != null && _pickerWasActive)
            {
                var backdrop = _cachedPicker.BackdropButton;
                if (backdrop != null)
                {
                    backdrop.onClick?.Invoke();
                    _log?.LogInfo("Picker dismissed via Escape");
                    return;
                }
            }
        }
        catch { }

        // Fallback: just clear the dialog state
        IsDialogOpen = false;
        RequestRescan();
        _log?.LogInfo("Dialog dismissed (fallback)");
    }

    /// <summary>Sets focus groups while restoring to the saved group name (from before a dialog opened).
    /// Uses SetGroupsRestoring to avoid double announcements. Returns true if restore was used.</summary>
    private bool SetGroupsWithRestore(List<FocusGroup> groups)
    {
        if (_restoreGroupName == null) return false;
        string groupName = _restoreGroupName;
        int elementIndex = _restoreElementIndex;
        _restoreGroupName = null;
        FocusManager.Instance?.SetGroupsRestoring(groups, groupName, elementIndex);
        return true;
    }

    // ── End-turn confirmation prompt ─────────────────────────────────

    /// <summary>Shows the end-turn confirmation prompt as a focus group with Confirm and Cancel.</summary>
    public void ShowEndTurnConfirm(int gold)
    {
        _restoreGroupName = FocusManager.Instance?.CurrentGroup?.Name;
        _restoreElementIndex = FocusManager.Instance?.CurrentElementIndex ?? 0;
        IsDialogOpen = true;

        var group = new FocusGroup("End Turn");
        group.Elements.Add(new FocusElement("Confirm")
        {
            Type = "button",
            OnActivate = () => { ShopAnnouncer.Instance?.ConfirmEndTurn(); }
        });
        group.Elements.Add(new FocusElement("Cancel")
        {
            Type = "button",
            OnActivate = () => { ShopAnnouncer.Instance?.CancelEndTurn(); }
        });

        FocusManager.Instance?.SetGroups(new List<FocusGroup> { group });
    }

    /// <summary>Dismisses the end-turn confirmation prompt and restores shop focus.</summary>
    public void DismissEndTurnConfirm()
    {
        if (!IsDialogOpen) return;
        IsDialogOpen = false;
        _needsScan = true;
        _scanDelay = 0.1f;
    }

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
                // The page might handle Back() internally (e.g. VersusCreator closing
                // its Advanced panel) without navigating to a different page.
                // Request a rescan so the mod picks up the changed UI state.
                RequestRescan();
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

    // ── History sub-page scanners ────────────────────────────────────────

    /// <summary>Scans the Achievements page. Each entry becomes a FocusElement with InfoRows
    /// (Row 0: name + unlock status, Row 1+: achievement details).</summary>
    private void ScanAchievements(Spacewood.Unity.Achievements achievementsPage)
    {
        var groups = new List<FocusGroup>();

        // ── Read filter state upfront ──
        bool filterOwned = false;
        bool filterIncomplete = false;
        try { filterOwned = achievementsPage.FilterOwned; } catch { }
        try { filterIncomplete = achievementsPage.FilterIncomplete; } catch { }

        // ── Entries group ──
        var entriesGroup = new FocusGroup("Achievements");
        try
        {
            // Entries always returns ALL entries regardless of filter state.
            // We apply the filter ourselves based on FilterOwned/FilterIncomplete flags.
            var entries = achievementsPage.Entries;
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry == null) continue;

                    try
                    {
                        string name = "";
                        try { name = entry.Title?.text ?? ""; } catch { }

                        // Skip unrevealed "???" entries — these are hidden pets
                        if (string.IsNullOrWhiteSpace(name) || name == "???")
                            continue;

                        // Determine unlock status from header visibility
                        bool revealed = false;
                        try { revealed = entry.HeaderRevealed != null && entry.HeaderRevealed.gameObject.activeSelf; } catch { }

                        bool hasStar = false;
                        bool hasSuperStar = false;
                        try { hasStar = entry.Star != null && entry.Star.gameObject.activeSelf; } catch { }
                        try { hasSuperStar = entry.SuperStar != null && entry.SuperStar.gameObject.activeSelf; } catch { }

                        // Apply filter: Entries doesn't respect filters, so we filter ourselves
                        if (filterOwned && !revealed)
                            continue; // "Only owned" = skip pets you haven't unlocked
                        if (filterIncomplete && revealed && hasStar && hasSuperStar)
                            continue; // "Only unachieved" = skip fully completed pets

                        var infoRows = new List<string>();

                        // Row 0: Name + status
                        string status = revealed ? "Unlocked" : "Locked";
                        if (hasSuperStar)
                            status += ", Super star";
                        else if (hasStar)
                            status += ", Star";
                        infoRows.Add($"{name}. {status}.");

                        // Row 1: Star details
                        if (revealed)
                        {
                            var detailParts = new List<string>();
                            if (hasStar)
                                detailParts.Add("Victory achieved");
                            else
                                detailParts.Add("No victory yet");
                            if (hasSuperStar)
                                detailParts.Add("Hard mode victory achieved");
                            else
                                detailParts.Add("No hard mode victory");
                            infoRows.Add(string.Join(". ", detailParts) + ".");
                        }

                        var capturedEntry = entry;
                        var element = new FocusElement(name, i)
                        {
                            InfoRows = infoRows,
                            Tag = capturedEntry,
                            OnActivate = () =>
                            {
                                try
                                {
                                    capturedEntry.HandleSubmit();
                                    _log?.LogInfo($"Achievement activated: {name}");
                                }
                                catch (System.Exception ex)
                                {
                                    _log?.LogError($"Achievement activate error: {ex}");
                                }
                            }
                        };

                        entriesGroup.Elements.Add(element);
                    }
                    catch { }
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Achievements entry scan error: {ex}");
        }

        if (entriesGroup.Elements.Count > 0)
            groups.Add(entriesGroup);

        // ── Actions group (filter, sort) ──
        var actionsGroup = new FocusGroup("Actions");

        // Filter: cycle through filter states directly instead of using the picker,
        // since the picker's Pick() callback doesn't reliably apply the filter.
        try
        {
            var capturedPage = achievementsPage;
            // Build label showing current filter state
            string filterLabel = "Filter: All";
            try
            {
                if (capturedPage.FilterOwned)
                    filterLabel = "Filter: Only owned";
                else if (capturedPage.FilterIncomplete)
                    filterLabel = "Filter: Only unachieved";
            }
            catch { }

            actionsGroup.Elements.Add(new FocusElement(filterLabel)
            {
                Type = "button",
                OnActivate = () =>
                {
                    try
                    {
                        // Cycle: All → Only owned → Only unachieved → All
                        bool wasOwned = capturedPage.FilterOwned;
                        bool wasIncomplete = capturedPage.FilterIncomplete;

                        if (!wasOwned && !wasIncomplete)
                        {
                            // All → Only owned
                            capturedPage.FilterOwned = true;
                            capturedPage.FilterIncomplete = false;
                            ScreenReader.Instance.Say("Filter: Only owned.");
                        }
                        else if (wasOwned)
                        {
                            // Only owned → Only unachieved
                            capturedPage.FilterOwned = false;
                            capturedPage.FilterIncomplete = true;
                            ScreenReader.Instance.Say("Filter: Only unachieved.");
                        }
                        else
                        {
                            // Only unachieved → All
                            capturedPage.FilterOwned = false;
                            capturedPage.FilterIncomplete = false;
                            ScreenReader.Instance.Say("Filter: All.");
                        }

                        // Refresh the view with new filter
                        capturedPage.UpdateView();
                        RequestRescan();
                    }
                    catch (System.Exception ex)
                    {
                        _log?.LogError($"Achievement filter cycle error: {ex}");
                    }
                }
            });
        }
        catch { }

        try
        {
            var sortBtn = achievementsPage.SortButton;
            if (sortBtn != null)
            {
                var capturedBtn = sortBtn;
                actionsGroup.Elements.Add(new FocusElement("Sort")
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch { }
                    }
                });
            }
        }
        catch { }

        if (actionsGroup.Elements.Count > 0)
            groups.Add(actionsGroup);

        // ── Counters (read-only info) ──
        var countersGroup = new FocusGroup("Counters");
        void AddCounter(Spacewood.Unity.AchievementsCounter? counter, string fallbackName)
        {
            if (counter == null) return;
            try
            {
                string label = "";
                try { label = counter.Label?.text ?? ""; } catch { }
                if (string.IsNullOrWhiteSpace(label))
                    label = fallbackName;

                countersGroup.Elements.Add(new FocusElement($"{fallbackName}: {label}"));
            }
            catch { }
        }

        try { AddCounter(achievementsPage.CounterMaxLevel, "Max Level"); } catch { }
        try { AddCounter(achievementsPage.CounterVictory, "Victory"); } catch { }
        try { AddCounter(achievementsPage.CounterVictoryHard, "Victory Hard"); } catch { }

        if (countersGroup.Elements.Count > 0)
            groups.Add(countersGroup);

        if (groups.Count == 0)
        {
            _log?.LogDebug("Achievements page: no entries found");
            return;
        }

        if (!SetGroupsWithRestore(groups))
            FocusManager.Instance?.SetGroups(groups);

        string filterName = filterOwned ? "Only owned" : filterIncomplete ? "Only unachieved" : "All";
        _log?.LogInfo($"Achievements scan: {entriesGroup.Elements.Count} entries (filter: {filterName})");
    }

    /// <summary>Scans the Replay page. Each replay entry gets InfoRows with date, board, result.</summary>
    private void ScanReplay(Spacewood.Unity.Replay replayPage)
    {
        var groups = new List<FocusGroup>();

        // ── Replay entries ──
        var replaysGroup = new FocusGroup("Replays");
        try
        {
            var items = replayPage.ItemContainer?.GetComponentsInChildren<Spacewood.Unity.ReplayItem>(false);
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null) continue;

                    try
                    {
                        string boardName = "";
                        try { boardName = item.BoardNameText?.text ?? ""; } catch { }
                        if (string.IsNullOrWhiteSpace(boardName))
                            boardName = $"Replay {i + 1}";

                        string date = "";
                        try { date = item.DateText?.text ?? ""; } catch { }

                        string victory = "";
                        try { victory = item.VictoryText?.text ?? ""; } catch { }

                        string lives = "";
                        try { lives = item.LivesText?.text ?? ""; } catch { }

                        string turn = "";
                        try { turn = item.TurnText?.text ?? ""; } catch { }

                        var infoRows = new List<string>();

                        // Row 0: Board name + result
                        string row0 = boardName;
                        if (!string.IsNullOrWhiteSpace(victory))
                            row0 += $". {victory}";
                        infoRows.Add(row0);

                        // Row 1: Date
                        if (!string.IsNullOrWhiteSpace(date))
                            infoRows.Add($"Date: {date}");

                        // Row 2: Lives and turns
                        var detailParts = new List<string>();
                        if (!string.IsNullOrWhiteSpace(lives))
                            detailParts.Add($"Lives: {lives}");
                        if (!string.IsNullOrWhiteSpace(turn))
                            detailParts.Add($"Turn: {turn}");
                        if (detailParts.Count > 0)
                            infoRows.Add(string.Join(". ", detailParts));

                        var capturedItem = item;
                        var element = new FocusElement(boardName, i)
                        {
                            InfoRows = infoRows,
                            Tag = capturedItem,
                            OnActivate = () =>
                            {
                                try
                                {
                                    capturedItem.Button?.Click();
                                    _log?.LogInfo($"Replay activated: {boardName}");
                                }
                                catch (System.Exception ex)
                                {
                                    _log?.LogError($"Replay activate error: {ex}");
                                }
                            }
                        };

                        replaysGroup.Elements.Add(element);
                    }
                    catch { }
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Replay scan error: {ex}");
        }

        if (replaysGroup.Elements.Count > 0)
            groups.Add(replaysGroup);

        // ── Shared replay actions ──
        var actionsGroup = new FocusGroup("Actions");

        try
        {
            var clipboardBtn = replayPage.SharedClipboardPlayButton;
            if (clipboardBtn != null)
            {
                try { if (!clipboardBtn.GetInteractable()) clipboardBtn = null; } catch { clipboardBtn = null; }
                if (clipboardBtn != null)
                {
                    var capturedBtn = clipboardBtn;
                    actionsGroup.Elements.Add(new FocusElement("Play from clipboard")
                    {
                        Type = "button",
                        OnActivate = () =>
                        {
                            try { capturedBtn.Click(); }
                            catch { }
                        }
                    });
                }
            }
        }
        catch { }

        try
        {
            var manualInput = replayPage.SharedManualPlayInput;
            if (manualInput != null)
            {
                var capturedInput = manualInput;
                actionsGroup.Elements.Add(new FocusElement("Replay code")
                {
                    Type = "editbox",
                    DynamicDetail = () =>
                    {
                        try { return capturedInput.InputField?.text; }
                        catch { return null; }
                    },
                    OnActivate = () =>
                    {
                        try
                        {
                            var tmpInput = capturedInput.InputField;
                            if (tmpInput != null)
                                StartEditing(tmpInput);
                        }
                        catch { }
                    }
                });
            }
        }
        catch { }

        try
        {
            var manualBtn = replayPage.SharedManualPlayButton;
            if (manualBtn != null)
            {
                var capturedBtn = manualBtn;
                actionsGroup.Elements.Add(new FocusElement("Play manual code")
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch { }
                    }
                });
            }
        }
        catch { }

        if (actionsGroup.Elements.Count > 0)
            groups.Add(actionsGroup);

        if (groups.Count == 0)
        {
            // Show empty state
            var emptyGroup = new FocusGroup("Replays");
            emptyGroup.Elements.Add(new FocusElement("No replays available."));
            groups.Add(emptyGroup);
        }

        if (!SetGroupsWithRestore(groups))
            FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"Replay scan: {replaysGroup.Elements.Count} entries");
    }

    /// <summary>Scans the StatsSummary page. Each stat item gets InfoRows with label and value.</summary>
    private void ScanStatsSummary(Spacewood.Unity.StatsSummary statsPage)
    {
        var groups = new List<FocusGroup>();

        // ── Stats entries ──
        var statsGroup = new FocusGroup("Stats");
        try
        {
            var items = statsPage.ItemContainer?.GetComponentsInChildren<Spacewood.Unity.StatsSummaryItem>(false);
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null) continue;

                    try
                    {
                        string label = "";
                        try { label = item.Label?.text ?? ""; } catch { }
                        if (string.IsNullOrWhiteSpace(label))
                            label = $"Stat {i + 1}";

                        string value = "";
                        try { value = item.Value?.text ?? ""; } catch { }

                        // For "most/least bought" entries, the pet/food name is only in the sprite.
                        // Extract it so screen reader users know which pet/food this stat is for.
                        string spriteName = "";
                        try { spriteName = item.Image?.sprite?.name ?? ""; } catch { }

                        // Skip entries with zero count for per-pet/food stats (empty slots)
                        if (value == "0" && !string.IsNullOrWhiteSpace(spriteName))
                            continue;

                        // Build display label with sprite name (pet/food) and value
                        string displayLabel;
                        if (!string.IsNullOrWhiteSpace(spriteName) && !string.IsNullOrWhiteSpace(value))
                            displayLabel = $"{spriteName}. {label}: {value}";
                        else if (!string.IsNullOrWhiteSpace(value))
                            displayLabel = $"{label}: {value}";
                        else
                            displayLabel = label;

                        var capturedItem = item;
                        var capturedLabel = displayLabel;
                        var element = new FocusElement(displayLabel, i)
                        {
                            Tag = capturedItem,
                            OnActivate = () =>
                            {
                                try
                                {
                                    var detailsBtn = capturedItem.Details;
                                    if (detailsBtn != null)
                                    {
                                        detailsBtn.Click();
                                        _log?.LogInfo($"Stats details opened: {capturedLabel}");
                                        // Schedule rescan to pick up StatsDetails panel
                                        RequestRescan();
                                    }
                                }
                                catch (System.Exception ex)
                                {
                                    _log?.LogError($"Stats details error: {ex}");
                                }
                            }
                        };

                        statsGroup.Elements.Add(element);
                    }
                    catch { }
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Stats scan error: {ex}");
        }

        if (statsGroup.Elements.Count > 0)
            groups.Add(statsGroup);

        // ── Filters group ──
        var filtersGroup = new FocusGroup("Filters");

        try
        {
            var modeBtn = statsPage.ModeFilterButton;
            if (modeBtn != null)
            {
                string modeLabel = "Mode filter";
                try
                {
                    string? title = modeBtn.GetTitle();
                    if (!string.IsNullOrWhiteSpace(title))
                        modeLabel = $"Mode: {title}";
                }
                catch { }

                var capturedBtn = modeBtn;
                filtersGroup.Elements.Add(new FocusElement(modeLabel)
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); RequestRescan(); }
                        catch { }
                    }
                });
            }
        }
        catch { }

        try
        {
            var packBtn = statsPage.PackFilterButton;
            if (packBtn != null)
            {
                string packLabel = "Pack filter";
                try
                {
                    string? title = packBtn.GetTitle();
                    if (!string.IsNullOrWhiteSpace(title))
                        packLabel = $"Pack: {title}";
                }
                catch { }

                var capturedBtn = packBtn;
                filtersGroup.Elements.Add(new FocusElement(packLabel)
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); RequestRescan(); }
                        catch { }
                    }
                });
            }
        }
        catch { }

        if (filtersGroup.Elements.Count > 0)
            groups.Add(filtersGroup);

        // ── StatsDetails panel (if open) ──
        try
        {
            var details = statsPage.StatsDetails;
            if (details != null && details.IsOpen)
            {
                var detailsGroup = new FocusGroup("Details");
                var detailItems = details.ItemContainer?.GetComponentsInChildren<Spacewood.Unity.StatsDetailsItem>(false);
                if (detailItems != null)
                {
                    for (int di = 0; di < detailItems.Count; di++)
                    {
                        var dItem = detailItems[di];
                        if (dItem == null) continue;
                        try
                        {
                            string dLabel = "";
                            try { dLabel = dItem.Label?.text ?? ""; } catch { }
                            if (string.IsNullOrWhiteSpace(dLabel))
                                dLabel = $"Detail {di + 1}";

                            detailsGroup.Elements.Add(new FocusElement(dLabel, di));
                        }
                        catch { }
                    }
                }

                if (detailsGroup.Elements.Count > 0)
                    groups.Add(detailsGroup);
            }
        }
        catch { }

        if (groups.Count == 0)
        {
            _log?.LogDebug("Stats page: no items found");
            return;
        }

        if (!SetGroupsWithRestore(groups))
            FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"Stats scan: {statsGroup.Elements.Count} items");
    }

    /// <summary>Scans the Spectate page. Each match entry is a focusable element.</summary>
    private void ScanSpectate(Spacewood.Unity.Spectate spectatePage)
    {
        var groups = new List<FocusGroup>();
        var matchesGroup = new FocusGroup("Matches");

        try
        {
            var items = spectatePage.ItemContainer?.GetComponentsInChildren<Spacewood.Unity.SpectateItem>(false);
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null) continue;

                    try
                    {
                        string name = "";
                        try { name = item.NameText?.text ?? ""; } catch { }
                        if (string.IsNullOrWhiteSpace(name))
                            name = $"Match {i + 1}";

                        var capturedItem = item;
                        var element = new FocusElement(name, i)
                        {
                            Type = "button",
                            Tag = capturedItem,
                            OnActivate = () =>
                            {
                                try
                                {
                                    capturedItem.Button?.Click();
                                    _log?.LogInfo($"Spectate activated: {name}");
                                }
                                catch (System.Exception ex)
                                {
                                    _log?.LogError($"Spectate activate error: {ex}");
                                }
                            }
                        };

                        matchesGroup.Elements.Add(element);
                    }
                    catch { }
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"Spectate scan error: {ex}");
        }

        if (matchesGroup.Elements.Count > 0)
        {
            groups.Add(matchesGroup);
        }
        else
        {
            matchesGroup.Elements.Add(new FocusElement("No matches available."));
            groups.Add(matchesGroup);
        }

        if (!SetGroupsWithRestore(groups))
            FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"Spectate scan: {matchesGroup.Elements.Count} entries");
    }

    // ══════════════════════════════════════════════════════════════════
    // ManageDonations (gift shop) scanning
    // ══════════════════════════════════════════════════════════════════

    private Spacewood.Unity.DonationBuyer? _cachedDonationBuyer;
    private bool _donationBuyerWasOpen;
    private string? _donationBuyerPhase; // "decision", "receiver", or null

    private void ScanManageDonations(Spacewood.Unity.ManageDonations donations)
    {
        var groups = new List<FocusGroup>();

        // ── Gift Shop (items available for gifting) ──
        try
        {
            var shop = donations.Shop;
            if (shop != null)
            {
                // Cache the DonationBuyer for modal polling
                try { _cachedDonationBuyer = shop.Buyer; } catch { }

                var shopGroup = new FocusGroup("Gift Shop");
                var items = shop.Items;
                if (items != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        try
                        {
                            var item = items[i];
                            if (item == null) continue;
                            try { if (!item.gameObject.activeInHierarchy) continue; } catch { continue; }

                            string? name = null;
                            try { name = item.ProductTemplate?.Name; } catch { }
                            if (!IsUsefulLabel(name))
                            {
                                try { name = item.Name?.text; } catch { }
                            }
                            if (string.IsNullOrWhiteSpace(name))
                                name = $"Item {i + 1}";

                            string? price = null;
                            try { price = item.Price?.text; } catch { }

                            string label = name!;
                            if (IsUsefulLabel(price))
                                label += $", {price}";

                            var capturedItem = item;
                            var element = new FocusElement(label, i)
                            {
                                Type = "button",
                                OnActivate = () =>
                                {
                                    try { capturedItem.Button?.Click(); }
                                    catch (System.Exception ex) { _log?.LogError($"Donation item click error: {ex}"); }
                                }
                            };

                            try
                            {
                                string? desc = item.ProductTemplate?.Description;
                                if (!string.IsNullOrWhiteSpace(desc))
                                    element.Detail = desc;
                            }
                            catch { }

                            shopGroup.Elements.Add(element);
                        }
                        catch { }
                    }
                }

                if (shopGroup.Elements.Count > 0)
                    groups.Add(shopGroup);
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"ManageDonations shop scan error: {ex}");
        }

        // ── Gift List (previously purchased gifts) ──
        try
        {
            var list = donations.List;
            if (list != null)
            {
                var listItems = list.Items;
                if (listItems != null && listItems.Count > 0)
                {
                    var listGroup = new FocusGroup("Your Gifts");
                    for (int i = 0; i < listItems.Count; i++)
                    {
                        try
                        {
                            var listItem = listItems[i];
                            if (listItem == null) continue;
                            try { if (!listItem.gameObject.activeInHierarchy) continue; } catch { continue; }

                            string? name = null;
                            try { name = listItem.NameTextMesh?.text; } catch { }
                            if (string.IsNullOrWhiteSpace(name))
                                name = $"Gift {i + 1}";

                            // Check for Give and Copy buttons
                            bool hasGive = false;
                            try { hasGive = listItem.GiveButton != null && listItem.GiveButton.gameObject.activeInHierarchy; } catch { }
                            bool hasCopy = false;
                            try { hasCopy = listItem.CopyButton != null && listItem.CopyButton.gameObject.activeInHierarchy; } catch { }

                            var capturedListItem = listItem;

                            if (hasGive)
                            {
                                var capturedGiveBtn = listItem.GiveButton;
                                listGroup.Elements.Add(new FocusElement($"{name}, Give")
                                {
                                    Type = "button",
                                    OnActivate = () =>
                                    {
                                        try { capturedGiveBtn!.Click(); }
                                        catch { }
                                    }
                                });
                            }

                            if (hasCopy)
                            {
                                var capturedCopyBtn = listItem.CopyButton;
                                listGroup.Elements.Add(new FocusElement($"{name}, Copy code")
                                {
                                    Type = "button",
                                    OnActivate = () =>
                                    {
                                        try { capturedCopyBtn!.Click(); }
                                        catch { }
                                    }
                                });
                            }

                            // If neither button, add as info-only
                            if (!hasGive && !hasCopy)
                            {
                                listGroup.Elements.Add(new FocusElement(name!));
                            }
                        }
                        catch { }
                    }

                    if (listGroup.Elements.Count > 0)
                        groups.Add(listGroup);
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"ManageDonations list scan error: {ex}");
        }

        if (groups.Count == 0)
        {
            var emptyGroup = new FocusGroup("Gifts");
            emptyGroup.Elements.Add(new FocusElement("No gifts available"));
            groups.Add(emptyGroup);
        }

        if (!SetGroupsWithRestore(groups))
        {
            FocusManager.Instance?.SetGroups(groups);
            ScreenReader.Instance.Say("Gifts.");
        }
        _log?.LogInfo($"ManageDonations scan: {groups.Count} groups");
    }

    // ── DonationBuyer modal polling ──

    private void PollForDonationBuyer()
    {
        if (_cachedDonationBuyer == null) return;

        try
        {
            bool isOpen = false;
            try { isOpen = _cachedDonationBuyer.Modal != null && _cachedDonationBuyer.Modal.gameObject.activeInHierarchy; } catch { }

            if (isOpen && !_donationBuyerWasOpen)
            {
                _donationBuyerWasOpen = true;
                OnDonationBuyerOpened();
            }
            else if (isOpen && _donationBuyerWasOpen)
            {
                // Check for phase transitions within the modal (decision → receiver)
                CheckDonationBuyerPhase();
            }
            else if (!isOpen && _donationBuyerWasOpen)
            {
                _donationBuyerWasOpen = false;
                _donationBuyerPhase = null;
                OnDonationBuyerClosed();
            }
        }
        catch { }
    }

    private void OnDonationBuyerOpened()
    {
        _restoreGroupName = FocusManager.Instance?.CurrentGroup?.Name;
        _restoreElementIndex = FocusManager.Instance?.CurrentElementIndex ?? 0;
        IsDialogOpen = true;
        ScanDonationBuyerDecision();
    }

    private void ScanDonationBuyerDecision()
    {
        _donationBuyerPhase = "decision";
        var buyer = _cachedDonationBuyer;
        if (buyer == null) return;

        var decision = buyer.Decision;
        if (decision == null) return;

        var group = new FocusGroup("Gift Options");

        // Product name from the model
        string productName = "";
        try { productName = buyer.Model?.Name ?? ""; } catch { }

        try
        {
            var giveBtn = decision.GiveButton;
            if (giveBtn != null)
            {
                bool active = false;
                try { active = giveBtn.gameObject.activeInHierarchy; } catch { }
                if (active)
                {
                    string label = "Gift to friend";
                    try
                    {
                        string? btnLabel = giveBtn.Label?.text;
                        if (!string.IsNullOrWhiteSpace(btnLabel)) label = btnLabel!;
                    }
                    catch { }
                    var captured = giveBtn;
                    group.Elements.Add(new FocusElement(label)
                    {
                        Type = "button",
                        OnActivate = () => { try { captured.Click(); } catch { } }
                    });
                }
            }
        }
        catch { }

        try
        {
            var codeBtn = decision.CodeButton;
            if (codeBtn != null)
            {
                bool active = false;
                try { active = codeBtn.gameObject.activeInHierarchy; } catch { }
                if (active)
                {
                    string label = "Get code";
                    try
                    {
                        string? btnLabel = codeBtn.Label?.text;
                        if (!string.IsNullOrWhiteSpace(btnLabel)) label = btnLabel!;
                    }
                    catch { }
                    var captured = codeBtn;
                    group.Elements.Add(new FocusElement(label)
                    {
                        Type = "button",
                        OnActivate = () => { try { captured.Click(); } catch { } }
                    });
                }
            }
        }
        catch { }

        try
        {
            var cancelBtn = decision.CancelButton;
            if (cancelBtn != null)
            {
                bool active = false;
                try { active = cancelBtn.gameObject.activeInHierarchy; } catch { }
                if (active)
                {
                    string label = "Cancel";
                    try
                    {
                        string? btnLabel = cancelBtn.Label?.text;
                        if (!string.IsNullOrWhiteSpace(btnLabel)) label = btnLabel!;
                    }
                    catch { }
                    var captured = cancelBtn;
                    group.Elements.Add(new FocusElement(label)
                    {
                        Type = "button",
                        OnActivate = () => { try { captured.Click(); } catch { } }
                    });
                }
            }
        }
        catch { }

        string announcement = "Gift";
        if (!string.IsNullOrWhiteSpace(productName))
            announcement = productName;

        FocusManager.Instance?.SetGroups(new List<FocusGroup> { group });
        ScreenReader.Instance.Say(announcement);
        _log?.LogInfo($"DonationBuyer decision: {productName}, {group.Elements.Count} options");
    }

    private void ScanDonationBuyerReceiver()
    {
        _donationBuyerPhase = "receiver";
        var buyer = _cachedDonationBuyer;
        if (buyer == null) return;

        var receiver = buyer.Receiver;
        if (receiver == null) return;

        var group = new FocusGroup("Send Gift");

        // Username input field
        try
        {
            var input = receiver.Input;
            if (input != null)
            {
                group.Elements.Add(new FocusElement("Recipient username")
                {
                    Type = "editbox",
                    DynamicDetail = () =>
                    {
                        try { return input.InputField?.text; }
                        catch { return null; }
                    }
                });
            }
        }
        catch { }

        // Confirm button
        try
        {
            var confirmBtn = receiver.ConfirmButton;
            if (confirmBtn != null)
            {
                string label = "Send";
                try
                {
                    string? btnLabel = confirmBtn.Label?.text;
                    if (!string.IsNullOrWhiteSpace(btnLabel)) label = btnLabel!;
                }
                catch { }
                var captured = confirmBtn;
                group.Elements.Add(new FocusElement(label)
                {
                    Type = "button",
                    OnActivate = () => { try { captured.Click(); } catch { } }
                });
            }
        }
        catch { }

        // Cancel button
        try
        {
            var cancelBtn = receiver.CancelButton;
            if (cancelBtn != null)
            {
                string label = "Cancel";
                try
                {
                    string? btnLabel = cancelBtn.Label?.text;
                    if (!string.IsNullOrWhiteSpace(btnLabel)) label = btnLabel!;
                }
                catch { }
                var captured = cancelBtn;
                group.Elements.Add(new FocusElement(label)
                {
                    Type = "button",
                    OnActivate = () => { try { captured.Click(); } catch { } }
                });
            }
        }
        catch { }

        FocusManager.Instance?.SetGroups(new List<FocusGroup> { group });
        ScreenReader.Instance.Say("Enter recipient username.");
        _log?.LogInfo("DonationBuyer receiver phase");
    }

    private void CheckDonationBuyerPhase()
    {
        var buyer = _cachedDonationBuyer;
        if (buyer == null) return;

        // Check if Receiver is now visible (transitioned from decision to receiver)
        try
        {
            var receiver = buyer.Receiver;
            if (receiver != null && receiver.gameObject.activeInHierarchy)
            {
                if (_donationBuyerPhase != "receiver")
                    ScanDonationBuyerReceiver();
                return;
            }
        }
        catch { }

        // Check if Decision is visible
        try
        {
            var decision = buyer.Decision;
            if (decision != null && decision.gameObject.activeInHierarchy)
            {
                if (_donationBuyerPhase != "decision")
                    ScanDonationBuyerDecision();
            }
        }
        catch { }
    }

    private void OnDonationBuyerClosed()
    {
        if (!IsDialogOpen) return;
        IsDialogOpen = false;
        _needsScan = true;
        _scanDelay = 0.3f;
        _log?.LogInfo("DonationBuyer closed");
    }

    // ══════════════════════════════════════════════════════════════════
    // Customize menu scanning
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Tracks the last sub-page pointer within Customize's internal PageManager
    /// so we can detect sub-page changes in Update().</summary>
    private System.IntPtr _customizeSubPagePtr;

    private void ScanCustomize(Spacewood.Unity.Customize customizePage)
    {
        // Customize has its own PageManager for switching between BrowseShops and individual shops.
        // Check which sub-page is currently active.
        Spacewood.Unity.Page? subPage = null;
        try { subPage = customizePage.PageManager?.CurrentPage; } catch { }

        // Track sub-page pointer for change detection in Update()
        try { _customizeSubPagePtr = subPage?.Pointer ?? System.IntPtr.Zero; } catch { _customizeSubPagePtr = System.IntPtr.Zero; }

        if (subPage == null)
        {
            // Fallback: scan the BrowseShops buttons directly
            ScanBrowseShops(customizePage);
            return;
        }

        // Try each shop type
        try
        {
            var browseShops = subPage.TryCast<Spacewood.Unity.BrowseShops>();
            if (browseShops != null) { ScanBrowseShops(customizePage); return; }
        }
        catch { }

        try
        {
            var cosmeticShop = subPage.TryCast<Spacewood.Unity.CosmeticShop>();
            if (cosmeticShop != null) { ScanProductShop(cosmeticShop.Shared, "Hats"); return; }
        }
        catch { }

        try
        {
            var backgroundShop = subPage.TryCast<Spacewood.Unity.BackgroundShop>();
            if (backgroundShop != null) { ScanProductShop(backgroundShop.Shared, "Backgrounds"); return; }
        }
        catch { }

        try
        {
            var mascotShop = subPage.TryCast<Spacewood.Unity.MascotShop>();
            if (mascotShop != null) { ScanProductShop(mascotShop.Shared, "Mascots"); return; }
        }
        catch { }

        try
        {
            var entranceShop = subPage.TryCast<Spacewood.Unity.EntranceShop>();
            if (entranceShop != null) { ScanProductShop(entranceShop.Shared, "Arenas"); return; }
        }
        catch { }

        try
        {
            var awardShop = subPage.TryCast<Spacewood.Unity.AwardShop>();
            if (awardShop != null) { ScanAwardShop(awardShop); return; }
        }
        catch { }

        // Unknown sub-page, fall back to BrowseShops
        ScanBrowseShops(customizePage);
    }

    private void ScanBrowseShops(Spacewood.Unity.Customize customizePage)
    {
        var groups = new List<FocusGroup>();
        var group = new FocusGroup("Customize");

        try
        {
            var browse = customizePage.BrowseShops;
            if (browse != null)
            {
                void AddBrowseButton(Spacewood.Unity.UI.ButtonBase? btn, Spacewood.Unity.UI.Label? label, string fallback)
                {
                    if (btn == null) return;
                    try { if (!btn.gameObject.activeInHierarchy) return; } catch { return; }

                    string name = fallback;
                    try
                    {
                        string? labelText = label?.Text?.text;
                        if (!string.IsNullOrWhiteSpace(labelText))
                            name = labelText;
                    }
                    catch { }

                    var capturedBtn = btn;
                    group.Elements.Add(new FocusElement(name)
                    {
                        Type = "button",
                        OnActivate = () =>
                        {
                            try { capturedBtn.Click(); }
                            catch { }
                            if (!_needsScan) RequestRescan();
                        }
                    });
                }

                // The Label fields (CosmeticLabel, BackgroundLabel, etc.) are "New!" badges,
                // not category names — always use fallback names.
                AddBrowseButton(browse.PetButton, null, "Pets");
                AddBrowseButton(browse.CosmeticButton, null, "Hats");
                AddBrowseButton(browse.BackgroundButton, null, "Backgrounds");
                AddBrowseButton(browse.MascotButton, null, "Mascots");
                AddBrowseButton(browse.EntranceButton, null, "Arenas");
                AddBrowseButton(browse.AwardButton, null, "Awards");
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"BrowseShops scan error: {ex}");
        }

        if (group.Elements.Count > 0)
            groups.Add(group);

        if (!SetGroupsWithRestore(groups))
            FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"BrowseShops scan: {group.Elements.Count} items");
    }

    /// <summary>Scans a ProductShop sub-page using the non-generic ProductShopShared container.
    /// Finds all ProductShopItemShared children to read item names, prices, and status.</summary>
    private void ScanProductShop(Spacewood.Unity.ProductShopShared shared, string shopName)
    {
        var groups = new List<FocusGroup>();
        var itemsGroup = new FocusGroup(shopName);
        int totalItems = 0;

        try
        {
            var container = shared?.ItemContainer;
            if (container != null)
            {
                var items = container.GetComponentsInChildren<Spacewood.Unity.ProductShopItemShared>(false);
                if (items != null)
                {
                    for (int i = 0; i < items.Length; i++)
                    {
                        var item = items[i];
                        if (item == null) continue;

                        try
                        {
                            totalItems++;
                            string name = "";
                            try { name = item.Name?.Text?.text ?? ""; } catch { }
                            if (string.IsNullOrWhiteSpace(name))
                                name = $"Item {i + 1}";

                            // Check status
                            bool isLocked = false;
                            try { isLocked = item.Lock != null && item.Lock.gameObject.activeSelf; } catch { }

                            bool isExcluded = false;
                            try { isExcluded = item.Excluded != null && item.Excluded.gameObject.activeSelf; } catch { }

                            // Price
                            string price = "";
                            try { price = item.Price?.Text?.text ?? ""; } catch { }

                            // Build detail
                            var detailParts = new List<string>();
                            if (isLocked)
                                detailParts.Add("Locked");
                            if (isExcluded)
                                detailParts.Add("Excluded from random");
                            if (!string.IsNullOrWhiteSpace(price))
                                detailParts.Add(price);

                            string? detail = detailParts.Count > 0 ? string.Join(", ", detailParts) : null;

                            var capturedBtn = item.Button;

                            // Check for ShowcaseButton (Preview)
                            bool hasPreview = false;
                            try { hasPreview = item.ShowcaseButton != null && item.ShowcaseButton.gameObject.activeInHierarchy; } catch { }

                            if (hasPreview)
                                detailParts.Insert(0, "Preview available");
                            detail = detailParts.Count > 0 ? string.Join(", ", detailParts) : null;

                            var element = new FocusElement(name, i)
                            {
                                Detail = detail,
                                Type = "button",
                                OnActivate = () =>
                                {
                                    try { capturedBtn?.Click(); }
                                    catch { }
                                    if (!_needsScan) RequestRescan();
                                }
                            };
                            itemsGroup.Elements.Add(element);

                            // Add Preview element if ShowcaseButton is available
                            if (hasPreview)
                            {
                                var capturedShowcase = item.ShowcaseButton;
                                itemsGroup.Elements.Add(new FocusElement($"{name} Preview", i)
                                {
                                    Type = "button",
                                    OnActivate = () =>
                                    {
                                        try { capturedShowcase?.Click(); }
                                        catch { }
                                    }
                                });
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"{shopName} shop scan error: {ex}");
        }

        if (itemsGroup.Elements.Count > 0)
            groups.Add(itemsGroup);

        // Actions group: randomize toggle
        var actionsGroup = new FocusGroup("Actions");
        try
        {
            var randomizeBtn = shared?.RandomizeButton;
            if (randomizeBtn != null)
            {
                var capturedBtn = randomizeBtn;
                actionsGroup.Elements.Add(new FocusElement("Randomize")
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch { }
                        if (!_needsScan) RequestRescan();
                    }
                });
            }
        }
        catch { }

        if (actionsGroup.Elements.Count > 0)
            groups.Add(actionsGroup);

        if (!SetGroupsWithRestore(groups))
            FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"{shopName} shop scan: {itemsGroup.Elements.Count} items (of {totalItems} visible)");
    }

    private void ScanAwardShop(Spacewood.Unity.AwardShop awardShop)
    {
        var groups = new List<FocusGroup>();
        var itemsGroup = new FocusGroup("Awards");

        try
        {
            // AwardShop has a private _items list; try accessing via shared ItemContainer
            var container = awardShop.shared?.ItemContainer;
            if (container != null)
            {
                var items = container.GetComponentsInChildren<Spacewood.Unity.AwardShopItem>(false);
                if (items != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        var item = items[i];
                        if (item == null) continue;

                        try
                        {
                            string name = $"Award {i + 1}";
                            // Try to get award model info
                            try
                            {
                                var model = item.Model;
                                if (model != null)
                                {
                                    string? modelName = model.Model;
                                    if (!string.IsNullOrWhiteSpace(modelName))
                                        name = modelName;
                                }
                            }
                            catch { }

                            bool isEquipped = false;
                            try { isEquipped = item.activator != null && item.activator.gameObject.activeSelf; } catch { }

                            string? detail = isEquipped ? "Equipped" : null;

                            var capturedItem = item;
                            var element = new FocusElement(name, i)
                            {
                                Detail = detail,
                                Type = "button",
                                OnActivate = () =>
                                {
                                    try { capturedItem.button?.Click(); }
                                    catch { }
                                    if (!_needsScan) RequestRescan();
                                }
                            };

                            itemsGroup.Elements.Add(element);
                        }
                        catch { }
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"AwardShop scan error: {ex}");
        }

        // "None" button
        try
        {
            var noneBtn = awardShop.none;
            if (noneBtn != null)
            {
                bool isNoneEquipped = false;
                try { isNoneEquipped = awardShop.noneActivator != null && awardShop.noneActivator.gameObject.activeSelf; } catch { }

                var capturedBtn = noneBtn;
                itemsGroup.Elements.Insert(0, new FocusElement("None")
                {
                    Detail = isNoneEquipped ? "Equipped" : null,
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch { }
                        if (!_needsScan) RequestRescan();
                    }
                });
            }
        }
        catch { }

        if (itemsGroup.Elements.Count > 0)
            groups.Add(itemsGroup);

        if (!SetGroupsWithRestore(groups))
            FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"AwardShop scan: {itemsGroup.Elements.Count} items");
    }

    private void ScanPetCustomizer(Spacewood.Unity.PetCustomizer petCustomizer)
    {
        var groups = new List<FocusGroup>();

        // Determine current mode (Skins vs Hats)
        bool isHatMode = false;
        string modeLabel = "Pets (Skins)";
        try
        {
            isHatMode = petCustomizer.Mode == Spacewood.Unity.PetCustomizerMode.Hat;
            modeLabel = isHatMode ? "Pets (Hats)" : "Pets (Skins)";
        }
        catch { }

        // ── Pet list ──
        var petsGroup = new FocusGroup(modeLabel);
        try
        {
            var items = petCustomizer.Items;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null) continue;

                    try
                    {
                        // Skip hidden items (filtered by search)
                        try { if (!item.gameObject.activeInHierarchy) continue; } catch { continue; }

                        string name = "";
                        try { name = item.Label?.text ?? ""; } catch { }
                        if (string.IsNullOrWhiteSpace(name))
                            name = $"Pet {i + 1}";

                        var capturedItem = item;
                        var element = new FocusElement(name, i)
                        {
                            DynamicDetail = () =>
                            {
                                try { return capturedItem.SkinLabel?.text; }
                                catch { return null; }
                            },
                        };

                        // Clicking a pet in Skins mode cycles its skin;
                        // in Hats mode it opens a hat picker (for subscribers who own hats).
                        element.Type = "button";
                        element.OnActivate = () =>
                        {
                            try { capturedItem.Button?.Click(); }
                            catch { }
                            if (!_needsScan) RequestRescan();
                            _scanDelay = 0.4f;
                        };

                        petsGroup.Elements.Add(element);
                    }
                    catch { }
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogError($"PetCustomizer scan error: {ex}");
        }

        if (petsGroup.Elements.Count > 0)
            groups.Add(petsGroup);

        // ── Actions group ──
        var actionsGroup = new FocusGroup("Actions");

        // Skin/Hat tabs — set mode via PersistKey and call UpdateViews()
        // Click() on these buttons doesn't trigger the game's mode switch,
        // so we set the mode directly via the persisted key (85 = GlobalPetCustomizerMode).
        var capturedCustomizer = petCustomizer;
        var modeKey = (Spacewood.Scripts.Utilities.PersistKey)85;

        try
        {
            var skinTab = petCustomizer.SkinTab;
            if (skinTab != null)
            {
                actionsGroup.Elements.Add(new FocusElement("Skins tab")
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try
                        {
                            Spacewood.Scripts.Utilities.Persist.Set<int>(modeKey, 0, true); // 0 = Pet/Skins
                            capturedCustomizer.UpdateViews();
                        }
                        catch (System.Exception ex)
                        {
                            _log?.LogError($"Skins tab error: {ex}");
                        }
                        if (!_needsScan) RequestRescan();
                    }
                });
            }
        }
        catch { }

        try
        {
            var hatTab = petCustomizer.HatTab;
            if (hatTab != null)
            {
                actionsGroup.Elements.Add(new FocusElement("Hats tab")
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try
                        {
                            Spacewood.Scripts.Utilities.Persist.Set<int>(modeKey, 1, true); // 1 = Hat
                            capturedCustomizer.UpdateViews();
                        }
                        catch (System.Exception ex)
                        {
                            _log?.LogError($"Hats tab error: {ex}");
                        }
                        if (!_needsScan) RequestRescan();
                    }
                });
            }
        }
        catch { }

        // Change All / Reset All buttons
        try
        {
            var changeAll = petCustomizer.ChangeAllButton;
            if (changeAll != null)
            {
                var capturedBtn = changeAll;
                actionsGroup.Elements.Add(new FocusElement("Change all")
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch { }
                    }
                });
            }
        }
        catch { }

        try
        {
            var resetAll = petCustomizer.ResetAllButton;
            if (resetAll != null)
            {
                var capturedBtn = resetAll;
                actionsGroup.Elements.Add(new FocusElement("Reset all")
                {
                    Type = "button",
                    OnActivate = () =>
                    {
                        try { capturedBtn.Click(); }
                        catch { }
                        if (!_needsScan) RequestRescan();
                    }
                });
            }
        }
        catch { }

        // Search field
        try
        {
            var searchField = petCustomizer.SearchField;
            if (searchField != null)
            {
                var capturedInput = searchField;
                actionsGroup.Elements.Add(new FocusElement("Search")
                {
                    Type = "editbox",
                    DynamicDetail = () =>
                    {
                        try { return capturedInput.InputField?.text; }
                        catch { return null; }
                    },
                    OnActivate = () =>
                    {
                        try
                        {
                            var tmpInput = capturedInput.InputField;
                            if (tmpInput != null)
                                StartEditing(tmpInput);
                        }
                        catch { }
                    }
                });
            }
        }
        catch { }

        if (actionsGroup.Elements.Count > 0)
            groups.Add(actionsGroup);

        if (!SetGroupsWithRestore(groups))
            FocusManager.Instance?.SetGroups(groups);

        _log?.LogInfo($"PetCustomizer scan: {petsGroup.Elements.Count} pets");
    }

}

