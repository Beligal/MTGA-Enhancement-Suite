using System;
using System.Collections.Generic;
using MTGAEnhancementSuite.State;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// L'editor del nome del mazzo: si scrive il nome in chiaro, si seleziona un
    /// pezzo, si sceglie uno stile. I tag non si vedono mai — li genera
    /// <see cref="StyledName"/> al momento di salvare, nella forma piu' corta
    /// che TMP accetta.
    ///
    /// **Il vincolo che decide tutta la finestra sono 64 caratteri**, tag
    /// compresi (`DeckValidationUtils.DECK_NAME_MAX_LENGTH`). Per questo il
    /// contatore sta in alto, grande, e il pulsante Applica si spegne appena si
    /// sfora: se si lasciasse salvare, Arena offrirebbe di tagliare con un
    /// `Substring(0, 64)` secco, che taglia un tag a meta' e lascia il nome a
    /// pezzi.
    ///
    /// **La selezione si legge di continuo, non al click.** Premere un pulsante
    /// toglie il fuoco al campo di testo, e con esso la selezione: se la
    /// leggessimo dentro il gestore del click, ogni stile finirebbe sul nome
    /// intero. Un piccolo MonoBehaviour la registra a ogni frame finche' il
    /// campo e' attivo, e i pulsanti usano l'ultima valida.
    /// </summary>
    internal static class DeckNameStudio
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
        private static readonly Color DangerRed       = new Color(0.80f, 0.30f, 0.30f, 1f);
        private static readonly Color OkGreen         = new Color(0.45f, 0.82f, 0.55f);

        private const string ModalName = "MTGAES_DeckNameStudio";

        /// <summary>Dieci tinte pronte. Il resto si scrive nel campo esadecimale.</summary>
        private static readonly string[] Swatches =
        {
            "#FFFFFF", "#D9D0A8", "#4A90D9", "#A98FC0", "#D9534F",
            "#58B368", "#FFD700", "#FF8000", "#FF69B4", "#00E5E5",
        };

        // --- stato dell'editor aperto ---
        private static GameObject _root;
        private static string _plain = "";
        private static List<CharStyle> _styles = new List<CharStyle>();
        private static Action<string> _onApply;
        private static string _subtitle = "";

        private static TMP_InputField _nameInput;
        private static TextMeshProUGUI _preview;
        private static TextMeshProUGUI _counter;
        private static TextMeshProUGUI _selectionInfo;
        private static Image _applyImage;
        private static TextMeshProUGUI _applyLabel;
        private static TMP_InputField _hexInput;

        // Ultima selezione valida letta dal watcher.
        private static int _selStart, _selEnd;

        // -----------------------------------------------------------------
        // Ingressi
        // -----------------------------------------------------------------

        /// <summary>
        /// Apre l'editor su un nome qualunque. <paramref name="onApply"/> riceve
        /// la stringa finale, con i tag: e' chi chiama a sapere dove scriverla.
        /// </summary>
        public static void Show(string currentName, string subtitle, Action<string> onApply)
        {
            if (onApply == null) return;

            _onApply = onApply;
            _subtitle = subtitle ?? "";

            // Se il nome ha gia' dei tag proviamo a rileggerli, cosi' si
            // riapre com'era e non si ricomincia da capo. Se contengono
            // qualcosa che non sappiamo rigenerare identico, TryParse dice di
            // no e trattiamo tutto come testo: meglio mostrare i tag che
            // riscriverli a caso e rovinare un nome che andava bene.
            if (!StyledName.TryParse(currentName ?? "", out _plain, out _styles))
            {
                _plain = currentName ?? "";
                _styles = new List<CharStyle>();
                for (int i = 0; i < _plain.Length; i++) _styles.Add(new CharStyle());
                Toast.Info("This name uses tags MTGA+ can't edit — showing it as plain text", 6f);
                Plugin.Log.LogInfo($"DeckNameStudio: '{currentName}' non e' rileggibile, modalita' testo grezzo.");
            }

            EnsureStyleCount();
            _selStart = _selEnd = 0;
            Build();
        }

        public static void Close()
        {
            ColorPicker.Close();
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _nameInput = null;
            _preview = null;
            _counter = null;
            _selectionInfo = null;
            _applyImage = null;
            _applyLabel = null;
            _hexInput = null;
            _onApply = null;
        }

        // -----------------------------------------------------------------
        // Selezione
        // -----------------------------------------------------------------

        /// <summary>
        /// Registra la selezione del campo a ogni frame. Vive sul riquadro e
        /// muore con lui.
        /// </summary>
        internal sealed class SelectionWatcher : MonoBehaviour
        {
            private void Update()
            {
                if (_nameInput == null) return;
                int a = _nameInput.selectionStringAnchorPosition;
                int b = _nameInput.selectionStringFocusPosition;

                if (a != b)
                {
                    int s = Mathf.Min(a, b), e = Mathf.Max(a, b);
                    if (s == _selStart && e == _selEnd) return;
                    _selStart = s; _selEnd = e;
                    UpdateSelectionInfo();
                    return;
                }

                // Niente selezione. Due casi opposti, e distinguerli e' tutto:
                //
                //  - il campo HA il fuoco: l'utente ha spostato il cursore o ha
                //    cliccato via la selezione, e vuole tornare al nome intero.
                //  - il campo NON ha il fuoco: gliel'ha appena tolto il click
                //    su un pulsante di stile. L'EventSystem deseleziona PRIMA
                //    di eseguire onClick, quindi azzerare qui vorrebbe dire che
                //    ogni stile finisce sul nome intero — cioe' esattamente il
                //    difetto che questo watcher esiste per evitare.
                if (!_nameInput.isFocused) return;

                if (_selStart == 0 && _selEnd == 0) return;
                _selStart = _selEnd = 0;
                UpdateSelectionInfo();
            }
        }

        /// <summary>
        /// L'intervallo su cui agiscono i pulsanti: la selezione se c'e' ed e'
        /// ancora dentro il testo, altrimenti il nome intero.
        /// </summary>
        private static void GetRange(out int start, out int end)
        {
            int len = _plain.Length;
            start = Mathf.Clamp(_selStart, 0, len);
            end = Mathf.Clamp(_selEnd, 0, len);
            if (start >= end) { start = 0; end = len; }
        }

        private static void UpdateSelectionInfo()
        {
            if (_selectionInfo == null) return;
            GetRange(out int a, out int b);
            bool whole = (a == 0 && b == _plain.Length);
            _selectionInfo.text = whole
                ? "applying to: the whole name"
                : $"applying to: \"{Excerpt(_plain, a, b)}\"";
        }

        private static string Excerpt(string s, int a, int b)
        {
            if (a >= b || a < 0 || b > s.Length) return "";
            string piece = s.Substring(a, b - a);
            return piece.Length <= 18 ? piece : piece.Substring(0, 17) + "...";
        }

        // -----------------------------------------------------------------
        // Modifiche
        // -----------------------------------------------------------------

        private static void EnsureStyleCount()
        {
            if (_styles == null) _styles = new List<CharStyle>();
            while (_styles.Count < _plain.Length) _styles.Add(new CharStyle());
            while (_styles.Count > _plain.Length) _styles.RemoveAt(_styles.Count - 1);
        }

        /// <summary>
        /// Tiene gli stili allineati al testo quando si scrive o si cancella.
        ///
        /// TMP_InputField dice solo com'e' il testo adesso, non cosa e'
        /// cambiato: il pezzo modificato lo ricaviamo confrontando il prefisso e
        /// il suffisso in comune. I caratteri nuovi ereditano lo stile di quello
        /// che li precede — e' quello che ci si aspetta scrivendo in mezzo a una
        /// parola gia' colorata.
        /// </summary>
        private static void OnTextChanged(string next)
        {
            next = next ?? "";
            if (next == _plain) return;

            string old = _plain;
            int min = Mathf.Min(old.Length, next.Length);

            int p = 0;
            while (p < min && old[p] == next[p]) p++;

            int s = 0;
            while (s < min - p && old[old.Length - 1 - s] == next[next.Length - 1 - s]) s++;

            int removed = old.Length - p - s;
            int inserted = next.Length - p - s;

            if (removed > 0) _styles.RemoveRange(p, removed);
            if (inserted > 0)
            {
                var inherited = (p > 0 && p - 1 < _styles.Count) ? _styles[p - 1]
                              : (_styles.Count > 0 ? _styles[0] : new CharStyle());
                for (int i = 0; i < inserted; i++) _styles.Insert(p + i, inherited);
            }

            _plain = next;
            EnsureStyleCount();
            Refresh();
        }

        private static void Apply(Func<CharStyle, CharStyle> change)
        {
            if (_plain.Length == 0) { Toast.Warning("Type a name first"); return; }
            GetRange(out int a, out int b);
            for (int i = a; i < b; i++) _styles[i] = change(_styles[i]);
            Refresh();
        }

        /// <summary>
        /// Il colore da cui far partire il selettore: quello del primo carattere
        /// selezionato che ne ha uno. Riaprire il selettore su un pezzo gia'
        /// colorato e trovarci bianco vorrebbe dire ricominciare ogni volta.
        /// </summary>
        private static Color32 SelectionColor(Color32 fallback)
        {
            GetRange(out int a, out int b);
            for (int i = a; i < b && i < _styles.Count; i++)
                if (_styles[i].HasColor) return _styles[i].Color;
            return fallback;
        }

        private static Color32 SelectionMark(Color32 fallback)
        {
            GetRange(out int a, out int b);
            for (int i = a; i < b && i < _styles.Count; i++)
                if (_styles[i].HasMark) return _styles[i].Mark;
            return fallback;
        }

        private static void Refresh()
        {
            string generated = StyledName.Generate(_plain, _styles);

            if (_preview != null)
                _preview.text = string.IsNullOrEmpty(generated) ? "<i>(empty)</i>" : generated;

            int len = generated.Length;
            bool over = len > StyledName.MaxLength;
            bool empty = string.IsNullOrWhiteSpace(_plain);

            if (_counter != null)
            {
                _counter.text = $"{len} / {StyledName.MaxLength}";
                _counter.color = over ? DangerRed : (len > StyledName.MaxLength - 8 ? new Color(0.95f, 0.78f, 0.35f) : OkGreen);
            }

            bool canApply = !over && !empty;
            if (_applyImage != null)
                _applyImage.color = canApply ? new Color(0.20f, 0.45f, 0.30f, 1f) : new Color(0.22f, 0.22f, 0.28f, 1f);
            if (_applyLabel != null)
            {
                _applyLabel.text = over ? $"{len - StyledName.MaxLength} too many" : "Apply";
                _applyLabel.color = canApply ? Color.white : TextDisabled;
            }

            UpdateSelectionInfo();
        }

        private static void DoApply()
        {
            string generated = StyledName.Generate(_plain, _styles);

            if (string.IsNullOrWhiteSpace(_plain))
            {
                Toast.Warning("A deck name can't be empty");
                return;
            }
            if (generated.Length > StyledName.MaxLength)
            {
                Toast.Warning($"Too long: {generated.Length} of {StyledName.MaxLength} characters");
                return;
            }

            // Generatore e lettore devono avere la stessa idea degli stessi tag.
            // Se il giro non torna qualcosa e' fuori posto, e il prossimo a
            // rimetterci sarebbe il nome di un mazzo: meglio fermarsi.
            if (!StyledName.RoundTrips(_plain, _styles, generated))
            {
                Plugin.Log.LogError(
                    $"DeckNameStudio: il controllo di andata e ritorno e' fallito su '{generated}'. " +
                    "Nome NON applicato.");
                Toast.Error("Internal check failed — the name was not changed. See the log.");
                return;
            }

            var callback = _onApply;
            Plugin.Log.LogInfo($"DeckNameStudio: applico '{generated}' ({generated.Length} caratteri).");
            Close();
            callback?.Invoke(generated);
        }

        // -----------------------------------------------------------------
        // Costruzione della finestra
        // -----------------------------------------------------------------
        private static void Build()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);

            _root = new GameObject(ModalName);
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 245;          // sopra la finestra di importazione
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            _root.AddComponent<GraphicRaycaster>();
            _root.AddComponent<SelectionWatcher>();

            var bg = NewChild(_root.transform, "Backdrop");
            StretchFull(bg);
            bg.AddComponent<Image>().color = new Color(0, 0, 0, 0.75f);
            var bgBtn = bg.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(Close));

            // Cornice fuori, pannello dentro: un bordo figlio del pannello lo
            // ricoprirebbe, perche' in Unity i figli si disegnano sopra il
            // padre. Vedi MISTAKES.md #28.
            var frame = NewChild(_root.transform, "Frame");
            var frRt = frame.GetComponent<RectTransform>();
            frRt.anchorMin = new Vector2(0.235f, 0.07f);
            frRt.anchorMax = new Vector2(0.765f, 0.93f);
            frRt.sizeDelta = Vector2.zero;
            frame.AddComponent<Image>().color = AccentBlue;

            var dialog = NewChild(frame.transform, "Dialog");
            var dRt = dialog.GetComponent<RectTransform>();
            dRt.anchorMin = Vector2.zero;
            dRt.anchorMax = Vector2.one;
            dRt.offsetMin = new Vector2(2f, 2f);
            dRt.offsetMax = new Vector2(-2f, -2f);
            dialog.AddComponent<Image>().color = PanelBackground;
            var d = dialog.transform;

            MakeLabel(d, "Deck name style", Color.white, 23, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.03f, 0.945f), new Vector2(0.70f, 0.995f), new Vector4(2, 0, 2, 0),
                FontStyles.Bold);

            MakeLabel(d, _subtitle, TextSecondary, 15, TextAlignmentOptions.MidlineRight,
                new Vector2(0.70f, 0.945f), new Vector2(0.97f, 0.995f), new Vector4(2, 0, 2, 0));

            BuildNameField(d);
            BuildPreview(d);
            BuildColorRows(d);
            BuildStyleRow(d);
            BuildNumericRow(d);
            BuildFooter(d);

            Refresh();
        }

        private static void BuildNameField(Transform d)
        {
            MakeLabel(d, "Name", TextSecondary, 14, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.03f, 0.905f), new Vector2(0.30f, 0.940f), new Vector4(2, 0, 2, 0));

            var holder = NewChild(d, "NameField");
            var rt = holder.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.03f, 0.845f);
            rt.anchorMax = new Vector2(0.97f, 0.902f);
            rt.sizeDelta = Vector2.zero;
            holder.AddComponent<Image>().color = FieldBackground;

            _nameInput = holder.AddComponent<TMP_InputField>();
            // Il testo in chiaro non puo' essere piu' lungo del risultato coi
            // tag, quindi lo stesso limite vale anche qui.
            _nameInput.characterLimit = StyledName.MaxLength;

            var textArea = NewChild(holder.transform, "TextArea");
            StretchFull(textArea);
            textArea.AddComponent<RectMask2D>();

            var textGO = NewChild(textArea.transform, "Text");
            StretchFull(textGO);
            var textTmp = textGO.AddComponent<TextMeshProUGUI>();
            textTmp.fontSize = 19;
            textTmp.color = TextPrimary;
            textTmp.alignment = TextAlignmentOptions.MidlineLeft;
            textTmp.margin = new Vector4(12, 0, 8, 0);
            // Il campo mostra il nome NUDO: qui i tag non si vedono e non si
            // interpretano, altrimenti selezionare un pezzo darebbe indici che
            // non corrispondono ai caratteri veri.
            textTmp.richText = false;
            ApplyFont(textTmp);
            _nameInput.textComponent = textTmp;
            _nameInput.textViewport = textArea.GetComponent<RectTransform>();

            // DEVE stare qui, dopo l'assegnazione del textComponent: prima non
            // servirebbe a niente. Senza questa riga la selezione non viene
            // evidenziata e non si vede cosa si sta per colorare.
            TmpInputFieldFix.EnableCaretAndSelection(_nameInput, new Color(0.30f, 0.50f, 0.70f, 0.85f));

            var ph = NewChild(textArea.transform, "Placeholder");
            StretchFull(ph);
            var phTmp = ph.AddComponent<TextMeshProUGUI>();
            phTmp.text = "Deck name...";
            phTmp.fontSize = 19;
            phTmp.color = TextDisabled;
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.alignment = TextAlignmentOptions.MidlineLeft;
            phTmp.margin = new Vector4(12, 0, 8, 0);
            ApplyFont(phTmp);
            _nameInput.placeholder = phTmp;

            _nameInput.text = _plain;
            _nameInput.onValueChanged.AddListener(new UnityAction<string>(OnTextChanged));
            BlinkingCaret.Attach(_nameInput);
            _nameInput.ActivateInputField();
        }

        private static void BuildPreview(Transform d)
        {
            MakeLabel(d, "Preview", TextSecondary, 14, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.03f, 0.800f), new Vector2(0.40f, 0.838f), new Vector4(2, 0, 2, 0));

            _counter = MakeLabel(d, "", OkGreen, 16, TextAlignmentOptions.MidlineRight,
                new Vector2(0.60f, 0.800f), new Vector2(0.97f, 0.838f), new Vector4(2, 0, 2, 0),
                FontStyles.Bold);

            var box = NewChild(d, "PreviewBox");
            var rt = box.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.03f, 0.715f);
            rt.anchorMax = new Vector2(0.97f, 0.798f);
            rt.sizeDelta = Vector2.zero;
            box.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.11f, 1f);

            _preview = MakeLabel(box.transform, "", Color.white, 24, TextAlignmentOptions.Center,
                Vector2.zero, Vector2.one, new Vector4(10, 0, 10, 0));
            // L'anteprima e' l'unico punto in cui i tag DEVONO essere
            // interpretati: e' quello che vedra' Arena.
            _preview.richText = true;

            _selectionInfo = MakeLabel(d, "", TextDisabled, 14, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.03f, 0.672f), new Vector2(0.97f, 0.710f), new Vector4(2, 0, 2, 0));
        }

        private static void BuildColorRows(Transform d)
        {
            MakeLabel(d, "Text color", TextSecondary, 14, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.03f, 0.615f), new Vector2(0.25f, 0.655f), new Vector4(2, 0, 2, 0));

            for (int i = 0; i < Swatches.Length; i++)
            {
                string hex = Swatches[i];
                float x0 = 0.26f + i * 0.0705f;
                StyledName.TryParseColor(hex, out var col);
                MakeButton(d, "", new Vector2(x0, 0.615f), new Vector2(x0 + 0.062f, 0.655f),
                    col, () => Apply(s => { s.HasColor = true; s.Color = col; return s; }), out _);
            }

            // --- esadecimale libero + rimozione colore ---
            var holder = NewChild(d, "HexField");
            var rt = holder.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.26f, 0.555f);
            rt.anchorMax = new Vector2(0.48f, 0.600f);
            rt.sizeDelta = Vector2.zero;
            holder.AddComponent<Image>().color = FieldBackground;
            _hexInput = holder.AddComponent<TMP_InputField>();
            _hexInput.characterLimit = 9;

            var ta = NewChild(holder.transform, "TextArea");
            StretchFull(ta);
            ta.AddComponent<RectMask2D>();
            var tg = NewChild(ta.transform, "Text");
            StretchFull(tg);
            var ttmp = tg.AddComponent<TextMeshProUGUI>();
            ttmp.fontSize = 15; ttmp.color = TextPrimary;
            ttmp.alignment = TextAlignmentOptions.MidlineLeft;
            ttmp.margin = new Vector4(8, 0, 6, 0);
            ttmp.richText = false;
            ApplyFont(ttmp);
            _hexInput.textComponent = ttmp;
            _hexInput.textViewport = ta.GetComponent<RectTransform>();
            TmpInputFieldFix.EnableCaretAndSelection(_hexInput, new Color(0.30f, 0.50f, 0.70f, 0.85f));

            var ph = NewChild(ta.transform, "Placeholder");
            StretchFull(ph);
            var phTmp = ph.AddComponent<TextMeshProUGUI>();
            phTmp.text = "#RGB or #RRGGBB";
            phTmp.fontSize = 15; phTmp.color = TextDisabled;
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.margin = new Vector4(8, 0, 6, 0);
            ApplyFont(phTmp);
            _hexInput.placeholder = phTmp;
            BlinkingCaret.Attach(_hexInput);

            MakeButton(d, "Use hex", new Vector2(0.49f, 0.555f), new Vector2(0.615f, 0.600f),
                ButtonIdle, () =>
                {
                    if (_hexInput == null) return;
                    if (!StyledName.TryParseColor(_hexInput.text, out var col))
                    {
                        Toast.Warning("Not a color TMP understands: use #RGB, #RGBA, #RRGGBB or #RRGGBBAA");
                        return;
                    }
                    Apply(s => { s.HasColor = true; s.Color = col; return s; });
                }, out _);

            MakeButton(d, "Pick...", new Vector2(0.625f, 0.555f), new Vector2(0.785f, 0.600f),
                AccentBlue, () => ColorPicker.Show(SelectionColor(new Color32(255, 255, 255, 255)), true,
                    "Text color", col => Apply(s => { s.HasColor = true; s.Color = col; return s; })), out _);

            MakeButton(d, "No color", new Vector2(0.795f, 0.555f), new Vector2(0.94f, 0.600f),
                ButtonIdle, () => Apply(s => { s.HasColor = false; return s; }), out _);

            // --- evidenziatore ---
            MakeLabel(d, "Highlight", TextSecondary, 14, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.03f, 0.495f), new Vector2(0.25f, 0.535f), new Vector4(2, 0, 2, 0));

            for (int i = 0; i < 6; i++)
            {
                string hex = Swatches[i + 2];
                float x0 = 0.26f + i * 0.0705f;
                StyledName.TryParseColor(hex, out var c);
                var mark = new Color32(c.r, c.g, c.b, 110);      // semitrasparente: il testo resta leggibile
                MakeButton(d, "", new Vector2(x0, 0.495f), new Vector2(x0 + 0.062f, 0.535f),
                    new Color32(c.r, c.g, c.b, 255),
                    () => Apply(s => { s.HasMark = true; s.Mark = mark; return s; }), out _);
            }

            MakeButton(d, "Pick...", new Vector2(0.69f, 0.495f), new Vector2(0.85f, 0.535f),
                AccentBlue, () => ColorPicker.Show(SelectionMark(new Color32(255, 215, 0, 110)), true,
                    "Highlight color", col => Apply(s => { s.HasMark = true; s.Mark = col; return s; })), out _);

            MakeButton(d, "None", new Vector2(0.86f, 0.495f), new Vector2(0.97f, 0.535f),
                ButtonIdle, () => Apply(s => { s.HasMark = false; return s; }), out _);
        }

        private static void BuildStyleRow(Transform d)
        {
            MakeLabel(d, "Style", TextSecondary, 14, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.03f, 0.425f), new Vector2(0.25f, 0.465f), new Vector4(2, 0, 2, 0));

            MakeButton(d, "<b>B</b>", new Vector2(0.26f, 0.425f), new Vector2(0.325f, 0.465f),
                ButtonIdle, () => Apply(s => { s.Bold = !s.Bold; return s; }), out _);
            MakeButton(d, "<i>I</i>", new Vector2(0.335f, 0.425f), new Vector2(0.40f, 0.465f),
                ButtonIdle, () => Apply(s => { s.Italic = !s.Italic; return s; }), out _);
            MakeButton(d, "<u>U</u>", new Vector2(0.41f, 0.425f), new Vector2(0.475f, 0.465f),
                ButtonIdle, () => Apply(s => { s.Underline = !s.Underline; return s; }), out _);
            MakeButton(d, "<s>S</s>", new Vector2(0.485f, 0.425f), new Vector2(0.55f, 0.465f),
                ButtonIdle, () => Apply(s => { s.Strike = !s.Strike; return s; }), out _);

            MakeButton(d, "Aa", new Vector2(0.575f, 0.425f), new Vector2(0.665f, 0.465f),
                ButtonIdle, () => Apply(s => { s.Case = CaseMode.SmallCaps; return s; }), out _);
            MakeButton(d, "AA", new Vector2(0.675f, 0.425f), new Vector2(0.765f, 0.465f),
                ButtonIdle, () => Apply(s => { s.Case = CaseMode.Upper; return s; }), out _);
            MakeButton(d, "aa", new Vector2(0.775f, 0.425f), new Vector2(0.865f, 0.465f),
                ButtonIdle, () => Apply(s => { s.Case = CaseMode.Lower; return s; }), out _);
            MakeButton(d, "reset", new Vector2(0.875f, 0.425f), new Vector2(0.97f, 0.465f),
                ButtonIdle, () => Apply(s => { s.Case = CaseMode.None; return s; }), out _);
        }

        private static void BuildNumericRow(Transform d)
        {
            Stepper(d, "Size", 0.355f,
                () => Apply(s => { s.SizePercent = NextSize(s.SizePercent, -10); return s; }),
                () => Apply(s => { s.SizePercent = NextSize(s.SizePercent, +10); return s; }),
                () => Apply(s => { s.SizePercent = 0; return s; }));

            Stepper(d, "Raise", 0.285f,
                () => Apply(s => { s.VOffsetTenths = Mathf.Clamp(s.VOffsetTenths - 1, -9, 9); return s; }),
                () => Apply(s => { s.VOffsetTenths = Mathf.Clamp(s.VOffsetTenths + 1, -9, 9); return s; }),
                () => Apply(s => { s.VOffsetTenths = 0; return s; }));

            Stepper(d, "Spacing", 0.215f,
                () => Apply(s => { s.CSpaceTenths = Mathf.Clamp(s.CSpaceTenths - 1, -9, 9); return s; }),
                () => Apply(s => { s.CSpaceTenths = Mathf.Clamp(s.CSpaceTenths + 1, -9, 9); return s; }),
                () => Apply(s => { s.CSpaceTenths = 0; return s; }));

            Stepper(d, "Rotate", 0.145f,
                () => Apply(s => { s.RotateDegrees = Mathf.Clamp(s.RotateDegrees - 5, -90, 90); return s; }),
                () => Apply(s => { s.RotateDegrees = Mathf.Clamp(s.RotateDegrees + 5, -90, 90); return s; }),
                () => Apply(s => { s.RotateDegrees = 0; return s; }));
        }

        /// <summary>Dimensione: default (0) significa 100%.</summary>
        private static int NextSize(int current, int delta)
        {
            int baseline = current == 0 ? 100 : current;
            int next = Mathf.Clamp(baseline + delta, 50, 200);
            return next == 100 ? 0 : next;
        }

        private static void Stepper(Transform d, string label, float y,
                                    Action minus, Action plus, Action reset)
        {
            float h = 0.042f;
            MakeLabel(d, label, TextSecondary, 14, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.03f, y), new Vector2(0.25f, y + h), new Vector4(2, 0, 2, 0));
            MakeButton(d, "-", new Vector2(0.26f, y), new Vector2(0.335f, y + h), ButtonIdle, minus, out _);
            MakeButton(d, "+", new Vector2(0.345f, y), new Vector2(0.42f, y + h), ButtonIdle, plus, out _);
            MakeButton(d, "reset", new Vector2(0.43f, y), new Vector2(0.545f, y + h), ButtonIdle, reset, out _);
        }

        private static void BuildFooter(Transform d)
        {
            MakeButton(d, "Clear all styles", new Vector2(0.03f, 0.082f), new Vector2(0.28f, 0.128f),
                ButtonIdle, () =>
                {
                    for (int i = 0; i < _styles.Count; i++) _styles[i] = new CharStyle();
                    Refresh();
                }, out _);

            MakeLabel(d, "Select part of the name to style just that piece. Tags are never closed at the end - it saves 8 of your 64 characters.",
                TextDisabled, 13, TextAlignmentOptions.MidlineRight,
                new Vector2(0.29f, 0.082f), new Vector2(0.97f, 0.128f), new Vector4(4, 0, 4, 0));

            MakeButton(d, "Cancel", new Vector2(0.03f, 0.020f), new Vector2(0.25f, 0.072f),
                new Color(0.25f, 0.25f, 0.32f, 1f), Close, out _);

            _applyImage = MakeButton(d, "Apply", new Vector2(0.70f, 0.020f), new Vector2(0.97f, 0.072f),
                new Color(0.20f, 0.45f, 0.30f, 1f), DoApply, out _applyLabel);
        }

        // -----------------------------------------------------------------
        // Mattoni
        // -----------------------------------------------------------------

        private static TextMeshProUGUI MakeLabel(Transform parent, string text, Color color, float size,
                                                 TextAlignmentOptions align, Vector2 anchorMin, Vector2 anchorMax,
                                                 Vector4 margin, FontStyles style = FontStyles.Normal)
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
            tmp.fontStyle = style;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            ApplyFont(tmp);
            return tmp;
        }

        private static Image MakeButton(Transform parent, string text,
                                        Vector2 anchorMin, Vector2 anchorMax,
                                        Color background, Action onClick, out TextMeshProUGUI label)
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

            label = string.IsNullOrEmpty(text) ? null
                : MakeLabel(go.transform, text, Color.white, 16, TextAlignmentOptions.Center,
                            Vector2.zero, Vector2.one, new Vector4(4, 0, 4, 0));
            if (label != null) label.richText = true;
            return img;
        }

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
