using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using MTGAEnhancementSuite.Firebase;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UrlScheme;
using MTGAEnhancementSuite.Helpers;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    internal static class EnhancementSuitePanel
    {
        private static GameObject _panelRoot;
        private static bool _isOpen;
        private static Transform _lobbyListContainer;
        private static TextMeshProUGUI _statusText;
        private static Coroutine _refreshCoroutine;
        private static MonoBehaviour _coroutineRunner;
        // Server browser format filter — multi-select.
        // Empty set = show everything (default).
        // Contains "__none__" sentinel = show nothing (after Select None click).
        // Otherwise = whitelist of mode IDs to show.
        private static HashSet<string> _selectedFilters = new HashSet<string>();
        private static string _filterSearchText = "";
        private static TMP_InputField _filterSearchField;
        private static List<GameObject> _filterButtons = new List<GameObject>();
        private static Dictionary<string, GameObject> _filterButtonsByKey = new Dictionary<string, GameObject>();

        // Tab system
        private static GameObject _browserContent;
        private static GameObject _settingsContent;
        private static GameObject _browserTabBtn;
        private static GameObject _settingsTabBtn;
        private static TextMeshProUGUI _titleText;

        public static bool IsOpen => _isOpen;

        public static void Toggle()
        {
            try
            {
                if (_panelRoot == null)
                    CreatePanel();

                _isOpen = !_isOpen;
                _panelRoot.SetActive(_isOpen);

                if (_isOpen)
                    RefreshLobbies();
            }
            catch (Exception ex)
            {
                PerPlayerLog.Error($"EnhancementSuitePanel.Toggle failed: {ex}");
            }
        }

        public static void Close()
        {
            if (_panelRoot != null && _isOpen)
            {
                _isOpen = false;
                _panelRoot.SetActive(false);
            }
        }

        private static void CreatePanel()
        {
            _panelRoot = new GameObject("EnhancementSuitePanel");
            UnityEngine.Object.DontDestroyOnLoad(_panelRoot);
            _coroutineRunner = FirebaseClient.Instance; // Use FirebaseClient's MonoBehaviour

            var canvas = _panelRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = _panelRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            _panelRoot.AddComponent<GraphicRaycaster>();

            // Semi-transparent dark background (clicking closes panel)
            var bg = CreateChild(_panelRoot.transform, "Background");
            StretchFull(bg);
            bg.AddComponent<Image>().color = new Color(0.05f, 0.05f, 0.1f, 0.92f);
            var bgBtn = bg.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(Close));

            // Content area
            var content = CreateChild(_panelRoot.transform, "Content");
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.2f, 0.1f);
            contentRect.anchorMax = new Vector2(0.8f, 0.9f);
            contentRect.sizeDelta = Vector2.zero;
            content.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.14f, 0.98f);

            // Title
            var title = CreateChild(content.transform, "Title");
            var titleRect = title.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.05f, 0.92f);
            titleRect.anchorMax = new Vector2(0.5f, 1f);
            titleRect.sizeDelta = Vector2.zero;
            _titleText = title.AddComponent<TextMeshProUGUI>();
            _titleText.text = "MTGA+";
            _titleText.fontSize = 28;
            _titleText.fontStyle = FontStyles.Bold;
            _titleText.alignment = TextAlignmentOptions.Left;
            _titleText.color = Color.white;

            // Tab buttons
            _browserTabBtn = CreateButton(content.transform, "BrowserTab", "Browser",
                new Vector2(0.52f, 0.93f), new Vector2(0.65f, 0.99f));
            _browserTabBtn.GetComponent<Image>().color = new Color(0.3f, 0.5f, 0.7f, 0.9f); // active
            _browserTabBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(() => SwitchTab(true)));

            _settingsTabBtn = CreateButton(content.transform, "SettingsTab", "Settings",
                new Vector2(0.66f, 0.93f), new Vector2(0.79f, 0.99f));
            _settingsTabBtn.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.25f, 0.9f); // inactive
            _settingsTabBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(() => SwitchTab(false)));

            // Close button
            var closeBtn = CreateButton(content.transform, "CloseBtn", "X",
                new Vector2(0.92f, 0.93f), new Vector2(0.98f, 0.99f));
            closeBtn.GetComponent<Image>().color = new Color(0.6f, 0.2f, 0.2f, 0.9f);
            closeBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(Close));

            // === Browser Content ===
            _browserContent = CreateChild(content.transform, "BrowserContent");
            var browserRect = _browserContent.GetComponent<RectTransform>();
            browserRect.anchorMin = Vector2.zero;
            browserRect.anchorMax = new Vector2(1f, 0.92f);
            browserRect.sizeDelta = Vector2.zero;

            // Refresh button (inside browser content)
            var refreshBtn = CreateButton(_browserContent.transform, "Refresh", "Refresh",
                new Vector2(0.82f, 0.93f), new Vector2(0.95f, 0.99f));
            refreshBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(() => RefreshLobbies()));

            // Status text
            var status = CreateChild(_browserContent.transform, "Status");
            var statusRect = status.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0.05f, 0.86f);
            statusRect.anchorMax = new Vector2(0.95f, 0.92f);
            statusRect.sizeDelta = Vector2.zero;
            _statusText = status.AddComponent<TextMeshProUGUI>();
            _statusText.text = "Loading lobbies...";
            _statusText.fontSize = 16;
            _statusText.color = new Color(0.6f, 0.6f, 0.7f);
            _statusText.alignment = TextAlignmentOptions.Left;

            // Format filter — vertical scrollable checklist of game modes.
            // Top strip has the search input + Select All / Select None.
            // Below: a scroll list of [checkbox] [mode name] rows.
            var filterRow = CreateChild(_browserContent.transform, "FilterRow");
            var filterRowRect = filterRow.GetComponent<RectTransform>();
            filterRowRect.anchorMin = new Vector2(0.03f, 0.55f);
            filterRowRect.anchorMax = new Vector2(0.97f, 0.86f);
            filterRowRect.sizeDelta = Vector2.zero;
            filterRow.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.15f);

            // Header strip: search input + Select All / Select None
            var header = CreateChild(filterRow.transform, "FilterHeader");
            var headerRect = header.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0f, 0.78f);
            headerRect.anchorMax = new Vector2(1f, 1f);
            headerRect.sizeDelta = Vector2.zero;

            var searchObj = CreateChild(header.transform, "FilterSearch");
            var searchRect = searchObj.GetComponent<RectTransform>();
            searchRect.anchorMin = new Vector2(0.02f, 0.15f);
            searchRect.anchorMax = new Vector2(0.55f, 0.85f);
            searchRect.sizeDelta = Vector2.zero;
            searchObj.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.22f, 1f);
            _filterSearchField = searchObj.AddComponent<TMP_InputField>();

            var sTextArea = CreateChild(searchObj.transform, "TextArea");
            StretchFull(sTextArea);
            sTextArea.AddComponent<RectMask2D>();
            var sTextObj = CreateChild(sTextArea.transform, "Text");
            StretchFull(sTextObj);
            var sTextTmp = sTextObj.AddComponent<TextMeshProUGUI>();
            sTextTmp.fontSize = 14;
            sTextTmp.color = Color.white;
            sTextTmp.alignment = TextAlignmentOptions.Left;
            sTextTmp.margin = new Vector4(8, 0, 8, 0);
            _filterSearchField.textComponent = sTextTmp;
            _filterSearchField.textViewport = sTextArea.GetComponent<RectTransform>();

            var sPh = CreateChild(sTextArea.transform, "Placeholder");
            StretchFull(sPh);
            var sPhTmp = sPh.AddComponent<TextMeshProUGUI>();
            sPhTmp.text = "Search formats...";
            sPhTmp.fontSize = 14;
            sPhTmp.color = new Color(0.4f, 0.4f, 0.5f);
            sPhTmp.fontStyle = FontStyles.Italic;
            sPhTmp.alignment = TextAlignmentOptions.Left;
            sPhTmp.margin = new Vector4(8, 0, 8, 0);
            _filterSearchField.placeholder = sPhTmp;

            _filterSearchField.onValueChanged.AddListener(new UnityAction<string>(s =>
            {
                _filterSearchText = s ?? "";
                ApplyChipFilter();
            }));

            // Select All — clears the filter set (empty = show everything)
            var selectAllBtn = CreateButton(header.transform, "FilterSelectAll", "Select All",
                new Vector2(0.57f, 0.15f), new Vector2(0.77f, 0.85f));
            selectAllBtn.GetComponent<Image>().color = new Color(0.2f, 0.4f, 0.5f, 0.9f);
            selectAllBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(() =>
            {
                _selectedFilters.Clear();
                UpdateAllFilterButtonStates();
                RefreshLobbies();
            }));

            // Select None — sentinel that matches nothing
            var selectNoneBtn = CreateButton(header.transform, "FilterSelectNone", "Select None",
                new Vector2(0.78f, 0.15f), new Vector2(0.98f, 0.85f));
            selectNoneBtn.GetComponent<Image>().color = new Color(0.4f, 0.2f, 0.2f, 0.9f);
            selectNoneBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(() =>
            {
                _selectedFilters.Clear();
                _selectedFilters.Add("__none__");
                UpdateAllFilterButtonStates();
                RefreshLobbies();
            }));

            // Scrollable vertical list of [checkbox] [mode name] rows
            var checklist = CreateChild(filterRow.transform, "FilterChecklist");
            var checklistRect = checklist.GetComponent<RectTransform>();
            checklistRect.anchorMin = new Vector2(0.02f, 0.05f);
            checklistRect.anchorMax = new Vector2(0.98f, 0.76f);
            checklistRect.sizeDelta = Vector2.zero;

            var listScroll = checklist.AddComponent<ScrollRect>();
            listScroll.horizontal = false;
            listScroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            // Viewport leaves a 10px strip on the right for the scrollbar
            const float filterScrollbarWidth = 10f;
            var listViewport = CreateChild(checklist.transform, "Viewport");
            var listVpRect = listViewport.GetComponent<RectTransform>();
            listVpRect.anchorMin = Vector2.zero;
            listVpRect.anchorMax = Vector2.one;
            listVpRect.offsetMin = new Vector2(0, 0);
            listVpRect.offsetMax = new Vector2(-filterScrollbarWidth, 0);
            listViewport.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.001f);
            listViewport.AddComponent<RectMask2D>();
            listScroll.viewport = listVpRect;

            // Build a vertical scrollbar pinned to the right edge of the checklist
            var filterScrollbar = BuildVerticalScrollbar(checklist.transform, filterScrollbarWidth);
            listScroll.verticalScrollbar = filterScrollbar;

            var filterListContent = CreateChild(listViewport.transform, "Content");
            var filterListContentRect = filterListContent.GetComponent<RectTransform>();
            filterListContentRect.anchorMin = new Vector2(0f, 1f);
            filterListContentRect.anchorMax = new Vector2(1f, 1f);
            filterListContentRect.pivot = new Vector2(0.5f, 1f);
            filterListContentRect.sizeDelta = new Vector2(0f, 0f);
            var filterVlg = filterListContent.AddComponent<VerticalLayoutGroup>();
            filterVlg.spacing = 1;
            filterVlg.padding = new RectOffset(2, 2, 0, 0);
            filterVlg.childForceExpandWidth = true;
            filterVlg.childForceExpandHeight = false;
            // childControlHeight=true forces VLG to apply LayoutElement.preferredHeight
            // to each row. With this off, rows default to RectTransform's 100×100 — which
            // is why the rows looked massive with tiny text inside.
            filterVlg.childControlHeight = true;
            filterVlg.childControlWidth = true;
            var filterFitter = filterListContent.AddComponent<ContentSizeFitter>();
            filterFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            listScroll.content = filterListContentRect;
            // Faster mouse-wheel scrolling — Unity's default of 1f feels glacial.
            listScroll.scrollSensitivity = 24f;

            // Build a checkbox row for every mode (skip the "none" sentinel)
            var formatKeys = ChallengeFormatState.FormatKeys;
            var formatOptions = ChallengeFormatState.FormatOptions;
            _filterButtons.Clear();
            _filterButtonsByKey.Clear();

            for (int i = 1; i < formatKeys.Length; i++)
            {
                var capturedKey = formatKeys[i];
                var capturedLabel = formatOptions[i];
                var rowGO = CreateChecklistRow(filterListContent.transform, $"Filter_{capturedKey}", capturedLabel);
                _filterButtons.Add(rowGO);
                _filterButtonsByKey[capturedKey] = rowGO;

                rowGO.GetComponent<Button>().onClick.AddListener(new UnityAction(() =>
                {
                    bool wasAllMode = !_selectedFilters.Contains("__none__") && _selectedFilters.Count == 0;
                    bool wasNoneMode = _selectedFilters.Contains("__none__");

                    _selectedFilters.Remove("__none__");

                    // From "All" → unchecking one means populate everything *except*
                    // the clicked key, so the visual matches the mental model.
                    // From "None" → checking one starts the whitelist with just it.
                    // From explicit list → toggle this key in/out as before.
                    if (wasAllMode)
                    {
                        for (int j = 1; j < formatKeys.Length; j++)
                            if (formatKeys[j] != capturedKey)
                                _selectedFilters.Add(formatKeys[j]);
                    }
                    else if (wasNoneMode)
                    {
                        _selectedFilters.Add(capturedKey);
                    }
                    else
                    {
                        if (_selectedFilters.Contains(capturedKey)) _selectedFilters.Remove(capturedKey);
                        else _selectedFilters.Add(capturedKey);

                        // If user manually re-selected every individual key,
                        // canonicalize to the empty "All" state.
                        if (_selectedFilters.Count == formatKeys.Length - 1)
                            _selectedFilters.Clear();
                    }

                    UpdateAllFilterButtonStates();
                    RefreshLobbies();
                }));
            }

            UpdateAllFilterButtonStates();
            ApplyChipFilter();

            // Lobby list scroll area — sits below the (now taller) filter
            var scrollArea = CreateChild(_browserContent.transform, "ScrollArea");
            var scrollRect = scrollArea.GetComponent<RectTransform>();
            scrollRect.anchorMin = new Vector2(0.03f, 0.12f);
            scrollRect.anchorMax = new Vector2(0.97f, 0.54f);
            scrollRect.sizeDelta = Vector2.zero;

            var scrollView = scrollArea.AddComponent<ScrollRect>();
            scrollView.horizontal = false;
            scrollView.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scrollView.scrollSensitivity = 24f;

            var viewport = CreateChild(scrollArea.transform, "Viewport");
            StretchFull(viewport);
            viewport.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.3f);
            viewport.AddComponent<Mask>().showMaskGraphic = true;
            scrollView.viewport = viewport.GetComponent<RectTransform>();

            var listContent = CreateChild(viewport.transform, "ListContent");
            var listRect = listContent.GetComponent<RectTransform>();
            listRect.anchorMin = new Vector2(0f, 1f);
            listRect.anchorMax = new Vector2(1f, 1f);
            listRect.pivot = new Vector2(0.5f, 1f);
            listRect.sizeDelta = new Vector2(0f, 0f);

            var vlg = listContent.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4;
            vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlHeight = false;
            vlg.childControlWidth = true;

            var csf = listContent.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollView.content = listRect;
            _lobbyListContainer = listContent.transform;

            // Paste invite section (bottom)
            var pasteSection = CreateChild(_browserContent.transform, "PasteSection");
            var pasteSectionRect = pasteSection.GetComponent<RectTransform>();
            pasteSectionRect.anchorMin = new Vector2(0.05f, 0.02f);
            pasteSectionRect.anchorMax = new Vector2(0.95f, 0.10f);
            pasteSectionRect.sizeDelta = Vector2.zero;

            var pasteLabel = CreateChild(pasteSection.transform, "Label");
            var pasteLabelRect = pasteLabel.GetComponent<RectTransform>();
            pasteLabelRect.anchorMin = new Vector2(0f, 0f);
            pasteLabelRect.anchorMax = new Vector2(0.2f, 1f);
            pasteLabelRect.sizeDelta = Vector2.zero;
            var pasteLabelText = pasteLabel.AddComponent<TextMeshProUGUI>();
            pasteLabelText.text = "Paste Invite:";
            pasteLabelText.fontSize = 16;
            pasteLabelText.color = Color.white;
            pasteLabelText.alignment = TextAlignmentOptions.Left;

            // Input field for pasting
            var inputObj = CreateChild(pasteSection.transform, "Input");
            var inputRect = inputObj.GetComponent<RectTransform>();
            inputRect.anchorMin = new Vector2(0.22f, 0.1f);
            inputRect.anchorMax = new Vector2(0.78f, 0.9f);
            inputRect.sizeDelta = Vector2.zero;
            inputObj.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.22f, 1f);
            var inputField = inputObj.AddComponent<TMP_InputField>();

            var inputTextArea = CreateChild(inputObj.transform, "TextArea");
            StretchFull(inputTextArea);
            inputTextArea.AddComponent<RectMask2D>();

            var inputText = CreateChild(inputTextArea.transform, "Text");
            StretchFull(inputText);
            var inputTmp = inputText.AddComponent<TextMeshProUGUI>();
            inputTmp.fontSize = 14;
            inputTmp.color = Color.white;
            inputTmp.alignment = TextAlignmentOptions.Left;
            inputField.textComponent = inputTmp;
            inputField.textViewport = inputTextArea.GetComponent<RectTransform>();

            var placeholder = CreateChild(inputTextArea.transform, "Placeholder");
            StretchFull(placeholder);
            var phTmp = placeholder.AddComponent<TextMeshProUGUI>();
            phTmp.text = "mtgaes://join/...";
            phTmp.fontSize = 14;
            phTmp.color = new Color(0.4f, 0.4f, 0.5f);
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.alignment = TextAlignmentOptions.Left;
            inputField.placeholder = phTmp;

            // Join button for pasted invite
            var joinPasteBtn = CreateButton(pasteSection.transform, "JoinPaste", "Join",
                new Vector2(0.80f, 0.1f), new Vector2(1f, 0.9f));
            var capturedInput = inputField;
            joinPasteBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(() =>
            {
                var url = capturedInput.text?.Trim();
                if (!string.IsNullOrEmpty(url))
                {
                    JoinFromUrl(url);
                }
            }));

            // === Settings Content (hidden by default) ===
            _settingsContent = CreateChild(content.transform, "SettingsContent");
            var settingsRect = _settingsContent.GetComponent<RectTransform>();
            settingsRect.anchorMin = Vector2.zero;
            settingsRect.anchorMax = new Vector2(1f, 0.92f);
            settingsRect.sizeDelta = Vector2.zero;
            _settingsContent.SetActive(false);

            // Settings header
            var settingsHeader = CreateChild(_settingsContent.transform, "SettingsHeader");
            var shRect = settingsHeader.GetComponent<RectTransform>();
            shRect.anchorMin = new Vector2(0.05f, 0.88f);
            shRect.anchorMax = new Vector2(0.95f, 0.98f);
            shRect.sizeDelta = Vector2.zero;
            var shText = settingsHeader.AddComponent<TextMeshProUGUI>();
            shText.text = "Gameplay Settings";
            shText.fontSize = 22;
            shText.fontStyle = FontStyles.Bold;
            shText.color = Color.white;
            shText.alignment = TextAlignmentOptions.Left;

            // Companion toggle
            CreateToggle(_settingsContent.transform, "CompanionToggle",
                "Disable Companions",
                "Hides all pets/companions during matches — no rendering, animations, or sounds.",
                new Vector2(0.05f, 0.745f), new Vector2(0.95f, 0.850f),
                ModSettings.Instance.DisableCompanions,
                (val) =>
                {
                    ModSettings.Instance.DisableCompanions = val;
                    ModSettings.Instance.Save();
                    Toast.Info(val ? "Companions disabled (takes effect next match)" : "Companions enabled (takes effect next match)");
                });

            // Card VFX toggle
            CreateToggle(_settingsContent.transform, "CardVFXToggle",
                "Disable Card Animations",
                "Removes flashy ETB effects, 3D model popups, and cosmetic card animations. Basic arrival sounds are kept.",
                new Vector2(0.05f, 0.615f), new Vector2(0.95f, 0.720f),
                ModSettings.Instance.DisableCardVFX,
                (val) =>
                {
                    ModSettings.Instance.DisableCardVFX = val;
                    ModSettings.Instance.Save();
                    Toast.Info(val ? "Card animations disabled" : "Card animations enabled");
                });

            // Force simplified card view toggle
            CreateToggle(_settingsContent.transform, "ForceSimplifiedToggle",
                "Force Simplified Cards",
                "Always show cards without special styling (showcase, borderless, alt-art). Artwork is kept. Applies in matches and in the examine view.",
                new Vector2(0.05f, 0.485f), new Vector2(0.95f, 0.590f),
                ModSettings.Instance.ForceSimplifiedCards,
                (val) =>
                {
                    ModSettings.Instance.ForceSimplifiedCards = val;
                    ModSettings.Instance.Save();
                    Toast.Info(val ? "Simplified cards on" : "Simplified cards off");
                });


            // Export collection — opens a Windows save dialog and writes the
            // owned cards in MTGA deck-list format. Useful for backing up
            // the collection or pasting into deckbuilders.
            CreateActionRow(_settingsContent.transform, "ExportCollectionRow",
                "Export Collection",
                "Save your owned cards to a text file (MTGA deck format) — for sharing with deckbuilders or backing up your collection.",
                new Vector2(0.05f, 0.355f), new Vector2(0.95f, 0.460f),
                "Export…",
                () =>
                {
                    try { Features.CollectionExporter.Export(); }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"Export collection failed: {ex}");
                        Toast.Error("Export failed — see log");
                    }
                });

            // Settings note
            var settingsNote = CreateChild(_settingsContent.transform, "SettingsNote");
            var snRect = settingsNote.GetComponent<RectTransform>();
            snRect.anchorMin = new Vector2(0.05f, 0.05f);
            snRect.anchorMax = new Vector2(0.95f, 0.15f);
            snRect.sizeDelta = Vector2.zero;
            var snText = settingsNote.AddComponent<TextMeshProUGUI>();
            snText.text = "Settings are saved automatically and persist across game restarts.";
            snText.fontSize = 13;
            snText.color = new Color(0.5f, 0.5f, 0.6f);
            snText.fontStyle = FontStyles.Italic;
            snText.alignment = TextAlignmentOptions.Left;

            _panelRoot.SetActive(false);
            PerPlayerLog.Info("Server browser panel created");
        }

        private static void SwitchTab(bool showBrowser)
        {
            if (_browserContent != null) _browserContent.SetActive(showBrowser);
            if (_settingsContent != null) _settingsContent.SetActive(!showBrowser);

            if (_browserTabBtn != null)
                _browserTabBtn.GetComponent<Image>().color = showBrowser
                    ? new Color(0.3f, 0.5f, 0.7f, 0.9f)
                    : new Color(0.15f, 0.15f, 0.25f, 0.9f);

            if (_settingsTabBtn != null)
                _settingsTabBtn.GetComponent<Image>().color = !showBrowser
                    ? new Color(0.3f, 0.5f, 0.7f, 0.9f)
                    : new Color(0.15f, 0.15f, 0.25f, 0.9f);

            if (showBrowser)
                RefreshLobbies();
        }

        /// <summary>
        /// Same row layout as CreateToggle but with a single action button on
        /// the right instead of an ON/OFF toggle. Used for one-shot settings
        /// like "Export Collection".
        /// </summary>
        private static void CreateActionRow(Transform parent, string name, string label, string description,
            Vector2 anchorMin, Vector2 anchorMax, string buttonLabel, Action onClick)
        {
            var row = CreateChild(parent, name);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.anchorMin = anchorMin;
            rowRect.anchorMax = anchorMax;
            rowRect.sizeDelta = Vector2.zero;
            row.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.18f, 0.8f);

            // Label
            var labelObj = CreateChild(row.transform, "Label");
            var labelRect = labelObj.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0.04f, 0.5f);
            labelRect.anchorMax = new Vector2(0.7f, 1f);
            labelRect.sizeDelta = Vector2.zero;
            var labelTmp = labelObj.AddComponent<TextMeshProUGUI>();
            labelTmp.text = label;
            labelTmp.fontSize = 18;
            labelTmp.fontStyle = FontStyles.Bold;
            labelTmp.color = Color.white;
            labelTmp.alignment = TextAlignmentOptions.Left;

            // Description
            var descObj = CreateChild(row.transform, "Desc");
            var descRect = descObj.GetComponent<RectTransform>();
            descRect.anchorMin = new Vector2(0.04f, 0f);
            descRect.anchorMax = new Vector2(0.7f, 0.5f);
            descRect.sizeDelta = Vector2.zero;
            var descTmp = descObj.AddComponent<TextMeshProUGUI>();
            descTmp.text = description;
            descTmp.fontSize = 13;
            descTmp.color = new Color(0.6f, 0.6f, 0.7f);
            descTmp.alignment = TextAlignmentOptions.Left;

            // Action button on the right
            var btnObj = CreateChild(row.transform, "ActionBtn");
            var btnRect = btnObj.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.74f, 0.2f);
            btnRect.anchorMax = new Vector2(0.96f, 0.8f);
            btnRect.sizeDelta = Vector2.zero;
            btnObj.AddComponent<Image>().color = new Color(0.3f, 0.5f, 0.7f, 0.9f);
            var btnText = CreateChild(btnObj.transform, "Text");
            StretchFull(btnText);
            var btnTmp = btnText.AddComponent<TextMeshProUGUI>();
            btnTmp.text = buttonLabel;
            btnTmp.fontSize = 16;
            btnTmp.fontStyle = FontStyles.Bold;
            btnTmp.color = Color.white;
            btnTmp.alignment = TextAlignmentOptions.Center;
            var btn = btnObj.AddComponent<Button>();
            btn.onClick.AddListener(new UnityEngine.Events.UnityAction(() =>
            {
                try { onClick?.Invoke(); }
                catch (Exception ex) { Plugin.Log.LogError($"Action row '{label}' threw: {ex}"); }
            }));
        }

        private static void CreateToggle(Transform parent, string name, string label, string description,
            Vector2 anchorMin, Vector2 anchorMax, bool initialValue, Action<bool> onChanged)
        {
            var row = CreateChild(parent, name);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.anchorMin = anchorMin;
            rowRect.anchorMax = anchorMax;
            rowRect.sizeDelta = Vector2.zero;
            row.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.18f, 0.8f);

            // Label
            var labelObj = CreateChild(row.transform, "Label");
            var labelRect = labelObj.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0.12f, 0.5f);
            labelRect.anchorMax = new Vector2(0.95f, 1f);
            labelRect.sizeDelta = Vector2.zero;
            var labelTmp = labelObj.AddComponent<TextMeshProUGUI>();
            labelTmp.text = label;
            labelTmp.fontSize = 18;
            labelTmp.fontStyle = FontStyles.Bold;
            labelTmp.color = Color.white;
            labelTmp.alignment = TextAlignmentOptions.Left;

            // Description
            var descObj = CreateChild(row.transform, "Desc");
            var descRect = descObj.GetComponent<RectTransform>();
            descRect.anchorMin = new Vector2(0.12f, 0f);
            descRect.anchorMax = new Vector2(0.95f, 0.5f);
            descRect.sizeDelta = Vector2.zero;
            var descTmp = descObj.AddComponent<TextMeshProUGUI>();
            descTmp.text = description;
            descTmp.fontSize = 13;
            descTmp.color = new Color(0.6f, 0.6f, 0.7f);
            descTmp.alignment = TextAlignmentOptions.Left;

            // Toggle button (checkbox-style)
            var toggleObj = CreateChild(row.transform, "ToggleBtn");
            var toggleRect = toggleObj.GetComponent<RectTransform>();
            toggleRect.anchorMin = new Vector2(0.02f, 0.25f);
            toggleRect.anchorMax = new Vector2(0.09f, 0.75f);
            toggleRect.sizeDelta = Vector2.zero;
            var toggleBg = toggleObj.AddComponent<Image>();
            toggleBg.color = initialValue
                ? new Color(0.2f, 0.6f, 0.3f, 0.9f)
                : new Color(0.3f, 0.15f, 0.15f, 0.9f);

            var checkObj = CreateChild(toggleObj.transform, "Check");
            StretchFull(checkObj);
            var checkTmp = checkObj.AddComponent<TextMeshProUGUI>();
            checkTmp.text = initialValue ? "ON" : "OFF";
            checkTmp.fontSize = 14;
            checkTmp.fontStyle = FontStyles.Bold;
            checkTmp.color = Color.white;
            checkTmp.alignment = TextAlignmentOptions.Center;

            var btn = toggleObj.AddComponent<Button>();
            var capturedBg = toggleBg;
            var capturedCheck = checkTmp;
            bool currentValue = initialValue;
            btn.onClick.AddListener(new UnityAction(() =>
            {
                currentValue = !currentValue;
                capturedBg.color = currentValue
                    ? new Color(0.2f, 0.6f, 0.3f, 0.9f)
                    : new Color(0.3f, 0.15f, 0.15f, 0.9f);
                capturedCheck.text = currentValue ? "ON" : "OFF";
                onChanged?.Invoke(currentValue);
            }));
        }

        public static void RefreshLobbies()
        {
            if (_statusText != null)
                _statusText.text = "Loading lobbies...";

            ClearLobbyList();

            FirebaseClient.Instance.ListPublicLobbies(lobbies =>
            {
                ClearLobbyList();

                if (lobbies == null || !lobbies.HasValues)
                {
                    if (_statusText != null)
                        _statusText.text = "No public lobbies found.";
                    return;
                }

                int count = 0;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                foreach (var prop in lobbies.Properties())
                {
                    var lobby = prop.Value as JObject;
                    if (lobby == null) continue;

                    var isPublic = lobby["isPublic"]?.Value<bool>() ?? false;
                    if (!isPublic) continue;

                    // Filter stale lobbies (no heartbeat in last 2 minutes)
                    var lastHeartbeat = lobby["lastHeartbeat"]?.Value<long>() ?? 0;
                    if (now - lastHeartbeat > 120) continue;

                    var challengeId = prop.Name;
                    var hostName = lobby["hostDisplayName"]?.ToString() ?? "Unknown";
                    var format = lobby["format"]?.ToString() ?? "none";
                    var isBo3 = lobby["isBestOf3"]?.Value<bool>() ?? false;

                    // Apply format filter
                    if (!FilterAllowsFormat(format))
                        continue;

                    CreateLobbyRow(challengeId, hostName, format, isBo3);
                    count++;
                }

                if (_statusText != null)
                    _statusText.text = count > 0
                        ? $"{count} public {(count == 1 ? "lobby" : "lobbies")} found"
                        : "No public lobbies found.";
            });
        }

        /// <summary>
        /// True if a lobby with the given format key passes the current filter.
        /// Empty selection = show all (legacy "All" behavior).
        /// "__none__" sentinel = show nothing.
        /// </summary>
        private static bool FilterAllowsFormat(string format)
        {
            if (_selectedFilters.Count == 0) return true;
            if (_selectedFilters.Contains("__none__")) return false;
            return _selectedFilters.Contains(format);
        }

        private static void UpdateAllFilterButtonStates()
        {
            // Filter-state semantics → visual mapping:
            //   _selectedFilters empty + no sentinel  → "All" mode → every row checked
            //   _selectedFilters contains "__none__"  → "None" mode → every row unchecked
            //   _selectedFilters has explicit keys    → those keys checked, others unchecked
            // This matches user expectation: pressing "Select All" makes every checkbox
            // appear checked; pressing "Select None" makes every checkbox appear empty.
            bool nothingMode = _selectedFilters.Contains("__none__");
            bool allMode = !nothingMode && _selectedFilters.Count == 0;

            foreach (var kvp in _filterButtonsByKey)
            {
                var rowGO = kvp.Value;
                if (rowGO == null) continue;

                bool selected = allMode || (!nothingMode && _selectedFilters.Contains(kvp.Key));

                // Subtle row background tint for the selected ones
                var rowImg = rowGO.GetComponent<Image>();
                if (rowImg != null)
                    rowImg.color = selected
                        ? new Color(0.18f, 0.28f, 0.36f, 0.9f)
                        : new Color(0.12f, 0.12f, 0.18f, 0.7f);

                // Find the checkbox children we tagged and toggle them
                var box = rowGO.transform.Find("Checkbox");
                if (box != null)
                {
                    var boxImg = box.GetComponent<Image>();
                    if (boxImg != null)
                        boxImg.color = selected
                            ? new Color(0.30f, 0.55f, 0.75f, 1f)
                            : new Color(0.10f, 0.10f, 0.14f, 1f);

                    var check = box.Find("Check");
                    if (check != null) check.gameObject.SetActive(selected);
                }
            }
        }

        /// <summary>
        /// Hides chips whose label/id doesn't match the search text. Empty
        /// search shows everything.
        /// </summary>
        private static void ApplyChipFilter()
        {
            var query = (_filterSearchText ?? "").Trim().ToLowerInvariant();
            for (int i = 0; i < _filterButtons.Count; i++)
            {
                var chip = _filterButtons[i];
                var key = "";
                foreach (var kvp in _filterButtonsByKey)
                {
                    if (kvp.Value == chip) { key = kvp.Key; break; }
                }
                var label = chip.GetComponentInChildren<TextMeshProUGUI>(true)?.text ?? "";
                bool show = string.IsNullOrEmpty(query) ||
                            label.ToLowerInvariant().Contains(query) ||
                            key.ToLowerInvariant().Contains(query);
                chip.SetActive(show);
            }
        }

        /// <summary>
        /// Creates one row in the vertical filter checklist:
        ///   [☐ checkbox] [Mode display name]
        /// Each row is itself a Button so clicking anywhere toggles selection.
        /// The checkbox child is named "Checkbox" with a "Check" inner Image so
        /// UpdateAllFilterButtonStates can toggle it without re-walking children.
        /// </summary>
        private static GameObject CreateChecklistRow(Transform parent, string name, string label)
        {
            var row = new GameObject(name);
            row.transform.SetParent(parent, false);
            row.AddComponent<RectTransform>();
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = 16;
            le.minHeight = 16;
            row.AddComponent<Image>().color = new Color(0.12f, 0.12f, 0.18f, 0.7f);
            row.AddComponent<Button>();

            // Checkbox on the left
            var box = new GameObject("Checkbox");
            box.transform.SetParent(row.transform, false);
            var boxRect = box.AddComponent<RectTransform>();
            boxRect.anchorMin = new Vector2(0f, 0.5f);
            boxRect.anchorMax = new Vector2(0f, 0.5f);
            boxRect.pivot = new Vector2(0f, 0.5f);
            boxRect.anchoredPosition = new Vector2(4f, 0f);
            boxRect.sizeDelta = new Vector2(11f, 11f);
            var boxImg = box.AddComponent<Image>();
            boxImg.color = new Color(0.10f, 0.10f, 0.14f, 1f);
            boxImg.raycastTarget = false; // let the row Button receive the click

            // "Check" — a small filled square inside the checkbox shown when selected
            var check = new GameObject("Check");
            check.transform.SetParent(box.transform, false);
            var checkRect = check.AddComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0.18f, 0.18f);
            checkRect.anchorMax = new Vector2(0.82f, 0.82f);
            checkRect.sizeDelta = Vector2.zero;
            var checkImg = check.AddComponent<Image>();
            checkImg.color = new Color(1f, 1f, 1f, 0.95f);
            checkImg.raycastTarget = false;
            check.SetActive(false); // hidden by default; UpdateAllFilterButtonStates flips it on

            // Label to the right of the checkbox
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(row.transform, false);
            var trect = textObj.AddComponent<RectTransform>();
            trect.anchorMin = new Vector2(0f, 0f);
            trect.anchorMax = new Vector2(1f, 1f);
            trect.offsetMin = new Vector2(20f, 0f);
            trect.offsetMax = Vector2.zero;
            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 14;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.raycastTarget = false;
            tmp.margin = Vector4.zero;
            tmp.extraPadding = false;

            return row;
        }

        /// <summary>
        /// Creates a horizontal-layout chip used in the filter strip.
        /// </summary>
        private static GameObject CreateChip(Transform parent, string name, string label)
        {
            var chip = new GameObject(name);
            chip.transform.SetParent(parent, false);
            chip.AddComponent<RectTransform>();
            var le = chip.AddComponent<LayoutElement>();
            le.minWidth = 90;
            le.preferredHeight = 30;
            chip.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.25f, 0.9f);
            chip.AddComponent<Button>();

            var textObj = new GameObject("Text");
            textObj.transform.SetParent(chip.transform, false);
            var trect = textObj.AddComponent<RectTransform>();
            trect.anchorMin = Vector2.zero;
            trect.anchorMax = Vector2.one;
            trect.sizeDelta = Vector2.zero;
            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 13;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.margin = new Vector4(10, 0, 10, 0);

            // Make the chip width fit its label
            var chipFitter = chip.AddComponent<ContentSizeFitter>();
            chipFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

            return chip;
        }

        private static void ClearLobbyList()
        {
            if (_lobbyListContainer == null) return;
            for (int i = _lobbyListContainer.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_lobbyListContainer.GetChild(i).gameObject);
        }

        private static void CreateLobbyRow(string challengeId, string hostName, string format, bool isBo3)
        {
            if (_lobbyListContainer == null) return;

            var row = new GameObject("LobbyRow");
            row.transform.SetParent(_lobbyListContainer, false);

            var rowRect = row.AddComponent<RectTransform>();
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = 45;

            row.AddComponent<Image>().color = new Color(0.12f, 0.12f, 0.20f, 0.9f);

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10;
            hlg.padding = new RectOffset(12, 12, 6, 6);
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;

            // Host name
            var hostObj = new GameObject("Host");
            hostObj.transform.SetParent(row.transform, false);
            var hostLe = hostObj.AddComponent<LayoutElement>();
            hostLe.flexibleWidth = 2;
            var hostText = hostObj.AddComponent<TextMeshProUGUI>();
            hostText.text = hostName;
            hostText.fontSize = 18;
            hostText.color = Color.white;
            hostText.alignment = TextAlignmentOptions.Left;

            // Format
            var formatObj = new GameObject("Format");
            formatObj.transform.SetParent(row.transform, false);
            var formatLe = formatObj.AddComponent<LayoutElement>();
            formatLe.flexibleWidth = 1;
            var formatText = formatObj.AddComponent<TextMeshProUGUI>();
            formatText.text = format.Substring(0, 1).ToUpper() + format.Substring(1);
            formatText.fontSize = 16;
            formatText.color = new Color(0.4f, 0.8f, 1f);
            formatText.alignment = TextAlignmentOptions.Center;

            // Bo1/Bo3
            var boObj = new GameObject("BestOf");
            boObj.transform.SetParent(row.transform, false);
            var boLe = boObj.AddComponent<LayoutElement>();
            boLe.preferredWidth = 45;
            var boText = boObj.AddComponent<TextMeshProUGUI>();
            boText.text = isBo3 ? "Bo3" : "Bo1";
            boText.fontSize = 14;
            boText.color = isBo3 ? new Color(1f, 0.8f, 0.3f) : new Color(0.6f, 0.6f, 0.7f);
            boText.alignment = TextAlignmentOptions.Center;

            // Join button
            var joinObj = new GameObject("JoinBtn");
            joinObj.transform.SetParent(row.transform, false);
            var joinLe = joinObj.AddComponent<LayoutElement>();
            joinLe.preferredWidth = 80;
            joinObj.AddComponent<Image>().color = new Color(0.2f, 0.5f, 0.3f, 0.9f);
            var joinBtn = joinObj.AddComponent<Button>();

            var joinTextObj = new GameObject("Text");
            joinTextObj.transform.SetParent(joinObj.transform, false);
            StretchFull(joinTextObj);
            var joinText = joinTextObj.AddComponent<TextMeshProUGUI>();
            joinText.text = "Join";
            joinText.fontSize = 16;
            joinText.color = Color.white;
            joinText.alignment = TextAlignmentOptions.Center;

            var capturedId = challengeId;
            var capturedFormat = format;
            joinBtn.onClick.AddListener(new UnityAction(() =>
            {
                PerPlayerLog.Info($"Join button clicked for lobby {capturedId}");
                JoinLobby(capturedId, capturedFormat);
            }));

            PerPlayerLog.Info($"Created lobby row: {hostName} ({format}) id={challengeId}");
        }

        public static void JoinFromUrl(string url)
        {
            if (UrlSchemeRegistrar.ParseJoinUrl(url, out var challengeId, out var format))
            {
                JoinLobby(challengeId, format);
            }
            else
            {
                Toast.Error("Invalid invite link.");
            }
        }

        public static void JoinLobby(string challengeId, string format)
        {
            PerPlayerLog.Info($"Attempting to join lobby {challengeId} with format {format}");
            Toast.Info($"Joining lobby...");

            // Pre-check: does the lobby still exist in Firebase?
            FirebaseClient.Instance.DatabaseGet($"lobbies/{challengeId}", lobbyData =>
            {
                if (lobbyData == null || lobbyData.Type == Newtonsoft.Json.Linq.JTokenType.Null
                    || lobbyData.ToString() == "null")
                {
                    PerPlayerLog.Warning($"Lobby {challengeId} not found in Firebase — showing recovery");
                    ShowJoinFailedRecovery(format);
                    return;
                }

                // Capture bestOf from lobby data
                var isBestOf3 = lobbyData["isBestOf3"]?.Value<bool>() ?? false;
                ChallengeFormatState.IsBestOf3 = isBestOf3;

                DoJoinLobby(challengeId, format);
            });
        }

        private static void DoJoinLobby(string challengeId, string format)
        {
            ChallengeFormatState.SelectedFormat = format;
            ChallengeFormatState.IsJoining = true;
            ChallengeFormatState.InLobbySession = true;
            ChallengeFormatState.ActiveChallengeId = Guid.Parse(challengeId);

            try
            {
                Close();

                FirebaseClient.Instance.StartCoroutine(
                    JoinViaChallengeCommunicationWrapper(challengeId));
            }
            catch (Exception ex)
            {
                PerPlayerLog.Error($"JoinLobby failed: {ex}");
                Toast.Error($"Failed to join: {ex.Message}");
                ChallengeFormatState.IsJoining = false;
            }
        }

        private static IEnumerator JoinViaChallengeCommunicationWrapper(string challengeId)
        {
            PerPlayerLog.Info($"JoinViaChallengeCommunicationWrapper: {challengeId}");
            var guidId = Guid.Parse(challengeId);
            var pantryType = GameReflection.TypeByName("Pantry");

            // Step 1: Get IChallengeCommunicationWrapper from Pantry
            // (PVPChallengeController is NOT in Pantry, but IChallengeCommunicationWrapper IS)
            Type commWrapperInterfaceType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "IChallengeCommunicationWrapper" && t.IsInterface)
                    {
                        commWrapperInterfaceType = t; break;
                    }
                }
                if (commWrapperInterfaceType != null) break;
            }

            if (commWrapperInterfaceType == null)
            {
                PerPlayerLog.Error("IChallengeCommunicationWrapper type not found");
                Toast.Error("Challenge system not available");
                yield break;
            }

            var commWrapper = pantryType.GetMethod("Get").MakeGenericMethod(commWrapperInterfaceType)
                .Invoke(null, null);

            if (commWrapper == null)
            {
                PerPlayerLog.Error("IChallengeCommunicationWrapper not in Pantry");
                Toast.Error("Challenge system not initialized");
                yield break;
            }

            PerPlayerLog.Info($"Got ChallengeCommunicationWrapper: {commWrapper.GetType().Name}");

            // Step 2: Get the ChallengeService (IChallengeServiceWrapper) from the wrapper
            var svcField = AccessTools.Field(commWrapper.GetType(), "_challengeService");
            if (svcField == null)
            {
                PerPlayerLog.Error("_challengeService field not found");
                yield break;
            }

            var challengeService = svcField.GetValue(commWrapper);
            PerPlayerLog.Info($"Got ChallengeService: {challengeService.GetType().Name}");

            // Step 3: Call ChallengeJoin(string challengeId) -> Promise<ChallengeStatusResp>
            var joinMethod = AccessTools.Method(challengeService.GetType(), "ChallengeJoin");
            var joinPromise = joinMethod.Invoke(challengeService, new object[] { challengeId });
            PerPlayerLog.Info("ChallengeJoin called");

            // Step 4: Poll the Promise for completion
            // Promise<T> has: bool Successful, T Result (accessible after completion)
            var successProp = AccessTools.Property(joinPromise.GetType(), "Successful");
            var resultProp = AccessTools.Property(joinPromise.GetType(), "Result");

            PerPlayerLog.Info($"Promise type: {joinPromise.GetType().FullName}");
            PerPlayerLog.Info($"Successful prop: {(successProp != null ? "found" : "null")}");
            PerPlayerLog.Info($"Result prop: {(resultProp != null ? "found" : "null")}");

            // Log all properties on the promise for debugging
            foreach (var p in joinPromise.GetType().GetProperties())
            {
                PerPlayerLog.Info($"  Promise property: {p.Name} ({p.PropertyType.Name})");
            }

            bool? joinSuccess = null;
            object joinResult = null;
            var isDoneProp = AccessTools.Property(joinPromise.GetType(), "IsDone");
            var stateProp = AccessTools.Property(joinPromise.GetType(), "State");

            PerPlayerLog.Info($"IsDone prop: {(isDoneProp != null ? "found" : "null")}");

            // Wait for the promise to complete using IsDone, not Successful
            for (int i = 0; i < 30; i++)
            {
                yield return new WaitForSeconds(0.5f);

                try
                {
                    // Check IsDone first — Successful is only valid after the promise resolves
                    bool isDone = false;
                    if (isDoneProp != null)
                    {
                        isDone = (bool)isDoneProp.GetValue(joinPromise);
                    }
                    else if (stateProp != null)
                    {
                        // Fallback: check State enum (Pending=0, Resolved=1, Rejected=2)
                        var state = stateProp.GetValue(joinPromise);
                        isDone = state.ToString() != "Pending";
                        PerPlayerLog.Info($"Promise State: {state}");
                    }

                    if (isDone)
                    {
                        joinSuccess = successProp != null ? (bool)successProp.GetValue(joinPromise) : false;
                        PerPlayerLog.Info($"Promise resolved: IsDone=true, Successful={joinSuccess}");

                        // Always try to read Result, even if Successful=false
                        // The server may have accepted the join even if the promise reports failure
                        if (resultProp != null)
                        {
                            try
                            {
                                joinResult = resultProp.GetValue(joinPromise);
                                PerPlayerLog.Info($"Result: {(joinResult != null ? joinResult.GetType().Name : "null")}");
                            }
                            catch (Exception rex)
                            {
                                PerPlayerLog.Warning($"Could not read Result: {rex.Message}");
                            }
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    PerPlayerLog.Warning($"Promise poll error: {ex.Message}");
                }
            }

            // If we got a Result with challenge data, proceed even if Successful=false
            // The server may have accepted the join (host sees joiner) even when the promise reports failure
            bool hasValidResult = joinResult != null;
            if (hasValidResult && joinSuccess != true)
            {
                PerPlayerLog.Warning($"Promise reports Successful=false but Result is non-null — proceeding with join");
            }

            if (!hasValidResult && joinSuccess != true)
            {
                PerPlayerLog.Error($"ChallengeJoin failed or timed out (success={joinSuccess})");

                // Log all diagnostic fields
                try
                {
                    var promiseType = joinPromise.GetType();
                    var errorProp = AccessTools.Property(promiseType, "Error");
                    var errorSourceProp = AccessTools.Property(promiseType, "ErrorSource");
                    var isConnErrProp = AccessTools.Property(promiseType, "IsConnectionError");
                    var elapsedProp = AccessTools.Property(promiseType, "ElapsedMilliseconds");

                    if (errorProp != null)
                    {
                        var error = errorProp.GetValue(joinPromise);
                        PerPlayerLog.Error($"Error: {error}");
                        if (error != null)
                        {
                            foreach (var p in error.GetType().GetProperties())
                            {
                                try { PerPlayerLog.Error($"  Error.{p.Name} = {p.GetValue(error)}"); } catch { }
                            }
                            foreach (var f in error.GetType().GetFields())
                            {
                                try { PerPlayerLog.Error($"  Error.{f.Name} = {f.GetValue(error)}"); } catch { }
                            }
                        }
                    }
                    if (errorSourceProp != null)
                        PerPlayerLog.Error($"ErrorSource: {errorSourceProp.GetValue(joinPromise)}");
                    if (isConnErrProp != null)
                        PerPlayerLog.Error($"IsConnectionError: {isConnErrProp.GetValue(joinPromise)}");
                    if (elapsedProp != null)
                        PerPlayerLog.Error($"ElapsedMilliseconds: {elapsedProp.GetValue(joinPromise)}");
                }
                catch (Exception diagEx)
                {
                    PerPlayerLog.Error($"Failed to read diagnostics: {diagEx.Message}");
                }

                ChallengeFormatState.IsJoining = false;
                ShowJoinFailedRecovery(ChallengeFormatState.SelectedFormat);
                yield break;
            }

            PerPlayerLog.Info("ChallengeJoin succeeded!");

            // Delist lobby from server browser
            FirebaseClient.Instance.SetLobbyPrivate(challengeId, success =>
            {
                if (success)
                    PerPlayerLog.Info($"Lobby {challengeId} set to private after join");
                else
                    PerPlayerLog.Warning($"Failed to set lobby {challengeId} to private");
            });

            // Step 5: Extract Challenge protobuf from ChallengeStatusResp
            // and convert to PVPChallengeData via ConvertToClientModel (private method)
            if (joinResult != null)
            {
                PerPlayerLog.Info($"Result type: {joinResult.GetType().FullName}");

                // Log all properties/fields on the result
                foreach (var p in joinResult.GetType().GetProperties())
                    PerPlayerLog.Info($"  Result property: {p.Name} ({p.PropertyType.Name})");
                foreach (var f in joinResult.GetType().GetFields())
                    PerPlayerLog.Info($"  Result field: {f.Name} ({f.FieldType.Name})");

                // Get Challenge from ChallengeStatusResp
                object challengeProto = null;
                var cp = AccessTools.Property(joinResult.GetType(), "Challenge");
                if (cp != null)
                    challengeProto = cp.GetValue(joinResult);
                else
                {
                    var cf = AccessTools.Field(joinResult.GetType(), "Challenge")
                        ?? AccessTools.Field(joinResult.GetType(), "challenge_");
                    if (cf != null)
                        challengeProto = cf.GetValue(joinResult);
                }

                if (challengeProto != null)
                {
                    PerPlayerLog.Info($"Got Challenge protobuf: {challengeProto.GetType().Name}");

                    // ConvertToClientModel is PRIVATE — use AccessTools which finds private methods
                    var convertMethod = AccessTools.Method(commWrapper.GetType(), "ConvertToClientModel");
                    PerPlayerLog.Info($"ConvertToClientModel: {(convertMethod != null ? "found" : "null")}");

                    if (convertMethod != null)
                    {
                        var pvpData = convertMethod.Invoke(commWrapper, new object[] { challengeProto });
                        PerPlayerLog.Info($"ConvertToClientModel result: {(pvpData != null ? "OK" : "null")}");

                        if (pvpData != null)
                        {
                            // OnChallengeGeneralUpdate is an auto-property with backing field
                            // <OnChallengeGeneralUpdate>k__BackingField
                            // Use AccessTools.Property to get it, then read the delegate
                            var onUpdateProp = AccessTools.Property(commWrapper.GetType(), "OnChallengeGeneralUpdate");
                            if (onUpdateProp != null)
                            {
                                var delegateObj = onUpdateProp.GetValue(commWrapper) as System.Delegate;
                                if (delegateObj != null)
                                {
                                    PerPlayerLog.Info("Invoking OnChallengeGeneralUpdate via property");
                                    delegateObj.DynamicInvoke(pvpData);
                                    PerPlayerLog.Info("OnChallengeGeneralUpdate invoked successfully");
                                }
                                else
                                {
                                    PerPlayerLog.Warning("OnChallengeGeneralUpdate delegate is null (no subscribers)");
                                }
                            }
                            else
                            {
                                // Fallback: try the backing field directly
                                var backingField = commWrapper.GetType().GetField(
                                    "<OnChallengeGeneralUpdate>k__BackingField",
                                    System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.NonPublic);
                                if (backingField != null)
                                {
                                    var delegateObj = backingField.GetValue(commWrapper) as System.Delegate;
                                    if (delegateObj != null)
                                    {
                                        PerPlayerLog.Info("Invoking OnChallengeGeneralUpdate via backing field");
                                        delegateObj.DynamicInvoke(pvpData);
                                        PerPlayerLog.Info("OnChallengeGeneralUpdate invoked successfully");
                                    }
                                }
                                else
                                {
                                    PerPlayerLog.Warning("Could not find OnChallengeGeneralUpdate property or backing field");
                                }
                            }
                        }
                    }
                }
                else
                {
                    PerPlayerLog.Warning("Challenge protobuf not found in response");
                }
            }

            // Step 6: Navigate to the challenge view
            yield return new WaitForSeconds(1f);
            yield return NavigateToChallengeView(guidId);
        }

        private static IEnumerator NavigateToChallengeView(Guid challengeId)
        {
            PerPlayerLog.Info($"NavigateToChallengeView starting for {challengeId}");

            // Wait a moment for the ChallengeJoin to be processed by the server
            yield return new WaitForSeconds(2f);

            // The challenge data may not be in PVPChallengeController because
            // ChallengeJoin alone doesn't trigger HandleChallengeGeneralUpdate.
            // Try multiple approaches:

            // Approach 1: Try SocialUI.OpenPlayBlade (game's own navigation)
            PerPlayerLog.Info("Trying SocialUI.OpenPlayBlade...");
            if (TryOpenPlayBlade(challengeId))
            {
                PerPlayerLog.Info("SocialUI.OpenPlayBlade succeeded");
                // Wait and check if it actually worked
                yield return new WaitForSeconds(2f);
                Toast.Success("Joined lobby!");
                yield break;
            }

            // Approach 2: Navigate to Home with Challenge context, then ViewFriendChallenge
            PerPlayerLog.Info("Trying GoToLanding with Challenge context...");
            NavigateToHomeWithChallengeContext(challengeId);
            yield return new WaitForSeconds(3f);

            if (TryViewFriendChallengeDirect(challengeId))
            {
                Toast.Success("Joined lobby!");
                yield break;
            }

            // Approach 3: Just navigate to Home — the challenge notification system
            // might eventually bring up the challenge UI
            PerPlayerLog.Info("Trying simple GoToLanding...");
            NavigateToHomeSimple();
            yield return new WaitForSeconds(2f);

            Toast.Warning("Joined lobby — navigate to Challenges to see it.");
        }

        private static void NavigateToHomeWithChallengeContext(Guid challengeId)
        {
            try
            {
                var sceneLoaderType = GameReflection.TypeByName("SceneLoader");
                var getLoaderMethod = AccessTools.Method(sceneLoaderType, "GetSceneLoader");
                var sceneLoader = getLoaderMethod.Invoke(null, null);
                if (sceneLoader == null) { PerPlayerLog.Warning("SceneLoader null"); return; }

                var homeContextType = GameReflection.TypeByName("HomePageContext");
                if (homeContextType == null) { PerPlayerLog.Warning("HomePageContext type null"); return; }

                var goToLanding = AccessTools.Method(sceneLoader.GetType(), "GoToLanding",
                    new[] { homeContextType, typeof(bool) });
                if (goToLanding == null) { PerPlayerLog.Warning("GoToLanding method null"); return; }

                var context = Activator.CreateInstance(homeContextType);

                // Set InitialBladeState = Challenge
                var bladeStateField = AccessTools.Field(homeContextType, "InitialBladeState");
                var pbcType = GameReflection.TypeByName("PlayBladeController");
                var statesType = pbcType?.GetNestedType("PlayBladeVisualStates");
                if (bladeStateField != null && statesType != null)
                {
                    bladeStateField.SetValue(context, Enum.Parse(statesType, "Challenge"));
                }

                // Set ChallengeId
                var challengeIdField = AccessTools.Field(homeContextType, "ChallengeId");
                if (challengeIdField != null)
                {
                    challengeIdField.SetValue(context, challengeId);
                }

                goToLanding.Invoke(sceneLoader, new object[] { context, true }); // forceReload=true
                PerPlayerLog.Info($"GoToLanding called with ChallengeId={challengeId}, InitialBladeState=Challenge");
            }
            catch (Exception ex)
            {
                PerPlayerLog.Warning($"NavigateToHomeWithChallengeContext failed: {ex.Message}");
            }
        }

        private static void NavigateToHomeSimple()
        {
            try
            {
                var sceneLoaderType = GameReflection.TypeByName("SceneLoader");
                var getLoaderMethod = AccessTools.Method(sceneLoaderType, "GetSceneLoader");
                var sceneLoader = getLoaderMethod.Invoke(null, null);
                if (sceneLoader == null) return;

                var homeContextType = GameReflection.TypeByName("HomePageContext");
                if (homeContextType == null) return;

                var goToLanding = AccessTools.Method(sceneLoader.GetType(), "GoToLanding",
                    new[] { homeContextType, typeof(bool) });
                if (goToLanding == null) return;

                var context = Activator.CreateInstance(homeContextType);
                goToLanding.Invoke(sceneLoader, new object[] { context, false });
                PerPlayerLog.Info("Simple GoToLanding called");
            }
            catch (Exception ex)
            {
                PerPlayerLog.Warning($"NavigateToHomeSimple failed: {ex.Message}");
            }
        }

        private static object GetChallengeDataFromController(Guid challengeId,
            out Type controllerType, out object controller)
        {
            controllerType = null;
            controller = null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "PVPChallengeController") { controllerType = t; break; }
                }
                if (controllerType != null) break;
            }

            if (controllerType == null)
            {
                PerPlayerLog.Warning("PVPChallengeController type not found in any assembly");
                return null;
            }

            // PVPChallengeController is a plain class (NOT MonoBehaviour) — FindObjectOfType won't work.
            // Access it via IChallengeCommunicationWrapper from Pantry instead.
            try
            {
                var pantryType = GameReflection.TypeByName("Pantry");
                Type commWrapperType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    foreach (var t in asm.GetTypes())
                        if (t.Name == "IChallengeCommunicationWrapper" && t.IsInterface)
                        { commWrapperType = t; break; }

                if (pantryType != null && commWrapperType != null)
                {
                    var getMethod = pantryType.GetMethod("Get").MakeGenericMethod(commWrapperType);
                    var commWrapper = getMethod.Invoke(null, null);
                    if (commWrapper != null)
                    {
                        // ChallengeCommunicationWrapper has a _challengeService field
                        var serviceField = AccessTools.Field(commWrapper.GetType(), "_challengeService");
                        if (serviceField != null)
                        {
                            controller = serviceField.GetValue(commWrapper);
                            PerPlayerLog.Info($"Got challenge service via Pantry: {controller?.GetType().Name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PerPlayerLog.Warning($"Failed to get controller via Pantry: {ex.Message}");
            }

            if (controller == null)
            {
                PerPlayerLog.Warning("PVPChallengeController instance not found via Pantry");
                return null;
            }

            // Try the _challengeDataProvider field path
            var providerField = AccessTools.Field(controllerType, "_challengeDataProvider");
            if (providerField != null)
            {
                var provider = providerField.GetValue(controller);
                if (provider != null)
                {
                    var getChallengeMethod = AccessTools.Method(provider.GetType(), "GetChallengeData",
                        new[] { typeof(Guid) });
                    if (getChallengeMethod != null)
                    {
                        var result = getChallengeMethod.Invoke(provider, new object[] { challengeId });
                        return result;
                    }
                }
            }

            // Fallback: direct method on controller
            var getDataMethod = AccessTools.Method(controllerType, "GetChallengeData",
                new[] { typeof(Guid) });
            if (getDataMethod == null)
            {
                PerPlayerLog.Warning("GetChallengeData(Guid) method not found");
                return null;
            }

            return getDataMethod.Invoke(controller, new object[] { challengeId });
        }

        private static bool TryOpenPlayBlade(Guid challengeId)
        {
            try
            {
                var socialUIType = GameReflection.TypeByName("SocialUI");
                if (socialUIType == null)
                {
                    PerPlayerLog.Warning("SocialUI type not found");
                    return false;
                }

                var instanceField = AccessTools.Field(socialUIType, "_instance");
                var socialUI = instanceField?.GetValue(null);
                if (socialUI == null)
                {
                    PerPlayerLog.Warning("SocialUI._instance is null");
                    return false;
                }

                var openMethod = AccessTools.Method(socialUIType, "OpenPlayBlade",
                    new[] { typeof(Guid) });
                if (openMethod == null)
                {
                    PerPlayerLog.Warning("SocialUI.OpenPlayBlade(Guid) not found");
                    return false;
                }

                openMethod.Invoke(socialUI, new object[] { challengeId });
                PerPlayerLog.Info("Called SocialUI.OpenPlayBlade successfully");
                return true;
            }
            catch (Exception ex)
            {
                PerPlayerLog.Warning($"TryOpenPlayBlade failed: {ex.Message}");
                return false;
            }
        }

        private static bool TryViewFriendChallengeDirect(Guid challengeId)
        {
            try
            {
                var sceneLoaderType = GameReflection.TypeByName("SceneLoader");
                var getLoaderMethod = AccessTools.Method(sceneLoaderType, "GetSceneLoader");
                var sceneLoader = getLoaderMethod.Invoke(null, null);
                if (sceneLoader == null) return false;

                var navContentProp = AccessTools.Property(sceneLoader.GetType(), "CurrentNavContent");
                var navContent = navContentProp?.GetValue(sceneLoader);
                if (navContent == null) return false;

                // Find ChallengeBladeController
                var prop = AccessTools.Property(navContent.GetType(), "ChallengeBladeController");
                object pbc = prop?.GetValue(navContent);

                if (pbc == null)
                {
                    // Try fields
                    foreach (var f in navContent.GetType().GetFields(
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Public))
                    {
                        if (f.Name.Contains("hallengeBlade") || f.Name.Contains("layBlade"))
                        {
                            pbc = f.GetValue(navContent);
                            if (pbc != null)
                            {
                                PerPlayerLog.Info($"Found PlayBladeController via field: {f.Name}");
                                break;
                            }
                        }
                    }
                }

                if (pbc == null)
                {
                    PerPlayerLog.Warning($"PlayBladeController not found on {navContent.GetType().Name}");
                    return false;
                }

                // Specify Guid overload — there's also a string overload we don't want
                var viewMethod = AccessTools.Method(pbc.GetType(), "ViewFriendChallenge", new[] { typeof(Guid) });
                if (viewMethod == null)
                {
                    PerPlayerLog.Warning("ViewFriendChallenge(Guid) method not found");
                    return false;
                }

                viewMethod.Invoke(pbc, new object[] { challengeId });
                PerPlayerLog.Info("Called ViewFriendChallenge directly");
                return true;
            }
            catch (Exception ex)
            {
                PerPlayerLog.Warning($"TryViewFriendChallengeDirect failed: {ex.Message}");
                return false;
            }
        }

        // --- UI Helpers ---

        private static GameObject CreateChild(Transform parent, string name)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            obj.AddComponent<RectTransform>();
            return obj;
        }

        private static void StretchFull(GameObject obj)
        {
            var rect = obj.GetComponent<RectTransform>() ?? obj.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
        }

        /// <summary>
        /// Builds a small vertical scrollbar (track + handle) pinned to the right
        /// edge of <paramref name="parent"/>. Mirrors the one in FormatComboBox.
        /// </summary>
        private static Scrollbar BuildVerticalScrollbar(Transform parent, float width)
        {
            var sbObj = CreateChild(parent, "Scrollbar");
            var sbRect = sbObj.GetComponent<RectTransform>();
            sbRect.anchorMin = new Vector2(1f, 0f);
            sbRect.anchorMax = new Vector2(1f, 1f);
            sbRect.pivot = new Vector2(1f, 1f);
            sbRect.sizeDelta = new Vector2(width, 0f);
            sbRect.anchoredPosition = Vector2.zero;
            sbObj.AddComponent<Image>().color = new Color(0.05f, 0.06f, 0.10f, 0.95f);

            var slidingArea = CreateChild(sbObj.transform, "Sliding Area");
            var saRect = slidingArea.GetComponent<RectTransform>();
            saRect.anchorMin = Vector2.zero;
            saRect.anchorMax = Vector2.one;
            saRect.offsetMin = new Vector2(2, 2);
            saRect.offsetMax = new Vector2(-2, -2);

            var handle = CreateChild(slidingArea.transform, "Handle");
            var hRect = handle.GetComponent<RectTransform>();
            hRect.anchorMin = Vector2.zero;
            hRect.anchorMax = Vector2.one;
            hRect.sizeDelta = Vector2.zero;
            var hImg = handle.AddComponent<Image>();
            hImg.color = new Color(0.55f, 0.60f, 0.75f, 0.95f);

            var scrollbar = sbObj.AddComponent<Scrollbar>();
            scrollbar.targetGraphic = hImg;
            scrollbar.handleRect = hRect;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            return scrollbar;
        }

        private static GameObject CreateButton(Transform parent, string name, string label,
            Vector2 anchorMin, Vector2 anchorMax)
        {
            var btn = CreateChild(parent, name);
            var rect = btn.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.sizeDelta = Vector2.zero;

            btn.AddComponent<Image>().color = new Color(0.2f, 0.3f, 0.5f, 0.9f);
            btn.AddComponent<Button>();

            var textObj = CreateChild(btn.transform, "Text");
            StretchFull(textObj);
            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 14;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;

            return btn;
        }

        // --- Failed Join Recovery Modal ---

        private static void ShowJoinFailedRecovery(string format)
        {
            int nameIdx = Array.IndexOf(ChallengeFormatState.FormatKeys, format);
            string formatDisplay = nameIdx >= 0 ? ChallengeFormatState.FormatOptions[nameIdx] : format;

            var modal = new GameObject("JoinFailedModal");
            UnityEngine.Object.DontDestroyOnLoad(modal);

            var canvas = modal.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 150;

            var scaler = modal.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            modal.AddComponent<GraphicRaycaster>();

            // Dark overlay
            var bg = CreateChild(modal.transform, "Bg");
            StretchFull(bg);
            bg.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.7f);

            // Dialog box
            var dialog = CreateChild(modal.transform, "Dialog");
            var dialogRect = dialog.GetComponent<RectTransform>();
            dialogRect.anchorMin = new Vector2(0.3f, 0.35f);
            dialogRect.anchorMax = new Vector2(0.7f, 0.65f);
            dialogRect.sizeDelta = Vector2.zero;
            dialog.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.18f, 0.98f);

            // Title
            var titleObj = CreateChild(dialog.transform, "Title");
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.05f, 0.7f);
            titleRect.anchorMax = new Vector2(0.95f, 0.95f);
            titleRect.sizeDelta = Vector2.zero;
            var titleTmp = titleObj.AddComponent<TextMeshProUGUI>();
            titleTmp.text = "Lobby Unavailable";
            titleTmp.fontSize = 24;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = Color.white;
            titleTmp.alignment = TextAlignmentOptions.Center;

            // Body
            var bodyObj = CreateChild(dialog.transform, "Body");
            var bodyRect = bodyObj.GetComponent<RectTransform>();
            bodyRect.anchorMin = new Vector2(0.08f, 0.35f);
            bodyRect.anchorMax = new Vector2(0.92f, 0.7f);
            bodyRect.sizeDelta = Vector2.zero;
            var bodyTmp = bodyObj.AddComponent<TextMeshProUGUI>();
            bodyTmp.text = format != "none"
                ? $"This lobby is no longer available.\nWould you like to create a new {formatDisplay} lobby?"
                : "This lobby is no longer available.\nWould you like to create a new lobby?";
            bodyTmp.fontSize = 16;
            bodyTmp.color = new Color(0.8f, 0.8f, 0.85f);
            bodyTmp.alignment = TextAlignmentOptions.Center;

            // Create New Lobby button
            var createBtn = CreateChild(dialog.transform, "CreateBtn");
            var createRect = createBtn.GetComponent<RectTransform>();
            createRect.anchorMin = new Vector2(0.1f, 0.08f);
            createRect.anchorMax = new Vector2(0.48f, 0.28f);
            createRect.sizeDelta = Vector2.zero;
            createBtn.AddComponent<Image>().color = new Color(0.2f, 0.5f, 0.3f, 0.9f);
            var createBtnComp = createBtn.AddComponent<Button>();

            var createTextObj = CreateChild(createBtn.transform, "Text");
            StretchFull(createTextObj);
            var createTmp = createTextObj.AddComponent<TextMeshProUGUI>();
            createTmp.text = "Create New Lobby";
            createTmp.fontSize = 15;
            createTmp.fontStyle = FontStyles.Bold;
            createTmp.color = Color.white;
            createTmp.alignment = TextAlignmentOptions.Center;

            var capturedFormat = format;
            var capturedModal = modal;
            createBtnComp.onClick.AddListener(new UnityAction(() =>
            {
                UnityEngine.Object.Destroy(capturedModal);
                CreateNewLobbyWithSettings(capturedFormat);
            }));

            // Cancel button
            var cancelBtn = CreateChild(dialog.transform, "CancelBtn");
            var cancelRect = cancelBtn.GetComponent<RectTransform>();
            cancelRect.anchorMin = new Vector2(0.52f, 0.08f);
            cancelRect.anchorMax = new Vector2(0.9f, 0.28f);
            cancelRect.sizeDelta = Vector2.zero;
            cancelBtn.AddComponent<Image>().color = new Color(0.4f, 0.2f, 0.2f, 0.9f);
            var cancelBtnComp = cancelBtn.AddComponent<Button>();

            var cancelTextObj = CreateChild(cancelBtn.transform, "Text");
            StretchFull(cancelTextObj);
            var cancelTmp = cancelTextObj.AddComponent<TextMeshProUGUI>();
            cancelTmp.text = "Cancel";
            cancelTmp.fontSize = 15;
            cancelTmp.color = Color.white;
            cancelTmp.alignment = TextAlignmentOptions.Center;

            var capturedModal2 = modal;
            cancelBtnComp.onClick.AddListener(new UnityAction(() =>
            {
                UnityEngine.Object.Destroy(capturedModal2);
                ChallengeFormatState.Reset();
            }));

            PerPlayerLog.Info($"Showing join failed recovery modal (format={format})");
        }

        private static void CreateNewLobbyWithSettings(string format)
        {
            PerPlayerLog.Info($"Creating new lobby with format={format}");
            ChallengeFormatState.SelectedFormat = format;
            ChallengeFormatState.IsJoining = false;
            ChallengeFormatState.InLobbySession = true;

            FirebaseClient.Instance.StartCoroutine(CreateLobbyAndNavigate(format));
        }

        private static IEnumerator CreateLobbyAndNavigate(string format)
        {
            // Step 1: Get PVPChallengeController (cached from ChallengeCreatePatch)
            var controller = Patches.ChallengeCreatePatch.CachedController;
            var controllerType = Patches.ChallengeCreatePatch.CachedControllerType;

            if (controller == null || controllerType == null)
            {
                PerPlayerLog.Warning("No cached PVPChallengeController — falling back to manual");
                Toast.Info($"Open Friends List > Challenge to create a new {format} lobby.");
                yield break;
            }

            // Step 2: Call CreateAndCacheChallenge()
            var createMethod = AccessTools.Method(controllerType, "CreateAndCacheChallenge");
            if (createMethod == null)
            {
                PerPlayerLog.Error("CreateAndCacheChallenge method not found");
                Toast.Info($"Open Friends List > Challenge to create a new {format} lobby.");
                yield break;
            }

            PerPlayerLog.Info("Calling CreateAndCacheChallenge...");
            Toast.Info("Creating lobby...");
            object promise;
            try
            {
                // CreateAndCacheChallenge(bool forceCreate = false)
                promise = createMethod.Invoke(controller, new object[] { false });
            }
            catch (Exception ex)
            {
                PerPlayerLog.Error($"CreateAndCacheChallenge failed: {ex}");
                Toast.Error("Failed to create lobby.");
                yield break;
            }

            // Step 3: Wait for promise
            var isDoneProp = AccessTools.Property(promise.GetType(), "IsDone");
            var successProp = AccessTools.Property(promise.GetType(), "Successful");
            var resultProp = AccessTools.Property(promise.GetType(), "Result");

            for (int i = 0; i < 30; i++)
            {
                yield return new WaitForSeconds(0.5f);
                if (isDoneProp != null && (bool)isDoneProp.GetValue(promise)) break;
            }

            bool success = successProp != null && (bool)successProp.GetValue(promise);
            if (!success)
            {
                PerPlayerLog.Error("CreateAndCacheChallenge promise failed");
                Toast.Error("Failed to create lobby.");
                yield break;
            }

            var challengeData = resultProp?.GetValue(promise);
            if (challengeData == null)
            {
                PerPlayerLog.Error("CreateAndCacheChallenge returned null");
                Toast.Error("Failed to create lobby.");
                yield break;
            }

            // Get the ChallengeId from the result
            var cidField = AccessTools.Field(challengeData.GetType(), "ChallengeId");
            if (cidField == null)
            {
                PerPlayerLog.Error("ChallengeId field not found on result");
                yield break;
            }

            var challengeId = (Guid)cidField.GetValue(challengeData);
            ChallengeFormatState.ActiveChallengeId = challengeId;
            PerPlayerLog.Info($"Challenge created: {challengeId}");

            // Step 4: Navigate to the challenge view via SocialUI.OpenPlayBlade(Guid)
            var socialUIType = GameReflection.TypeByName("SocialUI");
            if (socialUIType == null)
            {
                PerPlayerLog.Error("SocialUI type not found");
                yield break;
            }

            var instanceField = AccessTools.Field(socialUIType, "_instance");
            if (instanceField == null)
            {
                PerPlayerLog.Error("SocialUI._instance field not found");
                yield break;
            }

            var socialUI = instanceField.GetValue(null);
            if (socialUI == null)
            {
                PerPlayerLog.Error("SocialUI._instance is null");
                yield break;
            }

            // OpenPlayBlade(Guid challengeId)
            var openPlayBlade = AccessTools.Method(socialUIType, "OpenPlayBlade",
                new[] { typeof(Guid) });
            if (openPlayBlade == null)
            {
                PerPlayerLog.Error("SocialUI.OpenPlayBlade(Guid) not found");
                yield break;
            }

            PerPlayerLog.Info($"Calling SocialUI.OpenPlayBlade({challengeId})");
            openPlayBlade.Invoke(socialUI, new object[] { challengeId });

            int nameIdx = Array.IndexOf(ChallengeFormatState.FormatKeys, format);
            string formatDisplay = nameIdx >= 0 ? ChallengeFormatState.FormatOptions[nameIdx] : format;
            Toast.Success($"New {formatDisplay} lobby created!");
        }
    }
}
