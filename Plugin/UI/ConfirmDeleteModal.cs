using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Two-button confirmation overlay used before bulk-deleting decks.
    /// Built on a top-level ScreenSpaceOverlay canvas so it survives any
    /// MTGA scene transitions; clicking the dim backdrop or Cancel dismisses.
    /// </summary>
    internal static class ConfirmDeleteModal
    {
        public static void Show(int deckCount, Action onConfirm)
        {
            var modal = new GameObject("MTGAES_ConfirmDeleteModal");
            UnityEngine.Object.DontDestroyOnLoad(modal);

            var canvas = modal.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Stessa ragione di ConfirmActionModal.ConfirmSortingOrder: una
            // conferma che nasce dietro la finestra che l'ha aperta non
            // conferma niente. 260 = sopra ogni finestra del mod.
            canvas.sortingOrder = 260;
            var scaler = modal.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            modal.AddComponent<GraphicRaycaster>();

            // Dim backdrop — click to cancel.
            var bg = NewChild(modal.transform, "Backdrop");
            StretchFull(bg);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = new Color(0, 0, 0, 0.7f);
            var bgBtn = bg.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(() => UnityEngine.Object.Destroy(modal)));

            // Dialog
            var dialog = NewChild(modal.transform, "Dialog");
            var dRt = dialog.GetComponent<RectTransform>();
            dRt.anchorMin = new Vector2(0.35f, 0.40f);
            dRt.anchorMax = new Vector2(0.65f, 0.60f);
            dRt.sizeDelta = Vector2.zero;
            dialog.AddComponent<Image>().color = new Color(0.10f, 0.10f, 0.18f, 0.98f);

            // Title
            var title = NewChild(dialog.transform, "Title");
            var tRt = title.GetComponent<RectTransform>();
            tRt.anchorMin = new Vector2(0.05f, 0.65f);
            tRt.anchorMax = new Vector2(0.95f, 0.93f);
            tRt.sizeDelta = Vector2.zero;
            var titleTmp = title.AddComponent<TextMeshProUGUI>();
            titleTmp.text = deckCount == 1 ? "Delete 1 deck?" : $"Delete {deckCount} decks?";
            titleTmp.fontSize = 28;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = Color.white;
            titleTmp.alignment = TextAlignmentOptions.Center;

            // Body
            var body = NewChild(dialog.transform, "Body");
            var bRt = body.GetComponent<RectTransform>();
            bRt.anchorMin = new Vector2(0.05f, 0.35f);
            bRt.anchorMax = new Vector2(0.95f, 0.62f);
            bRt.sizeDelta = Vector2.zero;
            var bodyTmp = body.AddComponent<TextMeshProUGUI>();
            bodyTmp.text = "This cannot be undone.";
            bodyTmp.fontSize = 18;
            bodyTmp.color = new Color(0.85f, 0.85f, 0.90f);
            bodyTmp.alignment = TextAlignmentOptions.Center;

            // Cancel button (left)
            MakeBtn(dialog.transform, "CancelBtn", "Cancel",
                new Vector2(0.08f, 0.08f), new Vector2(0.46f, 0.28f),
                new Color(0.3f, 0.3f, 0.4f, 1f),
                () => UnityEngine.Object.Destroy(modal));

            // Delete button (right, red)
            MakeBtn(dialog.transform, "ConfirmBtn", "Delete",
                new Vector2(0.54f, 0.08f), new Vector2(0.92f, 0.28f),
                new Color(0.75f, 0.20f, 0.20f, 1f),
                () =>
                {
                    UnityEngine.Object.Destroy(modal);
                    try { onConfirm?.Invoke(); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"ConfirmDeleteModal callback: {ex.Message}"); }
                });
        }

        // ---- helpers (mirrors EnhancementSuitePanel's pattern) ----
        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
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

        private static void MakeBtn(Transform parent, string name, string label,
            Vector2 anchorMin, Vector2 anchorMax, Color bgColor, UnityAction onClick)
        {
            var btn = NewChild(parent, name);
            var rt = btn.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.sizeDelta = Vector2.zero;
            btn.AddComponent<Image>().color = bgColor;
            var b = btn.AddComponent<Button>();
            b.transition = Selectable.Transition.None;
            b.onClick.AddListener(onClick);

            var t = NewChild(btn.transform, "Text");
            StretchFull(t);
            var tmp = t.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 20;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
        }
    }
}
