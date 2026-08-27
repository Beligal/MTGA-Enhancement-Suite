using System;
using MTGAEnhancementSuite.Features;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Menu shown when the preset button in the deck editor is clicked.
    ///
    /// First row saves the open deck as a new preset; each following row is a
    /// saved preset — click to insert it into the deck, or the x on the right
    /// to delete it.
    ///
    /// Built on its own overlay canvas with a transparent backdrop that
    /// captures outside clicks to dismiss, same shape as
    /// <see cref="FolderContextMenu"/>.
    ///
    /// Sizing is derived from screen height rather than fixed pixels: the
    /// canvas uses ConstantPixelSize, so hardcoded values look tiny on high
    /// resolution displays.
    /// </summary>
    internal static class PresetMenu
    {
        /// <summary>Beyond this many presets the rows no longer fit: v1 has no scrolling.</summary>
        private const int MaxRows = 12;

        // Palette condivisa con EnhancementSuitePanel e gli altri pannelli del
        // mod, cosi il menu non stona.
        private static readonly Color PanelBackground = new Color(0.10f, 0.10f, 0.18f, 0.98f);
        private static readonly Color RowBackground   = new Color(0.15f, 0.15f, 0.25f, 0.9f);
        private static readonly Color AccentBlue      = new Color(0.30f, 0.50f, 0.70f, 0.9f);
        private static readonly Color AccentText      = new Color(0.55f, 0.75f, 0.95f);
        private static readonly Color TextPrimary     = new Color(0.85f, 0.85f, 0.90f);
        private static readonly Color TextSecondary   = new Color(0.60f, 0.60f, 0.70f);
        private static readonly Color TextDisabled    = new Color(0.40f, 0.40f, 0.50f);
        private static readonly Color DangerRed       = new Color(0.75f, 0.20f, 0.20f, 1f);

        public static void Show(Vector2 screenPos)
        {
            // --- dimensioni proporzionate allo schermo ---
            float rowHeight = Mathf.Max(29f, Screen.height * 0.034f);
            float menuWidth = Mathf.Clamp(Screen.width * 0.185f, 255f, 440f);
            float fontSize = Mathf.Max(14f, rowHeight * 0.45f);
            float padding = Mathf.Max(6f, rowHeight * 0.20f);

            var presets = CardPresetManager.All;
            int shown = Mathf.Min(presets.Count, MaxRows);
            float height = padding * 2 + rowHeight * (shown + 1);

            var root = new GameObject("MTGAES_PresetMenu");
            UnityEngine.Object.DontDestroyOnLoad(root);

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 225;
            root.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            root.AddComponent<GraphicRaycaster>();

            // Transparent full-screen backdrop: any click outside closes the menu.
            var backdrop = NewChild(root.transform, "Backdrop");
            var brt = backdrop.GetComponent<RectTransform>();
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
            backdrop.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            var bgBtn = backdrop.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(() => UnityEngine.Object.Destroy(root)));

            var menu = NewChild(root.transform, "Menu");
            var rt = menu.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0f, 1f);      // top-left corner sits at the anchor point
            rt.sizeDelta = new Vector2(menuWidth, height);
            rt.anchoredPosition = new Vector2(
                Mathf.Clamp(screenPos.x, 4f, Mathf.Max(4f, Screen.width - menuWidth - 4f)),
                Mathf.Clamp(screenPos.y, height + 4f, Screen.height - 4f));

            // Stessa palette degli altri pannelli MTGA+: fondo blu notte,
            // bordo blu acciaio (lo stesso della tab attiva del pannello).
            menu.AddComponent<Image>().color = PanelBackground;

            var border = NewChild(menu.transform, "Border");
            var bort = border.GetComponent<RectTransform>();
            bort.anchorMin = Vector2.zero; bort.anchorMax = Vector2.one;
            bort.offsetMin = new Vector2(-2f, -2f); bort.offsetMax = new Vector2(2f, 2f);
            var borderImg = border.AddComponent<Image>();
            borderImg.color = AccentBlue;
            borderImg.raycastTarget = false;
            border.transform.SetAsFirstSibling();

            float y = -padding;

            // --- save row ---
            bool canSave = CardPresetManager.IsDeckBuilderReady();
            MakeRow(menu.transform, ref y, rowHeight, padding, fontSize,
                canSave ? "+   Save current deck" : "+   No deck open",
                canSave ? AccentText : TextDisabled,
                canSave ? (Action)(() =>
                {
                    UnityEngine.Object.Destroy(root);
                    CardPresetManager.SaveCurrentDeck();
                }) : null,
                null);

            // --- one row per preset ---
            for (int i = 0; i < shown; i++)
            {
                var preset = presets[i];
                if (preset == null) continue;
                var id = preset.Id;
                string label = $"{preset.Name}   ({preset.TotalCards})";

                MakeRow(menu.transform, ref y, rowHeight, padding, fontSize, label,
                    TextPrimary,
                    () =>
                    {
                        UnityEngine.Object.Destroy(root);
                        CardPresetManager.Apply(preset);
                    },
                    () =>
                    {
                        UnityEngine.Object.Destroy(root);
                        CardPresetManager.Delete(id);
                    });
            }

            if (presets.Count > MaxRows)
                Plugin.Log.LogWarning($"PresetMenu: {presets.Count} presets, showing {MaxRows} (no scrolling yet).");
        }

        /// <summary>
        /// One row. A null <paramref name="onClick"/> renders it inert (greyed);
        /// a non-null <paramref name="onDelete"/> adds the x on the right.
        /// </summary>
        private static void MakeRow(Transform parent, ref float y, float rowHeight, float padding,
                                    float fontSize, string text, Color color,
                                    Action onClick, Action onDelete)
        {
            float deleteSize = rowHeight * 0.62f;

            var row = NewChild(parent, "Row");
            var rrt = row.GetComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0f, 1f);
            rrt.anchorMax = new Vector2(1f, 1f);
            rrt.pivot = new Vector2(0.5f, 1f);
            rrt.offsetMin = new Vector2(padding, 0f);
            rrt.offsetMax = new Vector2(-padding, 0f);
            rrt.sizeDelta = new Vector2(-padding * 2f, rowHeight);
            rrt.anchoredPosition = new Vector2(0f, y);
            y -= rowHeight;

            var bg = row.AddComponent<Image>();
            bg.color = RowBackground;

            if (onClick != null)
            {
                var btn = row.AddComponent<Button>();
                btn.targetGraphic = bg;
                var colors = btn.colors;
                colors.highlightedColor = AccentBlue;
                btn.colors = colors;
                btn.onClick.AddListener(new UnityAction(() => onClick()));
            }

            var font = TmpFontHelper.Get();

            var labelGO = NewChild(row.transform, "Label");
            var lrt = labelGO.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(rowHeight * 0.36f, 0f);
            lrt.offsetMax = new Vector2(onDelete != null ? -(deleteSize + 10f) : -10f, 0f);
            var label = labelGO.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.color = color;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            if (font != null) label.font = font;

            if (onDelete == null) return;

            var del = NewChild(row.transform, "Delete");
            var drt = del.GetComponent<RectTransform>();
            drt.anchorMin = new Vector2(1f, 0.5f);
            drt.anchorMax = new Vector2(1f, 0.5f);
            drt.pivot = new Vector2(1f, 0.5f);
            drt.sizeDelta = new Vector2(deleteSize, deleteSize);
            drt.anchoredPosition = new Vector2(-6f, 0f);
            var dbg = del.AddComponent<Image>();
            dbg.color = RowBackground;
            var dbtn = del.AddComponent<Button>();
            dbtn.targetGraphic = dbg;
            var dcolors = dbtn.colors;
            dcolors.highlightedColor = DangerRed;
            dbtn.colors = dcolors;
            dbtn.onClick.AddListener(new UnityAction(() => onDelete()));

            var xGO = NewChild(del.transform, "X");
            var xrt = xGO.GetComponent<RectTransform>();
            xrt.anchorMin = Vector2.zero; xrt.anchorMax = Vector2.one;
            xrt.offsetMin = Vector2.zero; xrt.offsetMax = Vector2.zero;
            var x = xGO.AddComponent<TextMeshProUGUI>();
            x.text = "x";
            x.fontSize = fontSize;
            x.color = TextSecondary;
            x.alignment = TextAlignmentOptions.Center;
            if (font != null) x.font = font;
        }

        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }
    }
}
