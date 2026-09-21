using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Lightweight toast notification system that slides in from the top-right.
    /// </summary>
    internal class Toast : MonoBehaviour
    {
        private static Toast _instance;
        private static GameObject _canvasRoot;
        private static RectTransform _toastContainer;

        private const float SlideInDuration = 0.3f;
        private const float DisplayDuration = 4f;
        private const float SlideOutDuration = 0.3f;
        private const float ToastWidth = 400f;
        private const float ToastPadding = 16f;
        private const float TopOffset = 80f; // Below the nav bar

        public static Toast Instance
        {
            get
            {
                if (_instance == null)
                    CreateToastSystem();
                return _instance;
            }
        }

        private static void CreateToastSystem()
        {
            _canvasRoot = new GameObject("MTGAES_ToastCanvas");
            _canvasRoot.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(_canvasRoot);

            var canvas = _canvasRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // 200 NON e' sopra tutto — diverse finestre del mod stanno piu'
            // in alto (ListModal 238, conferme 260). Va bene lo stesso
            // perche' i messaggi compaiono nell'angolo in alto a sinistra,
            // dove quelle finestre non arrivano: se un giorno si spostassero
            // al centro, questo numero andrebbe rivisto.
            canvas.sortingOrder = 200;

            var scaler = _canvasRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            _canvasRoot.AddComponent<GraphicRaycaster>();

            // Container anchored to top-left
            var containerObj = new GameObject("ToastContainer");
            containerObj.transform.SetParent(_canvasRoot.transform, false);
            _toastContainer = containerObj.AddComponent<RectTransform>();
            _toastContainer.anchorMin = new Vector2(0f, 1f);
            _toastContainer.anchorMax = new Vector2(0f, 1f);
            _toastContainer.pivot = new Vector2(0f, 1f);
            _toastContainer.anchoredPosition = new Vector2(20f, -TopOffset);
            _toastContainer.sizeDelta = new Vector2(ToastWidth, 0);

            _instance = _canvasRoot.AddComponent<Toast>();
        }

        public void Show(string message, ToastType type = ToastType.Info, float duration = DisplayDuration)
        {
            StartCoroutine(ShowToastCoroutine(message, type, duration));
        }

        // La durata e' opzionale: chi ha un messaggio piu' lungo di una riga, o
        // che chiede all'utente di fare qualcosa, puo' allungarla senza
        // rallentare tutti gli altri avvisi.
        public static void Info(string message, float duration = DisplayDuration) => Instance.Show(message, ToastType.Info, duration);
        public static void Success(string message, float duration = DisplayDuration) => Instance.Show(message, ToastType.Success, duration);
        public static void Warning(string message, float duration = DisplayDuration) => Instance.Show(message, ToastType.Warning, duration);
        public static void Error(string message, float duration = DisplayDuration) => Instance.Show(message, ToastType.Error, duration);

        private IEnumerator ShowToastCoroutine(string message, ToastType type, float duration)
        {
            var toast = CreateToastObject(message, type);
            var rect = toast.GetComponent<RectTransform>();
            var canvasGroup = toast.GetComponent<CanvasGroup>();

            // Shift existing toasts down
            ShiftExistingToasts(rect.sizeDelta.y + 8f);

            // Start off-screen to the left
            var startPos = new Vector2(-(ToastWidth + 20f), 0f);
            var endPos = Vector2.zero;
            rect.anchoredPosition = startPos;
            canvasGroup.alpha = 0f;

            // Slide in
            float elapsed = 0f;
            while (elapsed < SlideInDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = EaseOutCubic(Mathf.Clamp01(elapsed / SlideInDuration));
                rect.anchoredPosition = Vector2.Lerp(startPos, endPos, t);
                canvasGroup.alpha = t;
                yield return null;
            }
            rect.anchoredPosition = endPos;
            canvasGroup.alpha = 1f;

            // Hold
            yield return new WaitForSecondsRealtime(duration);

            // Slide out
            elapsed = 0f;
            startPos = rect.anchoredPosition;
            var outPos = new Vector2(-(ToastWidth + 20f), startPos.y);
            while (elapsed < SlideOutDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = EaseInCubic(Mathf.Clamp01(elapsed / SlideOutDuration));
                rect.anchoredPosition = Vector2.Lerp(startPos, outPos, t);
                canvasGroup.alpha = 1f - t;
                yield return null;
            }

            Destroy(toast);
        }

        private void ShiftExistingToasts(float amount)
        {
            for (int i = 0; i < _toastContainer.childCount; i++)
            {
                var child = _toastContainer.GetChild(i).GetComponent<RectTransform>();
                if (child != null)
                {
                    var pos = child.anchoredPosition;
                    pos.y -= amount;
                    child.anchoredPosition = pos;
                }
            }
        }

        private GameObject CreateToastObject(string message, ToastType type)
        {
            var toast = new GameObject("Toast");
            toast.transform.SetParent(_toastContainer, false);

            var rect = toast.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(ToastWidth, 0); // Height set after text layout

            var canvasGroup = toast.AddComponent<CanvasGroup>();

            // Background
            var bg = toast.AddComponent<Image>();
            bg.color = GetBackgroundColor(type);

            // Accent bar on the left
            var accent = new GameObject("Accent");
            accent.transform.SetParent(toast.transform, false);
            var accentRect = accent.AddComponent<RectTransform>();
            accentRect.anchorMin = new Vector2(0f, 0f);
            accentRect.anchorMax = new Vector2(0f, 1f);
            accentRect.pivot = new Vector2(0f, 0.5f);
            accentRect.sizeDelta = new Vector2(4f, 0f);
            accentRect.anchoredPosition = Vector2.zero;
            var accentImg = accent.AddComponent<Image>();
            accentImg.color = GetAccentColor(type);

            // Prefix label
            var prefix = new GameObject("Prefix");
            prefix.transform.SetParent(toast.transform, false);
            var prefixRect = prefix.AddComponent<RectTransform>();
            prefixRect.anchorMin = new Vector2(0f, 1f);
            prefixRect.anchorMax = new Vector2(1f, 1f);
            prefixRect.pivot = new Vector2(0f, 1f);
            prefixRect.anchoredPosition = new Vector2(ToastPadding, -8f);
            prefixRect.sizeDelta = new Vector2(-ToastPadding * 2, 20f);
            var prefixText = prefix.AddComponent<TextMeshProUGUI>();
            prefixText.text = $"MTGA+";
            prefixText.fontSize = 11;
            prefixText.fontStyle = FontStyles.Bold;
            prefixText.color = GetAccentColor(type);
            prefixText.alignment = TextAlignmentOptions.TopLeft;

            // Message text
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(toast.transform, false);
            var textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0f, 1f);
            textRect.anchorMax = new Vector2(1f, 1f);
            textRect.pivot = new Vector2(0f, 1f);
            textRect.anchoredPosition = new Vector2(ToastPadding, -26f);
            textRect.sizeDelta = new Vector2(-ToastPadding * 2, 0f);
            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = message;
            tmp.fontSize = 14;
            tmp.color = new Color(0.9f, 0.9f, 0.95f, 1f);
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.enableWordWrapping = true;

            // Force layout to calculate text height
            tmp.ForceMeshUpdate();
            float textHeight = tmp.preferredHeight;
            float totalHeight = textHeight + 40f; // padding top (26 prefix + gap) + padding bottom

            rect.sizeDelta = new Vector2(ToastWidth, totalHeight);
            textRect.sizeDelta = new Vector2(-ToastPadding * 2, textHeight);

            return toast;
        }

        private static Color GetBackgroundColor(ToastType type)
        {
            switch (type)
            {
                case ToastType.Success: return new Color(0.08f, 0.16f, 0.10f, 0.95f);
                case ToastType.Warning: return new Color(0.18f, 0.14f, 0.06f, 0.95f);
                case ToastType.Error: return new Color(0.20f, 0.08f, 0.08f, 0.95f);
                default: return new Color(0.10f, 0.10f, 0.16f, 0.95f);
            }
        }

        private static Color GetAccentColor(ToastType type)
        {
            switch (type)
            {
                case ToastType.Success: return new Color(0.3f, 0.85f, 0.4f, 1f);
                case ToastType.Warning: return new Color(0.95f, 0.75f, 0.2f, 1f);
                case ToastType.Error: return new Color(0.95f, 0.3f, 0.3f, 1f);
                default: return new Color(0.4f, 0.6f, 0.95f, 1f);
            }
        }

        private static float EaseOutCubic(float t) => 1f - Mathf.Pow(1f - t, 3f);
        private static float EaseInCubic(float t) => t * t * t;
    }

    internal enum ToastType
    {
        Info,
        Success,
        Warning,
        Error
    }
}
