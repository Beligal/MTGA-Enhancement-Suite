using System;
using MTGAEnhancementSuite.Features;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Il menu che compare col tasto destro su una tessera della lista mazzi.
    ///
    /// Nato il 2026-09-20 per necessita': il tasto destro apriva direttamente
    /// l'editor del nome, e le statistiche avevano bisogno dello stesso gesto.
    /// Una sola azione per click non regge l'aggiunta della seconda — e il
    /// tasto destro sulle intestazioni delle cartelle apre gia' un menu, quindi
    /// questo e' anche il gesto che l'utente si aspetta.
    /// </summary>
    internal static class DeckContextMenu
    {
        private static readonly Color PanelBackground = new Color(0.10f, 0.10f, 0.18f, 0.98f);
        private static readonly Color RowBackground   = new Color(0.15f, 0.15f, 0.25f, 1f);
        private static readonly Color AccentBlue      = new Color(0.30f, 0.50f, 0.70f, 1f);
        private static readonly Color TextPrimary     = new Color(0.85f, 0.85f, 0.90f);

        private static GameObject _root;

        public static void Show(Guid deckId, string deckName)
        {
            Close();
            if (deckId == Guid.Empty) return;

            float rowHeight = Mathf.Max(30f, Screen.height * 0.035f);
            float width = Mathf.Clamp(Screen.width * 0.16f, 230f, 380f);
            float fontSize = Mathf.Max(14f, rowHeight * 0.45f);
            float padding = Mathf.Max(6f, rowHeight * 0.2f);
            float height = padding * 2 + rowHeight * 3;

            var mouse = Mouse.current;
            Vector2 at = mouse != null ? mouse.position.ReadValue()
                                       : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            _root = new GameObject("MTGAES_DeckContextMenu");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 235;
            _root.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            _root.AddComponent<GraphicRaycaster>();

            var backdrop = NewChild(_root.transform, "Backdrop");
            var brt = backdrop.GetComponent<RectTransform>();
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
            backdrop.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            var bgBtn = backdrop.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(Close));

            // Cornice fuori, pannello dentro: un bordo figlio ricoprirebbe il
            // pannello invece di contornarlo (MISTAKES.md #28).
            var frame = NewChild(_root.transform, "Frame");
            var frt = frame.GetComponent<RectTransform>();
            frt.anchorMin = Vector2.zero;
            frt.anchorMax = Vector2.zero;
            frt.pivot = new Vector2(0f, 1f);
            frt.sizeDelta = new Vector2(width, height);
            frt.anchoredPosition = new Vector2(
                Mathf.Clamp(at.x, 4f, Mathf.Max(4f, Screen.width - width - 4f)),
                Mathf.Clamp(at.y, height + 4f, Screen.height - 4f));
            frame.AddComponent<Image>().color = AccentBlue;

            var menu = NewChild(frame.transform, "Menu");
            var mrt = menu.GetComponent<RectTransform>();
            mrt.anchorMin = Vector2.zero; mrt.anchorMax = Vector2.one;
            mrt.offsetMin = new Vector2(2f, 2f); mrt.offsetMax = new Vector2(-2f, -2f);
            menu.AddComponent<Image>().color = PanelBackground;

            float y = -padding;
            Row(menu.transform, ref y, rowHeight, padding, fontSize, "Deck statistics",
                () => { Close(); DeckStatsWindow.Show(deckId, deckName); });
            Row(menu.transform, ref y, rowHeight, padding, fontSize, "Compare with...",
                () => { Close(); DeckCompareWindow.PickAndShow(deckId); });
            Row(menu.transform, ref y, rowHeight, padding, fontSize, "Style name...",
                () => { Close(); DeckNameController.OpenForDeck(deckId); });
        }

        public static void Close()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        private static void Row(Transform parent, ref float y, float rowHeight, float padding,
                                float fontSize, string label, Action onClick)
        {
            var row = NewChild(parent, "Row");
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(padding, 0f);
            rt.offsetMax = new Vector2(-padding, 0f);
            rt.sizeDelta = new Vector2(-padding * 2f, rowHeight);
            rt.anchoredPosition = new Vector2(0f, y);
            y -= rowHeight;

            var bg = row.AddComponent<Image>();
            bg.color = RowBackground;
            var btn = row.AddComponent<Button>();
            btn.targetGraphic = bg;
            var colors = btn.colors;
            colors.highlightedColor = AccentBlue;
            btn.colors = colors;
            btn.onClick.AddListener(new UnityAction(() => onClick()));

            var labelGO = NewChild(row.transform, "Label");
            var lrt = labelGO.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(rowHeight * 0.36f, 0f);
            lrt.offsetMax = new Vector2(-10f, 0f);
            var tmp = labelGO.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = fontSize;
            tmp.color = TextPrimary;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.raycastTarget = false;
            var font = TmpFontHelper.Get();
            if (font != null) tmp.font = font;
        }

        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }
    }
}
