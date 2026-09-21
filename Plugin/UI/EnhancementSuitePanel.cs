using System;
using MTGAEnhancementSuite.State;
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

        // Barra delle schede. Ne resta una sola, Impostazioni: la barra e' stata
        // mantenuta di proposito perche' in futuro potrebbero aggiungersene altre.
        private static GameObject _settingsContent;
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

            // Barra delle schede — una sola scheda, Impostazioni.
            _settingsTabBtn = CreateButton(content.transform, "SettingsTab", "Settings",
                new Vector2(0.52f, 0.93f), new Vector2(0.65f, 0.99f));
            _settingsTabBtn.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.25f, 0.9f); // inactive
            _settingsTabBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(() => SwitchTab(true)));

            // "Replays..." - non e' una scheda, e i puntini lo dicono:
            // apre una finestra sua e chiude questo pannello.
            //
            // Sta QUI e non fra le righe delle impostazioni per una ragione
            // precisa: quelle righe hanno ancore scritte a mano, la scala e'
            // gia' stata ricalcolata una volta per farci stare la sesta, e una
            // settima stringerebbe tutte le descrizioni. Rifare quella scala
            // senza poter guardare il pannello in gioco e' il modo di
            // consegnare descrizioni tagliate.
            var replaysBtn = CreateButton(content.transform, "ReplaysBtn", "Replays...",
                new Vector2(0.665f, 0.93f), new Vector2(0.80f, 0.99f));
            replaysBtn.GetComponent<Image>().color = new Color(0.30f, 0.50f, 0.70f, 0.9f);
            replaysBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(() =>
            {
                Close();
                try { ReplayBrowserWindow.Show(); }
                catch (Exception ex)
                {
                    PerPlayerLog.Error($"ReplayBrowserWindow.Show failed: {ex}");
                    Toast.Error("Could not open the replay list - see the log");
                }
            }));

            // Close button
            var closeBtn = CreateButton(content.transform, "CloseBtn", "X",
                new Vector2(0.92f, 0.93f), new Vector2(0.98f, 0.99f));
            closeBtn.GetComponent<Image>().color = new Color(0.6f, 0.2f, 0.2f, 0.9f);
            closeBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(Close));

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
                new Vector2(0.05f, 0.755f), new Vector2(0.95f, 0.850f),
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
                new Vector2(0.05f, 0.646f), new Vector2(0.95f, 0.741f),
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
                new Vector2(0.05f, 0.537f), new Vector2(0.95f, 0.632f),
                ModSettings.Instance.ForceSimplifiedCards,
                (val) =>
                {
                    ModSettings.Instance.ForceSimplifiedCards = val;
                    ModSettings.Instance.Save();
                    Toast.Info(val ? "Simplified cards on" : "Simplified cards off");
                });

            // Hide cards already maxed out in the open deck. The rule is
            // MTGA's own "can I still add this card?" check, so it follows the
            // format limit (4, or 1 in Brawl) and keeps basic lands and
            // any-number cards visible. See Patches/HideDeckCardsPatch.cs.
            CreateToggle(_settingsContent.transform, "HideDeckCardsToggle",
                "Hide Cards Already In Deck",
                "In the deck editor, hides cards the open deck already has the maximum of (4, or 1 in Brawl). Basic lands and any-number cards stay. Constructed only.",
                new Vector2(0.05f, 0.428f), new Vector2(0.95f, 0.523f),
                ModSettings.Instance.HideCardsInDeck,
                (val) =>
                {
                    ModSettings.Instance.HideCardsInDeck = val;
                    ModSettings.Instance.Save();
                    // Se il deck editor e' aperto dietro al pannello, la lista
                    // si ridisegna subito invece che al primo filtro toccato.
                    Patches.HideDeckCardsPatch.RefreshPoolNow();
                    Toast.Info(val ? "Cards in deck hidden" : "Cards in deck shown");
                });

            // Export collection — opens a Windows save dialog and writes the
            // owned cards in MTGA deck-list format. Useful for backing up
            // the collection or pasting into deckbuilders.
            CreateActionRow(_settingsContent.transform, "ExportCollectionRow",
                "Export Collection",
                "Save your owned cards to a text file (MTGA deck format) — for sharing with deckbuilders or backing up your collection.",
                // Risalita di un passo (0.109) il 2026-09-21, quando la riga
                // sopra e' stata tolta: le ancore sono a mano, quindi togliere
                // una riga in mezzo lascia un buco se non si spostano quelle
                // sotto. Adesso le righe sono CINQUE, dalla cima 0.850 fino a
                // 0.319.
                new Vector2(0.05f, 0.319f), new Vector2(0.95f, 0.414f),
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

            // Il pannello si apre sulle Impostazioni, unica scheda rimasta.
            SwitchTab(true);

            _panelRoot.SetActive(false);
            PerPlayerLog.Info("MTGA+ panel created");
        }

        /// <summary>
        /// Mostra una scheda del pannello. Oggi ce n'e' una sola, Impostazioni:
        /// la barra e' stata mantenuta apposta (decisione D1) perche' in futuro
        /// potrebbero aggiungersene altre, e il parametro serve a non dover
        /// riscrivere la firma quando succedera'.
        /// </summary>
        private static void SwitchTab(bool showSettings)
        {
            if (_settingsContent != null) _settingsContent.SetActive(showSettings);

            if (_settingsTabBtn != null)
                _settingsTabBtn.GetComponent<Image>().color = showSettings
                    ? new Color(0.3f, 0.5f, 0.7f, 0.9f)
                    : new Color(0.15f, 0.15f, 0.25f, 0.9f);
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
            // Dall'alto e a capo automatico: senza, una descrizione lunga
            // esce dal riquadro e finisce fuori dalla finestra (visto il
            // 2026-09-11 con la riga del contatore di combattimento). Il
            // ritorno a capo non e' attivo d'ufficio: dipende dalle
            // TMP_Settings del gioco, quindi va chiesto esplicitamente.
            descTmp.textWrappingMode = TextWrappingModes.Normal;
            descTmp.alignment = TextAlignmentOptions.TopLeft;

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
            // Dall'alto e a capo automatico: senza, una descrizione lunga
            // esce dal riquadro e finisce fuori dalla finestra (visto il
            // 2026-09-11 con la riga del contatore di combattimento). Il
            // ritorno a capo non e' attivo d'ufficio: dipende dalle
            // TMP_Settings del gioco, quindi va chiesto esplicitamente.
            descTmp.textWrappingMode = TextWrappingModes.Normal;
            descTmp.alignment = TextAlignmentOptions.TopLeft;

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

    }
}
