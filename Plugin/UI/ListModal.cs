using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>Il riquadro aperto: dove mettere le righe, e dove i pulsanti.</summary>
    internal sealed class ModalFrame
    {
        public GameObject Root;
        /// <summary>Il contenuto scorrevole: ogni riga va qui dentro.</summary>
        public Transform List;
        /// <summary>La fascia in basso, per i pulsanti.</summary>
        public Transform Footer;

        public void Destroy()
        {
            if (Root != null) UnityEngine.Object.Destroy(Root);
            Root = null;
            List = null;
            Footer = null;
        }
    }

    /// <summary>
    /// Il riquadro con la lista scorrevole e il pie' di pagina, piu' i pezzi
    /// per riempirlo: righe, etichette, pulsanti.
    ///
    /// **Estratto da <see cref="DeckStatsWindow"/> quando e' servita una
    /// seconda finestra uguale** (quella dei replay). Prima era tutto li'
    /// dentro, privato; due copie della stessa cornice sarebbero diventate
    /// due cornici diverse alla prima modifica.
    ///
    /// I chiamanti lo usano con <c>using static</c>, cosi' scrivono
    /// <c>Row(...)</c> e <c>Label(...)</c> come quando i metodi erano in casa
    /// loro. Attenzione a una conseguenza: dentro un file che fa
    /// <c>using static</c> di questa classe, <c>Button</c> e' il **metodo** qui
    /// sotto, e il tipo di Unity va scritto per esteso.
    ///
    /// Ogni finestra tiene il **suo** <see cref="ModalFrame"/>: non c'e' uno
    /// stato condiviso, quindi aprirne una non chiude l'altra di nascosto.
    /// </summary>
    internal static class ListModal
    {
        public static readonly Color PanelBackground = new Color(0.10f, 0.10f, 0.18f, 0.98f);
        public static readonly Color RowBackground   = new Color(0.15f, 0.15f, 0.25f, 1f);
        public static readonly Color AccentBlue      = new Color(0.30f, 0.50f, 0.70f, 1f);
        public static readonly Color TextPrimary     = new Color(0.85f, 0.85f, 0.90f);
        public static readonly Color TextSecondary   = new Color(0.62f, 0.62f, 0.72f);
        public static readonly Color TextDisabled    = new Color(0.45f, 0.45f, 0.55f);
        public static readonly Color ButtonIdle      = new Color(0.20f, 0.20f, 0.30f, 1f);
        public static readonly Color WinGreen        = new Color(0.45f, 0.82f, 0.55f);
        public static readonly Color LossRed         = new Color(0.88f, 0.42f, 0.42f);

        public const float RowHeight = 34f;

        /// <summary>
        /// Costruisce il riquadro e restituisce i due punti dove si appende
        /// qualcosa.
        ///
        /// <paramref name="onClose"/> lo chiama il fondo scuro: cliccare fuori
        /// chiude. La cornice sono **due oggetti annidati** e non un figlio
        /// piu' grande, perche' in Unity i figli si disegnano SOPRA il
        /// genitore e un "bordo" figlio coprirebbe il pannello
        /// (<c>MISTAKES.md</c> #28).
        /// </summary>
        public static ModalFrame Open(string objectName, string title, bool richTitle, Action onClose,
                                      Vector2? anchorMin = null, Vector2? anchorMax = null)
        {
            var frame = new ModalFrame();

            frame.Root = new GameObject(objectName);
            UnityEngine.Object.DontDestroyOnLoad(frame.Root);

            var canvas = frame.Root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 238;
            var scaler = frame.Root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            frame.Root.AddComponent<GraphicRaycaster>();

            var bg = NewChild(frame.Root.transform, "Backdrop");
            Stretch(bg);
            bg.AddComponent<Image>().color = new Color(0, 0, 0, 0.7f);
            var bgBtn = bg.AddComponent<UnityEngine.UI.Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(() => onClose()));

            var border = NewChild(frame.Root.transform, "Frame");
            var frt = border.GetComponent<RectTransform>();
            frt.anchorMin = anchorMin ?? new Vector2(0.30f, 0.12f);
            frt.anchorMax = anchorMax ?? new Vector2(0.70f, 0.88f);
            frt.sizeDelta = Vector2.zero;
            border.AddComponent<Image>().color = AccentBlue;

            var dialog = NewChild(border.transform, "Dialog");
            var drt = dialog.GetComponent<RectTransform>();
            drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one;
            drt.offsetMin = new Vector2(2f, 2f); drt.offsetMax = new Vector2(-2f, -2f);
            dialog.AddComponent<Image>().color = PanelBackground;
            var d = dialog.transform;

            var titleLabel = Label(d, title, Color.white, 24, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.04f, 0.925f), new Vector2(0.96f, 0.985f), new Vector4(2, 0, 2, 0),
                FontStyles.Bold);
            titleLabel.richText = richTitle;

            var listArea = NewChild(d, "ListArea");
            var lrt = listArea.GetComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0.04f, 0.115f);
            lrt.anchorMax = new Vector2(0.96f, 0.915f);
            lrt.sizeDelta = Vector2.zero;
            var scroll = listArea.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.scrollSensitivity = 28f;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            var viewport = NewChild(listArea.transform, "Viewport");
            Stretch(viewport);
            viewport.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            viewport.AddComponent<RectMask2D>();
            scroll.viewport = viewport.GetComponent<RectTransform>();

            var content = NewChild(viewport.transform, "Content");
            var crt = content.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.sizeDelta = Vector2.zero;
            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4;
            vlg.padding = new RectOffset(6, 6, 6, 6);
            vlg.childForceExpandWidth = true;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandHeight = false;
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = crt;
            frame.List = content.transform;

            var footerGO = NewChild(d, "Footer");
            var frt2 = footerGO.GetComponent<RectTransform>();
            frt2.anchorMin = new Vector2(0.04f, 0.025f);
            frt2.anchorMax = new Vector2(0.96f, 0.10f);
            frt2.sizeDelta = Vector2.zero;
            frame.Footer = footerGO.transform;

            return frame;
        }

        // ---------------- i pezzi ----------------

        public static GameObject Row(Transform parent)
        {
            var row = NewChild(parent, "Row");
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = RowHeight;
            le.minHeight = RowHeight;
            row.AddComponent<Image>().color = RowBackground;
            return row;
        }

        public static TextMeshProUGUI Label(Transform parent, string text, Color color, float size,
                                            TextAlignmentOptions align, Vector2 aMin, Vector2 aMax,
                                            Vector4 margin, FontStyles style = FontStyles.Normal)
        {
            var go = NewChild(parent, "Label");
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.sizeDelta = Vector2.zero; rt.anchoredPosition = Vector2.zero;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.margin = margin;
            tmp.fontStyle = style;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            var font = TmpFontHelper.Get();
            if (font != null) tmp.font = font;
            return tmp;
        }

        public static void Button(Transform parent, string text, Vector2 aMin, Vector2 aMax,
                                  Color background, Action onClick)
        {
            var go = NewChild(parent, "Btn");
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.sizeDelta = Vector2.zero; rt.anchoredPosition = Vector2.zero;
            go.AddComponent<Image>().color = background;
            var b = go.AddComponent<UnityEngine.UI.Button>();
            b.transition = Selectable.Transition.None;
            b.onClick.AddListener(new UnityAction(() => onClick()));
            Label(go.transform, text, Color.white, 16, TextAlignmentOptions.Center,
                Vector2.zero, Vector2.one, new Vector4(6, 0, 6, 0));
        }

        /// <summary>Una riga di testo piccolo, senza sfondo: una nota a margine.</summary>
        public static void Note(Transform parent, string text)
        {
            var row = NewChild(parent, "Note");
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = RowHeight * 0.8f;
            le.minHeight = RowHeight * 0.8f;
            Label(row.transform, text, TextDisabled, 13, TextAlignmentOptions.MidlineLeft,
                Vector2.zero, Vector2.one, new Vector4(14, 0, 12, 0));
        }

        /// <summary>Il messaggio al centro quando la lista e' vuota.</summary>
        public static void Notice(Transform parent, string text)
        {
            var row = NewChild(parent, "Notice");
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = RowHeight * 3f;
            le.minHeight = RowHeight * 3f;
            Label(row.transform, text, TextSecondary, 16, TextAlignmentOptions.Center,
                Vector2.zero, Vector2.one, new Vector4(16, 0, 16, 0));
        }

        public static void Spacer(Transform parent)
        {
            var go = NewChild(parent, "Spacer");
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 10f;
            le.minHeight = 10f;
        }

        /// <summary>
        /// "1 match", "5 matches". Esiste perche' la scorciatoia `match(es)`
        /// compare a schermo cosi' com'e', e a schermo non ci va.
        /// </summary>
        public static string Count(int n, string one, string many)
        {
            return n + " " + (n == 1 ? one : many);
        }

        public static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        public static void Stretch(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero; rt.anchoredPosition = Vector2.zero;
        }
    }
}
