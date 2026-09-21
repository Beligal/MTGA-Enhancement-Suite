using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Generic two-button confirmation overlay with caller-supplied title,
    /// body, and confirm-button label. Same construction as
    /// <see cref="ConfirmDeleteModal"/> but for non-destructive actions
    /// (the confirm button is green rather than red).
    /// </summary>
    internal static class ConfirmActionModal
    {
        /// <summary>
        /// Sopra qualunque finestra del mod.
        ///
        /// **Il difetto che ha prodotto questa costante (2026-09-21):** la
        /// conferma stava a 200 e la finestra dei replay, come ogni
        /// <see cref="ListModal"/>, sta a 238. Chiedendo di cancellare qualcosa
        /// da li', la domanda nasceva DIETRO la finestra che l'aveva aperta: per
        /// vederla bisognava chiudere quella davanti, e una conferma che si
        /// deve andare a cercare non sta confermando niente.
        ///
        /// La scala delle finestre del mod, dal basso: pannello MTGA+ 100,
        /// suggerimenti 150, menu cartelle 220, menu preset e Sparky 225, menu
        /// tessera 235, ListModal 238, importazione 240, editor dei nomi 245,
        /// scelta colore 250. Una conferma deve stare sopra tutte, perche' puo'
        /// essere aperta da qualunque di loro.
        /// </summary>
        private const int ConfirmSortingOrder = 260;

        /// <summary>Single-button informational modal (just an OK to dismiss).</summary>
        public static void ShowInfo(string title, string body)
        {
            var modal = new GameObject("MTGAES_InfoModal");
            UnityEngine.Object.DontDestroyOnLoad(modal);

            var canvas = modal.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = ConfirmSortingOrder;
            var scaler = modal.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            modal.AddComponent<GraphicRaycaster>();

            var bg = NewChild(modal.transform, "Backdrop");
            StretchFull(bg);
            bg.AddComponent<Image>().color = new Color(0, 0, 0, 0.7f);
            var bgBtn = bg.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(() => UnityEngine.Object.Destroy(modal)));

            var dialog = NewChild(modal.transform, "Dialog");
            var dRt = dialog.GetComponent<RectTransform>();
            dRt.anchorMin = new Vector2(0.34f, 0.40f);
            dRt.anchorMax = new Vector2(0.66f, 0.60f);
            dRt.sizeDelta = Vector2.zero;
            dialog.AddComponent<Image>().color = new Color(0.10f, 0.10f, 0.18f, 0.98f);

            var titleGO = NewChild(dialog.transform, "Title");
            var tRt = titleGO.GetComponent<RectTransform>();
            tRt.anchorMin = new Vector2(0.05f, 0.62f);
            tRt.anchorMax = new Vector2(0.95f, 0.92f);
            tRt.sizeDelta = Vector2.zero;
            var titleTmp = titleGO.AddComponent<TextMeshProUGUI>();
            var font = TmpFontHelper.Get();
            if (font != null) titleTmp.font = font;
            titleTmp.text = title ?? "";
            titleTmp.fontSize = 24;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = Color.white;
            titleTmp.alignment = TextAlignmentOptions.Center;
            titleTmp.extraPadding = false;

            var bodyGO = NewChild(dialog.transform, "Body");
            var bRt = bodyGO.GetComponent<RectTransform>();
            bRt.anchorMin = new Vector2(0.07f, 0.30f);
            bRt.anchorMax = new Vector2(0.93f, 0.60f);
            bRt.sizeDelta = Vector2.zero;
            var bodyTmp = bodyGO.AddComponent<TextMeshProUGUI>();
            if (font != null) bodyTmp.font = font;
            bodyTmp.text = body ?? "";
            bodyTmp.fontSize = 16;
            bodyTmp.color = new Color(0.85f, 0.85f, 0.90f);
            bodyTmp.alignment = TextAlignmentOptions.Center;
            bodyTmp.extraPadding = false;

            MakeBtn(dialog.transform, "OkBtn", "OK",
                new Vector2(0.32f, 0.08f), new Vector2(0.68f, 0.26f),
                new Color(0.20f, 0.45f, 0.65f, 1f),
                () => UnityEngine.Object.Destroy(modal));
        }

        public static void Show(string title, string body, string confirmLabel, Action onConfirm)
        {
            var modal = new GameObject("MTGAES_ConfirmActionModal");
            UnityEngine.Object.DontDestroyOnLoad(modal);

            var canvas = modal.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = ConfirmSortingOrder;
            var scaler = modal.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            modal.AddComponent<GraphicRaycaster>();

            var bg = NewChild(modal.transform, "Backdrop");
            StretchFull(bg);
            bg.AddComponent<Image>().color = new Color(0, 0, 0, 0.7f);
            var bgBtn = bg.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(() => UnityEngine.Object.Destroy(modal)));

            var dialog = NewChild(modal.transform, "Dialog");
            var dRt = dialog.GetComponent<RectTransform>();
            dRt.anchorMin = new Vector2(0.34f, 0.38f);
            dRt.anchorMax = new Vector2(0.66f, 0.62f);
            dRt.sizeDelta = Vector2.zero;
            dialog.AddComponent<Image>().color = new Color(0.10f, 0.10f, 0.18f, 0.98f);

            var titleGO = NewChild(dialog.transform, "Title");
            var tRt = titleGO.GetComponent<RectTransform>();
            tRt.anchorMin = new Vector2(0.05f, 0.66f);
            tRt.anchorMax = new Vector2(0.95f, 0.93f);
            tRt.sizeDelta = Vector2.zero;
            var titleTmp = titleGO.AddComponent<TextMeshProUGUI>();
            var font = TmpFontHelper.Get();
            if (font != null) titleTmp.font = font;
            titleTmp.text = title ?? "Are you sure?";
            titleTmp.fontSize = 24;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = Color.white;
            titleTmp.alignment = TextAlignmentOptions.Center;
            titleTmp.extraPadding = false;

            var bodyGO = NewChild(dialog.transform, "Body");
            var bRt = bodyGO.GetComponent<RectTransform>();
            bRt.anchorMin = new Vector2(0.07f, 0.32f);
            bRt.anchorMax = new Vector2(0.93f, 0.64f);
            bRt.sizeDelta = Vector2.zero;
            var bodyTmp = bodyGO.AddComponent<TextMeshProUGUI>();
            if (font != null) bodyTmp.font = font;
            bodyTmp.text = body ?? "";
            bodyTmp.fontSize = 16;
            bodyTmp.color = new Color(0.85f, 0.85f, 0.90f);
            bodyTmp.alignment = TextAlignmentOptions.Center;
            bodyTmp.extraPadding = false;

            MakeBtn(dialog.transform, "CancelBtn", "Cancel",
                new Vector2(0.08f, 0.08f), new Vector2(0.46f, 0.26f),
                new Color(0.3f, 0.3f, 0.4f, 1f),
                () => UnityEngine.Object.Destroy(modal));

            MakeBtn(dialog.transform, "ConfirmBtn", confirmLabel ?? "Confirm",
                new Vector2(0.54f, 0.08f), new Vector2(0.92f, 0.26f),
                new Color(0.20f, 0.55f, 0.30f, 1f),
                () =>
                {
                    UnityEngine.Object.Destroy(modal);
                    try { onConfirm?.Invoke(); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"ConfirmActionModal callback: {ex.Message}"); }
                });
        }

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
            var font = TmpFontHelper.Get();
            if (font != null) tmp.font = font;
            tmp.text = label;
            tmp.fontSize = 18;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.extraPadding = false;
        }
    }
}
