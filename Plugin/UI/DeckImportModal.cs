using System;
using System.Collections.Generic;
using MTGAEnhancementSuite.Features;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// La finestra di "add from another deck", in due schermate dentro lo
    /// stesso riquadro:
    ///
    ///   1. l'elenco dei tuoi mazzi, con ricerca per nome e filtro per colore —
    ///      doppio click su uno per aprirlo;
    ///   2. le sue carte, ognuna con quante copie ne hai gia' nel mazzo che
    ///      stai costruendo, da spuntare e aggiungere.
    ///
    /// La colonna del confronto e' contata **per nome** e non per stampa
    /// (<c>GetQuantityInMainDeckByTitle</c>): se nel tuo mazzo hai una stampa
    /// vecchia e nell'altro c'e' quella nuova, e' la stessa carta e il
    /// confronto deve dirlo.
    ///
    /// **Il riquadro e' due oggetti annidati, non uno.** Fuori <c>Frame</c>,
    /// tinto d'accento; dentro <c>Dialog</c>, rientrato di 2px e col fondo
    /// scuro. In Unity i figli si disegnano SOPRA il padre, quindi un "bordo"
    /// messo come figlio del pannello non lo contorna: lo ricopre tutto. E'
    /// successo nella prima versione (2026-09-19) e il risultato era il testo
    /// dei suggerimenti illeggibile su una fascia azzurra.
    ///
    /// Cambiare ricerca, colori o formato **non ricostruisce la finestra**: si
    /// rifanno solo le righe della lista (<see cref="RebuildDeckRows"/>).
    /// Ricostruire tutto azzererebbe il campo di ricerca a ogni lettera.
    /// </summary>
    internal static class DeckImportModal
    {
        // --- palette condivisa con gli altri pannelli MTGA+ ---
        private static readonly Color PanelBackground = new Color(0.10f, 0.10f, 0.18f, 0.98f);
        private static readonly Color RowBackground   = new Color(0.15f, 0.15f, 0.25f, 1f);
        private static readonly Color FieldBackground = new Color(0.18f, 0.18f, 0.28f, 1f);
        private static readonly Color AccentBlue      = new Color(0.30f, 0.50f, 0.70f, 1f);
        private static readonly Color TextPrimary     = new Color(0.85f, 0.85f, 0.90f);
        private static readonly Color TextSecondary   = new Color(0.62f, 0.62f, 0.72f);
        private static readonly Color TextDisabled    = new Color(0.45f, 0.45f, 0.55f);
        private static readonly Color ButtonIdle      = new Color(0.20f, 0.20f, 0.30f, 1f);

        // Colori del confronto: verde = non ce l'hai, ambra = ne hai meno,
        // grigio = ne hai gia' almeno quante ne ha l'altro mazzo.
        private static readonly Color DiffMissing = new Color(0.45f, 0.82f, 0.55f);
        private static readonly Color DiffPartial = new Color(0.95f, 0.78f, 0.35f);
        private static readonly Color DiffSame    = TextDisabled;

        private const float RowHeight = 40f;
        private const string ModalName = "MTGAES_DeckImportModal";

        /// <summary>Doppio click: due click sulla stessa riga entro questo tempo.</summary>
        private const float DoubleClickSeconds = 0.4f;

        /// <summary>
        /// I cinque colori, con il glifo e la tinta con cui si scrivono. Il
        /// nero di Magic su fondo scuro non si vedrebbe: qui e' un lilla.
        /// </summary>
        private static readonly CardColorEntry[] ManaColors =
        {
            new CardColorEntry(CardColorFlags.White, "W", "EFE7CB"),
            new CardColorEntry(CardColorFlags.Blue,  "U", "4A90D9"),
            new CardColorEntry(CardColorFlags.Black, "B", "A98FC0"),
            new CardColorEntry(CardColorFlags.Red,   "R", "D9534F"),
            new CardColorEntry(CardColorFlags.Green, "G", "58B368"),
        };

        private struct CardColorEntry
        {
            public readonly CardColorFlags Flag;
            public readonly string Letter;
            public readonly string Hex;

            public CardColorEntry(CardColorFlags flag, string letter, string hex)
            {
                Flag = flag; Letter = letter; Hex = hex;
            }
        }

        // --- stato della finestra aperta ---
        private static GameObject _root;
        private static Transform _listContent;
        private static List<DeckImportManager.SourceDeck> _decks;
        private static List<DeckImportManager.SourceCard> _cards;
        private static string _openDeckName;
        private static TextMeshProUGUI _addButtonLabel;
        private static TextMeshProUGUI _hintLabel;
        private static TextMeshProUGUI _formatButtonLabel;
        private static Image _formatButtonImage;
        private static Guid _lastClickedDeck;
        private static float _lastClickTime;

        // --- filtri, vivono per la sessione ---
        private static bool _showAllFormats;
        private static string _search = "";
        private static CardColorFlags _colorFilter = CardColorFlags.None;
        private static readonly Dictionary<CardColorFlags, Image> _colorButtons =
            new Dictionary<CardColorFlags, Image>();

        // -----------------------------------------------------------------
        // Ingresso
        // -----------------------------------------------------------------
        public static void Show()
        {
            if (!DeckCardInserter.IsDeckBuilderReady())
            {
                Toast.Warning("Import: no deck open");
                return;
            }

            if (!DeckImportManager.ContextAllowsImport())
            {
                // Draft, sealed, sideboarding e sola lettura: li' il pool non
                // sono le carte possedute e ogni inserimento verrebbe comunque
                // rifiutato. Meglio dirlo subito che aprire una finestra che
                // non puo' funzionare.
                Toast.Warning("Import: only available while building a constructed deck");
                return;
            }

            _decks = DeckImportManager.ListDecks();
            _cards = null;
            _search = "";
            _lastClickedDeck = Guid.Empty;
            ShowDeckList();
        }

        public static void Close()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _listContent = null;
            _decks = null;
            _cards = null;
            _addButtonLabel = null;
            _hintLabel = null;
            _formatButtonLabel = null;
            _formatButtonImage = null;
            _colorButtons.Clear();
        }

        // -----------------------------------------------------------------
        // Schermata 1: i mazzi
        // -----------------------------------------------------------------
        private static void ShowDeckList()
        {
            // Il formato del mazzo aperto si legge una volta sola qui: dentro
            // il ciclo sarebbe una lookup nel Pantry per ogni mazzo.
            string openFormat = DeckImportManager.OpenDeckFormat();

            int sameFormatCount = 0;
            foreach (var deck in _decks)
                if (DeckImportManager.MatchesFormat(deck, openFormat)) sameFormatCount++;

            // Se il filtro sul formato non lascerebbe NIENTE ma dei mazzi ci
            // sono, accendiamo da soli "tutti i formati".
            //
            // Non e' una comodita': e' la rete sotto un confronto che non ho
            // potuto provare. Il formato del mazzo aperto arriva da
            // DeckBuilderModel._deckFormat (cioe' da DeckInfo.format, oppure dal
            // formato del contesto quando ce n'e' uno), quello dei mazzi salvati
            // da Client_DeckSummary.Attributes["Format"]: due campi diversi, che
            // credo contengano la stessa stringa ma che non ho verificato a
            // runtime. Se non combaciassero, senza questa riga la finestra si
            // aprirebbe vuota e sembrerebbe rotta.
            if (sameFormatCount == 0 && _decks.Count > 0 && !_showAllFormats)
            {
                _showAllFormats = true;
                Plugin.Log.LogInfo(
                    $"DeckImport: nessuno dei {_decks.Count} mazzi e' nel formato " +
                    $"'{openFormat ?? "?"}': mostro tutti i formati da solo.");
            }

            var dialog = BeginModal("Add from another deck", 0.155f, 0.785f, out var footer);

            BuildSearchRow(dialog);
            BuildColorRow(dialog);

            TextMeshProUGUI formatLabel;
            _formatButtonImage = MakeButton(footer, "", out formatLabel,
                new Vector2(0.00f, 0f), new Vector2(0.62f, 1f), ButtonIdle,
                () => { _showAllFormats = !_showAllFormats; RebuildDeckRows(); });
            _formatButtonLabel = formatLabel;

            TextMeshProUGUI ignored;
            MakeButton(footer, "Close", out ignored,
                new Vector2(0.66f, 0f), new Vector2(1f, 1f),
                new Color(0.25f, 0.25f, 0.32f, 1f),
                Close);

            SetHint("Double-click a deck to see its cards.");
            RebuildDeckRows();
        }

        /// <summary>
        /// Rifa' solo le righe della lista. Il campo di ricerca e i pulsanti
        /// restano quelli: e' il motivo per cui si puo' scrivere senza che la
        /// finestra si ricostruisca sotto le dita a ogni lettera.
        /// </summary>
        private static void RebuildDeckRows()
        {
            if (_listContent == null || _decks == null) return;

            ClearChildren(_listContent);

            string openFormat = DeckImportManager.OpenDeckFormat();
            string needle = (_search ?? "").Trim();

            int hiddenByFormat = 0, shown = 0;

            foreach (var deck in _decks)
            {
                bool sameFormat = DeckImportManager.MatchesFormat(deck, openFormat);
                if (!sameFormat) hiddenByFormat++;
                if (!sameFormat && !_showAllFormats) continue;

                if (needle.Length > 0 &&
                    (deck.Name == null ||
                     deck.Name.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) < 0)) continue;

                // Il filtro sui colori chiede "che contenga almeno questi":
                // scegliendo R e G escono Gruul, Jund, Naya... Chiedere
                // "esattamente questi" farebbe sparire proprio i mazzi che si
                // cercano, quando di un mazzo si ricorda meta' dei colori.
                if (_colorFilter != CardColorFlags.None &&
                    (deck.Colors & _colorFilter) != _colorFilter) continue;

                MakeDeckRow(_listContent, deck);
                shown++;
            }

            if (shown == 0)
            {
                string why;
                if (_decks.Count == 0) why = "No other decks to import from.";
                else if (needle.Length > 0) why = $"No deck matches \"{needle}\".";
                else if (_colorFilter != CardColorFlags.None) why = "No deck in those colors.";
                else why = "No decks in this format - turn on \"show all formats\" below.";
                MakeNotice(_listContent, why);
            }

            if (_formatButtonLabel != null)
            {
                if (_showAllFormats)
                    _formatButtonLabel.text = "[x] showing all formats - cards may be illegal here";
                else if (hiddenByFormat == 0)
                    _formatButtonLabel.text = "all your decks are in this format";
                else
                    _formatButtonLabel.text = $"[ ] show all formats ({hiddenByFormat} more deck(s))";
            }
            if (_formatButtonImage != null)
                _formatButtonImage.color = _showAllFormats ? new Color(0.45f, 0.35f, 0.18f, 1f) : ButtonIdle;
        }

        private static void BuildSearchRow(Transform dialog)
        {
            var holder = NewChild(dialog, "SearchRow");
            var rt = holder.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.03f, 0.865f);
            rt.anchorMax = new Vector2(0.97f, 0.925f);
            rt.sizeDelta = Vector2.zero;
            holder.AddComponent<Image>().color = FieldBackground;

            var input = holder.AddComponent<TMP_InputField>();

            var textArea = NewChild(holder.transform, "TextArea");
            StretchFull(textArea);
            textArea.AddComponent<RectMask2D>();

            var textGO = NewChild(textArea.transform, "Text");
            StretchFull(textGO);
            var textTmp = textGO.AddComponent<TextMeshProUGUI>();
            textTmp.fontSize = 17;
            textTmp.color = TextPrimary;
            textTmp.alignment = TextAlignmentOptions.MidlineLeft;
            textTmp.margin = new Vector4(12, 0, 8, 0);
            ApplyFont(textTmp);
            input.textComponent = textTmp;
            input.textViewport = textArea.GetComponent<RectTransform>();

            var ph = NewChild(textArea.transform, "Placeholder");
            StretchFull(ph);
            var phTmp = ph.AddComponent<TextMeshProUGUI>();
            phTmp.text = "Search decks by name...";
            phTmp.fontSize = 17;
            phTmp.color = TextDisabled;
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.alignment = TextAlignmentOptions.MidlineLeft;
            phTmp.margin = new Vector4(12, 0, 8, 0);
            ApplyFont(phTmp);
            input.placeholder = phTmp;

            input.text = _search ?? "";
            input.onValueChanged.AddListener(new UnityAction<string>(s =>
            {
                _search = s;
                RebuildDeckRows();
            }));

            // Il cursore di TMP non si disegna sui campi costruiti a runtime:
            // il progetto ne ha gia' uno di scorta, lo stesso che usa la
            // rinomina delle cartelle.
            BlinkingCaret.Attach(input);
            input.ActivateInputField();
        }

        private static void BuildColorRow(Transform dialog)
        {
            _colorButtons.Clear();

            var holder = NewChild(dialog, "ColorRow");
            var rt = holder.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.03f, 0.795f);
            rt.anchorMax = new Vector2(0.97f, 0.855f);
            rt.sizeDelta = Vector2.zero;

            MakeLabel(holder.transform, "Colors", TextSecondary, 15, TextAlignmentOptions.MidlineLeft,
                new Vector2(0f, 0f), new Vector2(0.14f, 1f), new Vector4(4, 0, 4, 0));

            for (int i = 0; i < ManaColors.Length; i++)
            {
                var entry = ManaColors[i];
                float x0 = 0.15f + i * 0.075f;
                bool on = (_colorFilter & entry.Flag) != 0;
                var flag = entry.Flag;

                TextMeshProUGUI ignored;
                var img = MakeButton(holder.transform,
                    "<color=#" + entry.Hex + ">" + entry.Letter + "</color>", out ignored,
                    new Vector2(x0, 0f), new Vector2(x0 + 0.065f, 1f),
                    on ? AccentBlue : ButtonIdle,
                    () => ToggleColor(flag));

                _colorButtons[flag] = img;
            }

            TextMeshProUGUI ignored2;
            MakeButton(holder.transform, "any", out ignored2,
                new Vector2(0.535f, 0f), new Vector2(0.635f, 1f), ButtonIdle,
                () =>
                {
                    _colorFilter = CardColorFlags.None;
                    RefreshColorButtons();
                    RebuildDeckRows();
                });
        }

        private static void ToggleColor(CardColorFlags flag)
        {
            if ((_colorFilter & flag) != 0) _colorFilter &= ~flag;
            else _colorFilter |= flag;
            RefreshColorButtons();
            RebuildDeckRows();
        }

        private static void RefreshColorButtons()
        {
            foreach (var pair in _colorButtons)
            {
                if (pair.Value == null) continue;
                pair.Value.color = (_colorFilter & pair.Key) != 0 ? AccentBlue : ButtonIdle;
            }
        }

        private static void MakeDeckRow(Transform parent, DeckImportManager.SourceDeck deck)
        {
            var row = MakeRow(parent, RowBackground);

            var btn = row.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(new UnityAction(() => OnDeckClicked(deck)));

            MakeLabel(row.transform, deck.Name, TextPrimary, 18, TextAlignmentOptions.MidlineLeft,
                new Vector2(0f, 0f), new Vector2(0.55f, 1f), new Vector4(14, 0, 6, 0));

            MakeLabel(row.transform, ColorPips(deck.Colors), TextSecondary, 17, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.55f, 0f), new Vector2(0.66f, 1f), new Vector4(0, 0, 4, 0));

            MakeLabel(row.transform, deck.Format ?? "", TextSecondary, 14, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.66f, 0f), new Vector2(0.92f, 1f), new Vector4(0, 0, 6, 0));

            MakeLabel(row.transform, deck.MainDeckSize.ToString(), TextSecondary, 16, TextAlignmentOptions.MidlineRight,
                new Vector2(0.92f, 0f), new Vector2(1f, 1f), new Vector4(0, 0, 14, 0));
        }

        /// <summary>Le lettere WUBRG del mazzo, ognuna della sua tinta.</summary>
        private static string ColorPips(CardColorFlags colors)
        {
            if (colors == CardColorFlags.None) return "<color=#8080A0>C</color>";
            var sb = new System.Text.StringBuilder();
            foreach (var entry in ManaColors)
                if ((colors & entry.Flag) != 0)
                    sb.Append("<color=#").Append(entry.Hex).Append(">").Append(entry.Letter).Append("</color>");
            return sb.ToString();
        }

        /// <summary>
        /// Doppio click come da richiesta: il primo click arma, il secondo sulla
        /// stessa riga entro <see cref="DoubleClickSeconds"/> apre. Tempo non
        /// scalato, cosi' il fast-forward F7 (che cambia Time.timeScale) non
        /// rende il doppio click impossibile da fare.
        /// </summary>
        private static void OnDeckClicked(DeckImportManager.SourceDeck deck)
        {
            float now = Time.unscaledTime;
            bool isDouble = deck.Id == _lastClickedDeck && (now - _lastClickTime) <= DoubleClickSeconds;
            _lastClickedDeck = deck.Id;
            _lastClickTime = now;

            if (!isDouble) return;

            _openDeckName = deck.Name;
            _cards = DeckImportManager.ReadDeck(deck.Id);

            if (_cards.Count == 0)
            {
                Toast.Warning($"'{deck.Name}' has no readable cards");
                return;
            }

            ShowCardList();
        }

        // -----------------------------------------------------------------
        // Schermata 2: le carte
        // -----------------------------------------------------------------
        private static void ShowCardList()
        {
            BeginModal($"From: {_openDeckName}", 0.155f, 0.925f, out var footer);

            foreach (var card in _cards)
                MakeCardRow(_listContent, card);

            TextMeshProUGUI ignored;

            MakeButton(footer, "< Back", out ignored,
                new Vector2(0f, 0f), new Vector2(0.20f, 1f),
                new Color(0.25f, 0.25f, 0.32f, 1f),
                ShowDeckList);

            MakeButton(footer, "Select missing", out ignored,
                new Vector2(0.23f, 0f), new Vector2(0.48f, 1f), ButtonIdle,
                SelectMissing);

            MakeButton(footer, "None", out ignored,
                new Vector2(0.51f, 0f), new Vector2(0.63f, 1f), ButtonIdle,
                SelectNone);

            TextMeshProUGUI addLabel;
            MakeButton(footer, AddButtonText(), out addLabel,
                new Vector2(0.66f, 0f), new Vector2(1f, 1f),
                new Color(0.20f, 0.45f, 0.30f, 1f),
                () =>
                {
                    // La lista serve dopo la chiusura: la copiamo, cosi'
                    // Close() puo' azzerare lo stato senza sfilarcela di mano.
                    var chosen = _cards;
                    string name = _openDeckName;
                    Close();
                    DeckImportManager.AddSelected(chosen, name);
                });
            _addButtonLabel = addLabel;

            SetHint("Green = not in your deck  |  Amber = you have fewer  |  Grey = already covered");
        }

        private static void MakeCardRow(Transform parent, DeckImportManager.SourceCard card)
        {
            var row = MakeRow(parent, RowBackground);

            // --- casella ---
            var box = NewChild(row.transform, "Check");
            var boxRt = box.GetComponent<RectTransform>();
            boxRt.anchorMin = new Vector2(0f, 0.5f);
            boxRt.anchorMax = new Vector2(0f, 0.5f);
            boxRt.pivot = new Vector2(0f, 0.5f);
            boxRt.sizeDelta = new Vector2(22f, 22f);
            boxRt.anchoredPosition = new Vector2(12f, 0f);
            var boxImg = box.AddComponent<Image>();
            boxImg.color = card.Selected ? AccentBlue : FieldBackground;

            var tick = NewChild(box.transform, "Tick");
            StretchFull(tick);
            var tickImg = tick.AddComponent<Image>();
            var checkSprite = IconLoader.Get("check");
            if (checkSprite != null)
            {
                tickImg.sprite = checkSprite;
                tickImg.preserveAspect = true;
            }
            // Senza l'icona resta il riquadro pieno bianco, che comunica lo
            // stesso acceso/spento: non lasciamo la riga senza segnale.
            tickImg.color = Color.white;
            tickImg.raycastTarget = false;
            tick.SetActive(card.Selected);

            // Il colore del nome dice a colpo d'occhio cosa manca, senza dover
            // leggere la colonna di destra riga per riga.
            Color nameColor;
            string diffText;
            if (card.InOpenDeck == 0)
            {
                nameColor = DiffMissing;
                diffText = $"not in deck   (+{card.Missing})";
            }
            else if (card.Missing > 0)
            {
                nameColor = DiffPartial;
                diffText = $"in deck {card.InOpenDeck}   (+{card.Missing})";
            }
            else
            {
                nameColor = DiffSame;
                diffText = $"in deck {card.InOpenDeck}";
            }

            MakeLabel(row.transform, card.Name, nameColor, 18, TextAlignmentOptions.MidlineLeft,
                new Vector2(0f, 0f), new Vector2(0.58f, 1f), new Vector4(44, 0, 6, 0));

            // Quante ne ha il mazzo di origine.
            MakeLabel(row.transform, $"x{card.InSource}", TextSecondary, 17, TextAlignmentOptions.MidlineRight,
                new Vector2(0.58f, 0f), new Vector2(0.68f, 1f), new Vector4(0, 0, 8, 0));

            // Quante ne hai tu, e quante ne entrerebbero davvero.
            MakeLabel(row.transform, diffText, nameColor, 15, TextAlignmentOptions.MidlineRight,
                new Vector2(0.68f, 0f), new Vector2(1f, 1f), new Vector4(0, 0, 14, 0));

            var btn = row.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(new UnityAction(() =>
            {
                card.Selected = !card.Selected;
                boxImg.color = card.Selected ? AccentBlue : FieldBackground;
                tick.SetActive(card.Selected);
                RefreshAddButton();
            }));
        }

        /// <summary>Spunta le carte che al mazzo aperto mancano davvero.</summary>
        private static void SelectMissing()
        {
            if (_cards == null) return;
            foreach (var c in _cards) c.Selected = c.Missing > 0;
            ShowCardList();
        }

        private static void SelectNone()
        {
            if (_cards == null) return;
            foreach (var c in _cards) c.Selected = false;
            ShowCardList();
        }

        /// <summary>
        /// Il pulsante conta le COPIE che entrano davvero, non le righe
        /// spuntate: con 4 copie nell'origine e 3 nel mazzo aperto ne entra
        /// una sola, e il numero sul pulsante deve essere quello che poi ci
        /// si ritrova nel mazzo.
        /// </summary>
        private static string AddButtonText()
        {
            uint n = DeckImportManager.SelectedCopies(_cards);
            return n == 1 ? "Add 1 copy" : $"Add {n} copies";
        }

        private static void RefreshAddButton()
        {
            if (_addButtonLabel != null) _addButtonLabel.text = AddButtonText();
        }

        // -----------------------------------------------------------------
        // Impalcatura comune alle due schermate
        // -----------------------------------------------------------------

        /// <summary>
        /// Distrugge il riquadro precedente e ne costruisce uno vuoto.
        /// Restituisce il pannello interno; la lista scorrevole finisce in
        /// <see cref="_listContent"/> e <paramref name="footer"/> e' la
        /// striscia dei pulsanti in fondo.
        /// </summary>
        private static Transform BeginModal(string title, float listBottom, float listTop, out Transform footer)
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _listContent = null;
            _addButtonLabel = null;
            _hintLabel = null;
            _formatButtonLabel = null;
            _formatButtonImage = null;
            _colorButtons.Clear();

            _root = new GameObject(ModalName);
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Sopra PresetMenu (225), da cui questa finestra viene aperta.
            canvas.sortingOrder = 240;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            _root.AddComponent<GraphicRaycaster>();

            var bg = NewChild(_root.transform, "Backdrop");
            StretchFull(bg);
            bg.AddComponent<Image>().color = new Color(0, 0, 0, 0.7f);
            var bgBtn = bg.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(Close));

            // Cornice e pannello sono DUE oggetti annidati: vedi il commento in
            // testa alla classe. Un bordo figlio del pannello lo ricoprirebbe.
            var frame = NewChild(_root.transform, "Frame");
            var frRt = frame.GetComponent<RectTransform>();
            frRt.anchorMin = new Vector2(0.26f, 0.10f);
            frRt.anchorMax = new Vector2(0.74f, 0.90f);
            frRt.sizeDelta = Vector2.zero;
            frame.AddComponent<Image>().color = AccentBlue;

            var dialog = NewChild(frame.transform, "Dialog");
            var dRt = dialog.GetComponent<RectTransform>();
            dRt.anchorMin = Vector2.zero;
            dRt.anchorMax = Vector2.one;
            dRt.offsetMin = new Vector2(2f, 2f);
            dRt.offsetMax = new Vector2(-2f, -2f);
            dialog.AddComponent<Image>().color = PanelBackground;

            var titleGO = NewChild(dialog.transform, "Title");
            var tRt = titleGO.GetComponent<RectTransform>();
            tRt.anchorMin = new Vector2(0.03f, 0.935f);
            tRt.anchorMax = new Vector2(0.97f, 0.99f);
            tRt.sizeDelta = Vector2.zero;
            var titleTmp = titleGO.AddComponent<TextMeshProUGUI>();
            titleTmp.text = title;
            titleTmp.fontSize = 23;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = Color.white;
            titleTmp.alignment = TextAlignmentOptions.MidlineLeft;
            titleTmp.overflowMode = TextOverflowModes.Ellipsis;
            titleTmp.raycastTarget = false;
            ApplyFont(titleTmp);

            // --- lista scorrevole ---
            var listArea = NewChild(dialog.transform, "ListArea");
            var lRt = listArea.GetComponent<RectTransform>();
            lRt.anchorMin = new Vector2(0.03f, listBottom);
            lRt.anchorMax = new Vector2(0.97f, listTop);
            lRt.sizeDelta = Vector2.zero;
            var scroll = listArea.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.scrollSensitivity = 28f;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            var viewport = NewChild(listArea.transform, "Viewport");
            StretchFull(viewport);
            viewport.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            viewport.AddComponent<RectMask2D>();
            scroll.viewport = viewport.GetComponent<RectTransform>();

            var content = NewChild(viewport.transform, "Content");
            var cRt = content.GetComponent<RectTransform>();
            cRt.anchorMin = new Vector2(0f, 1f);
            cRt.anchorMax = new Vector2(1f, 1f);
            cRt.pivot = new Vector2(0.5f, 1f);
            cRt.sizeDelta = Vector2.zero;
            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4;
            vlg.padding = new RectOffset(6, 6, 6, 6);
            vlg.childForceExpandWidth = true;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandHeight = false;
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = cRt;
            _listContent = content.transform;

            // --- riga del suggerimento, fissa: fuori dalla lista scorrevole,
            //     altrimenti la legenda dei colori si legge solo dopo aver
            //     scorso fino in fondo, cioe' quando non serve piu'. ---
            var hintGO = NewChild(dialog.transform, "Hint");
            var hRt = hintGO.GetComponent<RectTransform>();
            hRt.anchorMin = new Vector2(0.03f, 0.105f);
            hRt.anchorMax = new Vector2(0.97f, 0.150f);
            hRt.sizeDelta = Vector2.zero;
            _hintLabel = MakeLabel(hintGO.transform, "", TextSecondary, 14,
                TextAlignmentOptions.Center, Vector2.zero, Vector2.one, new Vector4(8, 0, 8, 0));

            // --- striscia dei pulsanti ---
            var footerGO = NewChild(dialog.transform, "Footer");
            var fRt = footerGO.GetComponent<RectTransform>();
            fRt.anchorMin = new Vector2(0.03f, 0.02f);
            fRt.anchorMax = new Vector2(0.97f, 0.09f);
            fRt.sizeDelta = Vector2.zero;
            footer = footerGO.transform;

            return dialog.transform;
        }

        /// <summary>
        /// Svuota un contenitore. Lo spegnimento serve subito: Destroy agisce a
        /// fine frame, e un VerticalLayoutGroup conta solo i figli attivi —
        /// senza SetActive(false) le righe vecchie resterebbero nel calcolo
        /// dell'altezza per un frame, e la lista salterebbe a ogni lettera
        /// digitata nella ricerca.
        /// </summary>
        private static void ClearChildren(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }

        private static GameObject MakeRow(Transform parent, Color background)
        {
            var row = NewChild(parent, "Row");
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = RowHeight;
            le.minHeight = RowHeight;
            row.AddComponent<Image>().color = background;
            return row;
        }

        private static void MakeNotice(Transform parent, string text)
        {
            var row = NewChild(parent, "Notice");
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = RowHeight * 2f;
            le.minHeight = RowHeight * 2f;
            MakeLabel(row.transform, text, TextSecondary, 17, TextAlignmentOptions.Center,
                Vector2.zero, Vector2.one, new Vector4(12, 0, 12, 0));
        }

        private static void SetHint(string text)
        {
            if (_hintLabel != null) _hintLabel.text = text;
        }

        private static TextMeshProUGUI MakeLabel(Transform parent, string text, Color color, float size,
                                                 TextAlignmentOptions align, Vector2 anchorMin, Vector2 anchorMax,
                                                 Vector4 margin)
        {
            var go = NewChild(parent, "Label");
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.margin = margin;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            ApplyFont(tmp);
            return tmp;
        }

        /// <summary>
        /// Un pulsante. Restituisce l'<see cref="Image"/> di sfondo (serve a chi
        /// deve cambiargliela dopo) e passa fuori l'etichetta in
        /// <paramref name="label"/>, per chi deve riscriverne il testo senza
        /// ricostruire il pulsante.
        /// </summary>
        private static Image MakeButton(Transform parent, string text, out TextMeshProUGUI label,
                                        Vector2 anchorMin, Vector2 anchorMax,
                                        Color background, Action onClick)
        {
            var go = NewChild(parent, "Btn");
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = background;
            var btn = go.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            if (onClick != null) btn.onClick.AddListener(new UnityAction(() => onClick()));

            label = MakeLabel(go.transform, text, Color.white, 16, TextAlignmentOptions.Center,
                Vector2.zero, Vector2.one, new Vector4(6, 0, 6, 0));
            return img;
        }

        /// <summary>
        /// TMP aggiunto a runtime non ha un font finche' non gliene diamo uno:
        /// senza, i glifi non vengono disegnati e la riga resta vuota.
        /// </summary>
        private static void ApplyFont(TMP_Text tmp)
        {
            var font = TmpFontHelper.Get();
            if (font != null) tmp.font = font;
        }

        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void StretchFull(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
        }
    }
}
