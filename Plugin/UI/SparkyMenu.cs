using System;
using System.Collections.Generic;
using MTGAEnhancementSuite.Patches;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Finestrella che si apre con F9: quale mazzo giochera' Sparky e come si
    /// comportera'. Un click sceglie e chiude.
    ///
    /// Stessa meccanica di <see cref="PresetMenu"/>: canvas proprio, sfondo
    /// trasparente a tutto schermo che intercetta i click fuori e chiude. Le
    /// misure vengono dall'altezza dello schermo e non da pixel fissi, perche'
    /// il canvas e' ConstantPixelSize e su un monitor ad alta risoluzione i
    /// valori fissi risultano minuscoli.
    ///
    /// L'ASPETTO invece e' quello di <see cref="Features.CombatTotals"/>:
    /// lastra arrotondata su neutri caldi, font e materiale presi dai testi
    /// che Arena sta gia' mostrando, filetto sotto ai titoli di sezione. Le
    /// finestre che il mod mette sopra il tavolo da gioco si somigliano fra
    /// loro e somigliano al gioco; quelle sopra i menu (il pannello dei mazzi)
    /// seguono invece il blu notte di quelle schermate.
    ///
    /// La finestra si apre al centro: F9 non e' legato a un pulsante, quindi
    /// non c'e' un punto dello schermo da cui farla scendere.
    /// </summary>
    internal static class SparkyMenu
    {
        // Stessa tavolozza del cartellino del combattimento (CombatTotals):
        // neutri caldi e scuri, come l'interfaccia di Arena in duello. Il blu
        // acciaio di prima veniva dal pannello dei mazzi, che vive su fondo blu
        // notte; qui sopra il tavolo da gioco saltava fuori come un adesivo.
        //
        // Il fondo e' pieno (alfa 1): questa e' una finestra che chiede una
        // scelta, non un cartellino da leggere mentre si guarda il tavolo.
        private static readonly Color PanelColor    = new Color(0.055f, 0.05f, 0.045f, 1f);
        private static readonly Color BorderColor   = new Color(0.62f, 0.56f, 0.44f, 0.30f);
        private static readonly Color RuleColor     = new Color(0.62f, 0.56f, 0.44f, 0.22f);
        private static readonly Color TextPrimary   = new Color(0.93f, 0.91f, 0.86f, 1f);
        private static readonly Color AccentText    = new Color(0.79f, 0.69f, 0.51f, 1f);  // C9B182
        private static readonly Color TextSecondary = new Color(0.55f, 0.52f, 0.47f, 1f);  // 8C8478

        // L'arancio del danno che passa, lo stesso del cartellino del
        // combattimento: qui marca la voce attualmente scelta.
        private static readonly Color AccentDanger  = new Color(0.878f, 0.541f, 0.353f, 1f);  // E08A5A

        // Le tre tinte della riga cliccabile. Sono i colori del *pulsante*, non
        // dell'immagine: l'immagine resta bianca e fa da moltiplicatore neutro,
        // altrimenti una riga di fondo trasparente resterebbe invisibile anche
        // sotto il puntatore (il tint di Unity moltiplica, non sostituisce).
        //
        // Sono NEUTRE, non dorate. La prima versione metteva un velo ottone
        // sopra un fondo ottone: senza stacco le righe si fondevano col
        // pannello e il risultato era una macchia sola. L'oro resta dove
        // significa qualcosa — il bordo esterno e i titoli di sezione.
        private static readonly Color RowNormal      = new Color(0.115f, 0.108f, 0.098f, 1f);
        private static readonly Color RowHighlighted = new Color(0.175f, 0.162f, 0.142f, 1f);
        private static readonly Color RowPressed     = new Color(0.235f, 0.215f, 0.185f, 1f);

        /// <summary>Contorno scuro attorno a ogni voce: e' cio' che le separa.</summary>
        private static readonly Color RowOutline     = new Color(0f, 0f, 0f, 0.7f);

        private static GameObject _open;

        /// <summary>
        /// Apre la finestra, o la chiude se e' gia' aperta: cosi' F9 premuto due
        /// volte non lascia due menu sovrapposti.
        /// </summary>
        public static void Toggle()
        {
            if (_open != null) { Close(); return; }

            float rowHeight = Mathf.Max(29f, Screen.height * 0.034f);
            float menuWidth = Mathf.Clamp(Screen.width * 0.20f, 280f, 460f);
            float fontSize = Mathf.Max(14f, rowHeight * 0.45f);
            float padding = Mathf.Max(6f, rowHeight * 0.20f);

            var decks = SparkyDeckPatch.AvailableDecks();
            bool provisional = !SparkyDeckPatch.HasLiveList;
            var current = SparkyDeckPatch.Choice;
            var behaviours = (SparkyBehaviourPatch.Behaviour[])
                Enum.GetValues(typeof(SparkyBehaviourPatch.Behaviour));
            var currentBehaviour = SparkyBehaviourPatch.Mode;

            // titolo + eventuale nota + sorteggio + un mazzo per riga,
            // poi titolo del comportamento + una riga per modalita'.
            int rows = 1 + (provisional ? 1 : 0) + 1 + decks.Count + 1 + behaviours.Length;
            float height = padding * 2 + rowHeight * rows;

            var root = new GameObject("MTGAES_SparkyMenu");
            UnityEngine.Object.DontDestroyOnLoad(root);
            _open = root;

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 225;
            root.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            root.AddComponent<GraphicRaycaster>();

            var backdrop = NewChild(root.transform, "Backdrop");
            var brt = backdrop.GetComponent<RectTransform>();
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
            backdrop.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            var bgBtn = backdrop.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(Close));

            var menu = NewChild(root.transform, "Menu");
            var rt = menu.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(menuWidth, height);
            rt.anchoredPosition = Vector2.zero;
            // Due lastre arrotondate, una dentro l'altra: quella sotto sporge
            // di un pixel e fa da bordo. Stessa costruzione del cartellino del
            // combattimento, cosi' le due finestre del mod si somigliano.
            var rounded = RoundedRect.Get(10);

            var menuImg = menu.AddComponent<Image>();
            menuImg.sprite = rounded;
            menuImg.type = Image.Type.Sliced;
            menuImg.color = PanelColor;

            var border = NewChild(menu.transform, "Border");
            var bort = border.GetComponent<RectTransform>();
            bort.anchorMin = Vector2.zero; bort.anchorMax = Vector2.one;
            bort.offsetMin = new Vector2(-1f, -1f); bort.offsetMax = new Vector2(1f, 1f);
            var borderImg = border.AddComponent<Image>();
            borderImg.sprite = rounded;
            borderImg.type = Image.Type.Sliced;
            borderImg.color = BorderColor;
            borderImg.raycastTarget = false;
            border.transform.SetAsFirstSibling();

            // Terza lastra, la piu' esterna: lo stesso contorno scuro che
            // hanno le voci. Chiude la finestra contro il tavolo da gioco, che
            // sotto e' pieno di illustrazioni chiare — senza, il pannello
            // sfrangiava sui fondi luminosi. Sporge di un pixel oltre il
            // filetto ottone, quindi si vedono entrambi: scuro fuori, caldo
            // dentro.
            //
            // Va creata DOPO il filetto e poi mandata per prima: in Unity
            // l'ordine fra fratelli e' l'ordine di disegno, e questa deve
            // finire sotto a tutto.
            var outline = NewChild(menu.transform, "Outline");
            var outt = outline.GetComponent<RectTransform>();
            outt.anchorMin = Vector2.zero; outt.anchorMax = Vector2.one;
            outt.offsetMin = new Vector2(-2f, -2f); outt.offsetMax = new Vector2(2f, 2f);
            var outlineImg = outline.AddComponent<Image>();
            outlineImg.sprite = rounded;
            outlineImg.type = Image.Type.Sliced;
            outlineImg.color = RowOutline;
            outlineImg.raycastTarget = false;
            outline.transform.SetAsFirstSibling();

            float y = -padding;

            MakeRow(menu.transform, ref y, rowHeight, padding, fontSize,
                "SPARKY'S DECK", AccentText, null, header: true, rule: true);

            if (provisional)
            {
                // Onesto invece che silenzioso: prima della prima partita
                // contro il bot non abbiamo ancora visto la lista vera del
                // gioco, e questi nomi vengono dal ripiego.
                MakeRow(menu.transform, ref y, rowHeight, padding, fontSize,
                    "list not confirmed yet - play one bot game", TextSecondary, null, header: true);
            }

            bool randomSelected = current == SparkyDeckPatch.RandomChoice;
            MakeRow(menu.transform, ref y, rowHeight, padding, fontSize,
                Mark(randomSelected) + "Random (game's own draw)",
                RowText(randomSelected),
                () => Choose(SparkyDeckPatch.RandomChoice));

            for (int i = 0; i < decks.Count; i++)
            {
                var deck = decks[i];
                MakeRow(menu.transform, ref y, rowHeight, padding, fontSize,
                    Mark(current == deck) + SparkyDeckPatch.Label(deck),
                    RowText(current == deck),
                    () => Choose(deck));
            }

            MakeRow(menu.transform, ref y, rowHeight, padding, fontSize,
                "BEHAVIOUR", AccentText, null, header: true, rule: true);

            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                MakeRow(menu.transform, ref y, rowHeight, padding, fontSize,
                    Mark(currentBehaviour == behaviour) + SparkyBehaviourPatch.Describe(behaviour),
                    RowText(currentBehaviour == behaviour),
                    () => ChooseBehaviour(behaviour));
            }
        }

        public static void Close()
        {
            if (_open == null) return;
            UnityEngine.Object.Destroy(_open);
            _open = null;
        }

        private static void Choose(string deck)
        {
            Close();
            SparkyDeckPatch.Choose(deck);
        }

        private static void ChooseBehaviour(SparkyBehaviourPatch.Behaviour behaviour)
        {
            Close();
            SparkyBehaviourPatch.Choose(behaviour);
        }

        /// <summary>
        /// Il colore del testo di una voce: arancione se e' quella in vigore.
        /// Il marcatore da solo si vede poco in una lista lunga; il colore si
        /// trova a colpo d'occhio senza doverla leggere tutta.
        /// </summary>
        private static Color RowText(bool selected)
        {
            return selected ? AccentDanger : TextPrimary;
        }

        /// <summary>
        /// Il segno della voce scelta. E' un carattere di accento, colorato
        /// col tag rich text di TMP: il resto della riga resta del colore
        /// normale, cosi' si legge la voce e non il marcatore.
        ///
        /// Il testo riparte da una posizione FISSA (<c>pos</c>), non da un
        /// numero di spazi: il font non e' a larghezza fissa, quindi contare
        /// spazi farebbe ballare le voci in orizzontale a seconda che siano
        /// scelte o no.
        /// </summary>
        private static string Mark(bool selected)
        {
            return selected
                ? "<color=#E08A5A>›</color><pos=1.2em>"
                : "<pos=1.2em>";
        }

        /// <summary>
        /// Una riga. Con <paramref name="onClick"/> nullo resta inerte, e con
        /// <paramref name="header"/> perde anche lo sfondo: serve per il titolo
        /// e per la nota, che non sono voci selezionabili.
        ///
        /// <paramref name="rule"/> aggiunge il filetto sotto al titolo di
        /// sezione — lo stesso stacco che il cartellino del combattimento mette
        /// sotto alla sua intestazione. Non lo vuole la nota "lista non
        /// confermata", che titolo non e'.
        /// </summary>
        private static void MakeRow(Transform parent, ref float y, float rowHeight, float padding,
                                    float fontSize, string text, Color color,
                                    Action onClick, bool header = false, bool rule = false)
        {
            var row = NewChild(parent, header ? "Header" : "Row");
            var rrt = row.GetComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0f, 1f);
            rrt.anchorMax = new Vector2(1f, 1f);
            rrt.pivot = new Vector2(0.5f, 1f);
            rrt.offsetMin = new Vector2(padding, 0f);
            rrt.offsetMax = new Vector2(-padding, 0f);
            // La casella e' piu' bassa del passo di riga: i tre pixel che
            // avanzano sono lo stacco fra un contorno e il successivo, senza i
            // quali i bordi si toccherebbero e tornerebbero a leggersi come un
            // blocco unico. Il passo NON cambia, quindi l'altezza totale
            // calcolata in Toggle resta valida.
            rrt.sizeDelta = new Vector2(-padding * 2f, header ? rowHeight : rowHeight - 3f);
            rrt.anchoredPosition = new Vector2(0f, y);
            y -= rowHeight;

            // L'immagine della riga fa da CONTORNO: il riempimento e' un figlio
            // rientrato di un pixel. Serve perche' in Unity il grafico di un
            // oggetto si disegna PRIMA dei suoi figli — un contorno messo come
            // figlio finirebbe sopra al riempimento invece che dietro.
            var bg = row.AddComponent<Image>();
            bg.raycastTarget = !header;

            Image fill = null;

            if (header)
            {
                bg.color = new Color(0f, 0f, 0f, 0f);
            }
            else
            {
                var rounded = RoundedRect.Get(10);

                bg.sprite = rounded;
                bg.type = Image.Type.Sliced;
                bg.color = RowOutline;

                var fillGO = NewChild(row.transform, "Fill");
                var frt = fillGO.GetComponent<RectTransform>();
                frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
                frt.offsetMin = new Vector2(1f, 1f); frt.offsetMax = new Vector2(-1f, -1f);

                // Immagine BIANCA di proposito: il tint di Unity moltiplica il
                // colore del pulsante per quello dell'immagine, quindi una base
                // gia' colorata falserebbe tutte e tre le tinte. Il bianco e' il
                // moltiplicatore neutro, e i colori veri stanno in Button.colors.
                fill = fillGO.AddComponent<Image>();
                fill.sprite = rounded;
                fill.type = Image.Type.Sliced;
                fill.color = Color.white;
                fill.raycastTarget = false;
            }

            if (onClick != null)
            {
                var btn = row.AddComponent<Button>();
                btn.targetGraphic = fill != null ? (Graphic)fill : bg;
                var colors = btn.colors;
                colors.normalColor = RowNormal;
                colors.highlightedColor = RowHighlighted;
                colors.pressedColor = RowPressed;
                colors.selectedColor = RowNormal;
                colors.fadeDuration = 0.08f;
                btn.colors = colors;
                btn.onClick.AddListener(new UnityAction(() => onClick()));
            }

            var labelGO = NewChild(row.transform, "Label");
            var lrt = labelGO.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(rowHeight * 0.36f, 0f);
            lrt.offsetMax = new Vector2(-10f, 0f);
            var label = labelGO.AddComponent<TextMeshProUGUI>();

            // Font e materiale presi dai testi che Arena sta gia' mostrando:
            // e' quello che fa sembrare la finestra parte del gioco invece che
            // incollata sopra. Vedi UI/ArenaFont.
            ArenaFont.Apply(label);

            label.text = text;
            label.fontSize = header ? fontSize * 0.85f : fontSize;
            label.color = color;
            if (header) label.characterSpacing = 2f;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            label.richText = true;

            if (!rule) return;

            var line = NewChild(row.transform, "Rule");
            var lineRect = line.GetComponent<RectTransform>();
            lineRect.anchorMin = new Vector2(0f, 0f);
            lineRect.anchorMax = new Vector2(1f, 0f);
            lineRect.pivot = new Vector2(0.5f, 0f);
            lineRect.offsetMin = new Vector2(0f, 0f);
            lineRect.offsetMax = new Vector2(0f, 0f);
            lineRect.sizeDelta = new Vector2(0f, 1f);
            var lineImg = line.AddComponent<Image>();
            lineImg.color = RuleColor;
            lineImg.raycastTarget = false;
        }

        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }
    }
}
