using System;
using MTGAEnhancementSuite.State;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Selettore di colore: quadrato saturazione/luminosita', barra della tinta,
    /// barra dell'opacita', casella esadecimale e anteprima.
    ///
    /// Le immagini sono <see cref="Texture2D"/> generate a runtime invece che
    /// asset: il quadrato si ridisegna solo quando cambia la tinta (128x128,
    /// qualche decimo di millisecondo), la barra della tinta una volta sola.
    /// Cosi' non c'e' niente da spedire in <c>assets/</c> e niente da caricare.
    ///
    /// Il colore si legge dalla posizione del click con
    /// <c>RectTransformUtility.ScreenPointToLocalPointInRectangle</c>: su un
    /// canvas in overlay la camera da passare e' <c>null</c>, ed e' proprio
    /// quello che arriva in <c>eventData.pressEventCamera</c>.
    /// </summary>
    internal static class ColorPicker
    {
        private static readonly Color PanelBackground = new Color(0.10f, 0.10f, 0.18f, 0.99f);
        private static readonly Color FieldBackground = new Color(0.18f, 0.18f, 0.28f, 1f);
        private static readonly Color AccentBlue      = new Color(0.30f, 0.50f, 0.70f, 1f);
        private static readonly Color TextPrimary     = new Color(0.85f, 0.85f, 0.90f);
        private static readonly Color TextSecondary   = new Color(0.62f, 0.62f, 0.72f);
        private static readonly Color TextDisabled    = new Color(0.45f, 0.45f, 0.55f);

        private const int SvSize = 128;
        private const int BarWidth = 180;

        private static GameObject _root;
        private static Action<Color32> _onPick;

        private static float _hue, _sat, _val;
        private static float _alpha = 1f;

        private static Texture2D _svTexture;
        private static Image _svImage;
        private static RectTransform _svRect, _svMarker;
        private static RectTransform _hueRect, _hueMarker;
        private static RectTransform _alphaRect, _alphaMarker;
        private static Image _alphaImage;
        private static Texture2D _alphaTexture;
        private static Image _previewImage;
        private static TMP_InputField _hexInput;
        private static bool _suppressHexEcho;
        private static bool _withAlpha;

        // -----------------------------------------------------------------
        // Ingresso
        // -----------------------------------------------------------------
        public static void Show(Color32 initial, bool withAlpha, string title, Action<Color32> onPick)
        {
            if (onPick == null) return;
            _onPick = onPick;
            _withAlpha = withAlpha;

            Color.RGBToHSV(new Color(initial.r / 255f, initial.g / 255f, initial.b / 255f),
                           out _hue, out _sat, out _val);
            _alpha = withAlpha ? initial.a / 255f : 1f;

            Build(title ?? "Pick a color");
            RedrawSv();

            // I segnalini si posizionano su rect.width/height: appena costruito
            // il riquadro quelle misure sono ancora zero e finirebbero tutti al
            // centro, cioe' su un colore che non e' quello di partenza. Una
            // passata di layout prima di collocarli.
            Canvas.ForceUpdateCanvases();
            RefreshAll();
        }

        public static void Close()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _onPick = null;
            _svImage = null;
            _svRect = _svMarker = null;
            _hueRect = _hueMarker = null;
            _alphaRect = _alphaMarker = null;
            _alphaImage = null;
            _previewImage = null;
            _hexInput = null;

            // Le texture non sono figlie di nessun GameObject: Destroy del
            // riquadro non le porta via, e restano in memoria fino all'uscita.
            if (_svTexture != null) { UnityEngine.Object.Destroy(_svTexture); _svTexture = null; }
            if (_alphaTexture != null) { UnityEngine.Object.Destroy(_alphaTexture); _alphaTexture = null; }
        }

        private static Color32 Current()
        {
            var rgb = Color.HSVToRGB(_hue, _sat, _val);
            return new Color32(
                (byte)Mathf.RoundToInt(rgb.r * 255f),
                (byte)Mathf.RoundToInt(rgb.g * 255f),
                (byte)Mathf.RoundToInt(rgb.b * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(_alpha) * 255f));
        }

        // -----------------------------------------------------------------
        // Aree cliccabili
        // -----------------------------------------------------------------

        /// <summary>
        /// Un'area che riporta dove e' stato premuto, in coordinate 0..1.
        /// Gestisce anche il trascinamento, altrimenti si potrebbe solo
        /// picchiettare e scegliere un colore diventerebbe un lavoro.
        /// </summary>
        internal sealed class PickerArea : MonoBehaviour, IPointerDownHandler, IDragHandler
        {
            public Action<Vector2> OnPick;

            public void OnPointerDown(PointerEventData e) => Report(e);
            public void OnDrag(PointerEventData e) => Report(e);

            private void Report(PointerEventData e)
            {
                var rt = transform as RectTransform;
                if (rt == null || OnPick == null) return;

                Vector2 local;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        rt, e.position, e.pressEventCamera, out local)) return;

                var r = rt.rect;
                float x = Mathf.Clamp01((local.x - r.xMin) / Mathf.Max(0.0001f, r.width));
                float y = Mathf.Clamp01((local.y - r.yMin) / Mathf.Max(0.0001f, r.height));
                OnPick(new Vector2(x, y));
            }
        }

        // -----------------------------------------------------------------
        // Texture
        // -----------------------------------------------------------------
        private static void RedrawSv()
        {
            if (_svTexture == null)
            {
                _svTexture = new Texture2D(SvSize, SvSize, TextureFormat.RGBA32, false);
                _svTexture.wrapMode = TextureWrapMode.Clamp;
                _svTexture.filterMode = FilterMode.Bilinear;
            }

            var pixels = new Color32[SvSize * SvSize];
            for (int y = 0; y < SvSize; y++)
            {
                float v = y / (float)(SvSize - 1);
                for (int x = 0; x < SvSize; x++)
                {
                    float s = x / (float)(SvSize - 1);
                    var c = Color.HSVToRGB(_hue, s, v);
                    pixels[y * SvSize + x] = new Color32(
                        (byte)Mathf.RoundToInt(c.r * 255f),
                        (byte)Mathf.RoundToInt(c.g * 255f),
                        (byte)Mathf.RoundToInt(c.b * 255f), 255);
                }
            }
            _svTexture.SetPixels32(pixels);
            _svTexture.Apply(false);

            // Lo Sprite si crea una volta sola e poi si tiene: l'Image legge la
            // texture, e riscrivere i pixel basta a farla ridisegnare.
            // Ricrearlo a ogni giro vorrebbe dire un oggetto Unity nuovo per
            // ogni frame di trascinamento sulla barra della tinta, e quelli non
            // se ne vanno da soli come la memoria normale.
            if (_svImage != null && _svImage.sprite == null)
                _svImage.sprite = Sprite.Create(_svTexture, new Rect(0, 0, SvSize, SvSize), new Vector2(0.5f, 0.5f));
        }

        private static Texture2D BuildHueTexture()
        {
            var tex = new Texture2D(BarWidth, 1, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            var px = new Color32[BarWidth];
            for (int x = 0; x < BarWidth; x++)
            {
                var c = Color.HSVToRGB(x / (float)(BarWidth - 1), 1f, 1f);
                px[x] = new Color32((byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), 255);
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            return tex;
        }

        /// <summary>
        /// La barra dell'opacita' mostra il colore corrente da trasparente a
        /// pieno. Il trasparente si vede solo se c'e' qualcosa dietro: lo
        /// componiamo su una scacchiera, come fanno tutti i programmi di
        /// disegno, altrimenti a sinistra ci sarebbe solo il fondo del pannello
        /// e non si capirebbe che significa.
        /// </summary>
        private static void RedrawAlpha()
        {
            if (!_withAlpha) return;
            if (_alphaTexture == null)
            {
                _alphaTexture = new Texture2D(BarWidth, 8, TextureFormat.RGBA32, false);
                _alphaTexture.wrapMode = TextureWrapMode.Clamp;
            }

            var rgb = Color.HSVToRGB(_hue, _sat, _val);
            var px = new Color32[BarWidth * 8];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < BarWidth; x++)
                {
                    float a = x / (float)(BarWidth - 1);
                    bool light = ((x / 6) + (y / 4)) % 2 == 0;
                    float bgv = light ? 0.55f : 0.35f;
                    var c = new Color(
                        Mathf.Lerp(bgv, rgb.r, a),
                        Mathf.Lerp(bgv, rgb.g, a),
                        Mathf.Lerp(bgv, rgb.b, a), 1f);
                    px[y * BarWidth + x] = new Color32((byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), 255);
                }
            }
            _alphaTexture.SetPixels32(px);
            _alphaTexture.Apply(false);

            if (_alphaImage != null && _alphaImage.sprite == null)
                _alphaImage.sprite = Sprite.Create(_alphaTexture, new Rect(0, 0, BarWidth, 8), new Vector2(0.5f, 0.5f));
        }

        // -----------------------------------------------------------------
        // Aggiornamento
        // -----------------------------------------------------------------
        private static void RefreshAll()
        {
            var col = Current();

            if (_previewImage != null) _previewImage.color = new Color32(col.r, col.g, col.b, 255);

            if (_hexInput != null && !_suppressHexEcho)
            {
                _suppressHexEcho = true;
                _hexInput.text = StyledName.ShortestColor(col);
                _suppressHexEcho = false;
            }

            if (_svMarker != null && _svRect != null)
                _svMarker.anchoredPosition = new Vector2(
                    (_sat - 0.5f) * _svRect.rect.width,
                    (_val - 0.5f) * _svRect.rect.height);

            if (_hueMarker != null && _hueRect != null)
                _hueMarker.anchoredPosition = new Vector2((_hue - 0.5f) * _hueRect.rect.width, 0f);

            if (_withAlpha && _alphaMarker != null && _alphaRect != null)
                _alphaMarker.anchoredPosition = new Vector2((_alpha - 0.5f) * _alphaRect.rect.width, 0f);

            RedrawAlpha();
        }

        // -----------------------------------------------------------------
        // Costruzione
        // -----------------------------------------------------------------
        private static void Build(string title)
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);

            _root = new GameObject("MTGAES_ColorPicker");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 250;      // sopra l'editor del nome (245)
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            _root.AddComponent<GraphicRaycaster>();

            var bg = NewChild(_root.transform, "Backdrop");
            Stretch(bg);
            bg.AddComponent<Image>().color = new Color(0, 0, 0, 0.55f);
            var bgBtn = bg.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(Close));

            // Cornice fuori, pannello dentro (MISTAKES.md #28).
            var frame = NewChild(_root.transform, "Frame");
            var frRt = frame.GetComponent<RectTransform>();
            frRt.anchorMin = new Vector2(0.355f, 0.17f);
            frRt.anchorMax = new Vector2(0.645f, 0.83f);
            frRt.sizeDelta = Vector2.zero;
            frame.AddComponent<Image>().color = AccentBlue;

            var panel = NewChild(frame.transform, "Panel");
            var pRt = panel.GetComponent<RectTransform>();
            pRt.anchorMin = Vector2.zero; pRt.anchorMax = Vector2.one;
            pRt.offsetMin = new Vector2(2f, 2f); pRt.offsetMax = new Vector2(-2f, -2f);
            panel.AddComponent<Image>().color = PanelBackground;
            var p = panel.transform;

            Label(p, title, Color.white, 20, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.06f, 0.935f), new Vector2(0.94f, 0.985f), FontStyles.Bold);

            BuildSvSquare(p);
            BuildHueBar(p);
            if (_withAlpha) BuildAlphaBar(p);
            BuildPreviewAndHex(p);
            BuildButtons(p);
        }

        private static void BuildSvSquare(Transform p)
        {
            var go = NewChild(p, "SV");
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.06f, _withAlpha ? 0.450f : 0.395f);
            rt.anchorMax = new Vector2(0.94f, 0.920f);
            rt.sizeDelta = Vector2.zero;
            _svRect = rt;

            _svImage = go.AddComponent<Image>();
            _svImage.type = Image.Type.Simple;

            var area = go.AddComponent<PickerArea>();
            area.OnPick = n =>
            {
                _sat = n.x; _val = n.y;
                RefreshAll();
            };

            _svMarker = Marker(go.transform, 14f);
        }

        private static void BuildHueBar(Transform p)
        {
            float y0 = _withAlpha ? 0.370f : 0.315f;
            var go = NewChild(p, "Hue");
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.06f, y0);
            rt.anchorMax = new Vector2(0.94f, y0 + 0.055f);
            rt.sizeDelta = Vector2.zero;
            _hueRect = rt;

            var img = go.AddComponent<Image>();
            var tex = BuildHueTexture();
            img.sprite = Sprite.Create(tex, new Rect(0, 0, BarWidth, 1), new Vector2(0.5f, 0.5f));

            var area = go.AddComponent<PickerArea>();
            area.OnPick = n =>
            {
                _hue = n.x;
                RedrawSv();
                RefreshAll();
            };

            _hueMarker = Marker(go.transform, 8f, full: true);
        }

        private static void BuildAlphaBar(Transform p)
        {
            var go = NewChild(p, "Alpha");
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.06f, 0.295f);
            rt.anchorMax = new Vector2(0.94f, 0.350f);
            rt.sizeDelta = Vector2.zero;
            _alphaRect = rt;

            _alphaImage = go.AddComponent<Image>();

            var area = go.AddComponent<PickerArea>();
            area.OnPick = n => { _alpha = n.x; RefreshAll(); };

            _alphaMarker = Marker(go.transform, 8f, full: true);
        }

        private static RectTransform Marker(Transform parent, float size, bool full = false)
        {
            var go = NewChild(parent, "Marker");
            var rt = go.GetComponent<RectTransform>();
            if (full)
            {
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(size * 0.45f, 6f);
            }
            else
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(size, size);
            }
            var img = go.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.95f);
            img.raycastTarget = false;      // il click deve arrivare all'area sotto
            return rt;
        }

        private static void BuildPreviewAndHex(Transform p)
        {
            float y0 = _withAlpha ? 0.195f : 0.180f;

            var prev = NewChild(p, "Preview");
            var prt = prev.GetComponent<RectTransform>();
            prt.anchorMin = new Vector2(0.06f, y0);
            prt.anchorMax = new Vector2(0.30f, y0 + 0.075f);
            prt.sizeDelta = Vector2.zero;
            _previewImage = prev.AddComponent<Image>();

            var holder = NewChild(p, "Hex");
            var hrt = holder.GetComponent<RectTransform>();
            hrt.anchorMin = new Vector2(0.34f, y0);
            hrt.anchorMax = new Vector2(0.94f, y0 + 0.075f);
            hrt.sizeDelta = Vector2.zero;
            holder.AddComponent<Image>().color = FieldBackground;

            _hexInput = holder.AddComponent<TMP_InputField>();
            _hexInput.characterLimit = 10;

            var ta = NewChild(holder.transform, "TextArea");
            Stretch(ta);
            ta.AddComponent<RectMask2D>();
            var tg = NewChild(ta.transform, "Text");
            Stretch(tg);
            var tmp = tg.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = 17; tmp.color = TextPrimary;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.margin = new Vector4(10, 0, 6, 0);
            tmp.richText = false;
            Font(tmp);
            _hexInput.textComponent = tmp;
            _hexInput.textViewport = ta.GetComponent<RectTransform>();

            TmpInputFieldFix.EnableCaretAndSelection(_hexInput, new Color(0.30f, 0.50f, 0.70f, 0.85f));
            BlinkingCaret.Attach(_hexInput);

            _hexInput.onEndEdit.AddListener(new UnityAction<string>(s =>
            {
                if (_suppressHexEcho) return;
                if (!StyledName.TryParseColor(s, out var c))
                {
                    Toast.Warning("Not a color: use #RGB, #RGBA, #RRGGBB or #RRGGBBAA");
                    RefreshAll();
                    return;
                }
                Color.RGBToHSV(new Color(c.r / 255f, c.g / 255f, c.b / 255f), out _hue, out _sat, out _val);
                if (_withAlpha) _alpha = c.a / 255f;
                RedrawSv();
                RefreshAll();
            }));
        }

        private static void BuildButtons(Transform p)
        {
            Label(p, "Click or drag on the square and the bars.", TextDisabled, 13,
                TextAlignmentOptions.Center, new Vector2(0.06f, 0.125f), new Vector2(0.94f, 0.165f));

            Button(p, "Cancel", new Vector2(0.06f, 0.035f), new Vector2(0.46f, 0.110f),
                new Color(0.25f, 0.25f, 0.32f, 1f), Close);

            Button(p, "Use this color", new Vector2(0.54f, 0.035f), new Vector2(0.94f, 0.110f),
                new Color(0.20f, 0.45f, 0.30f, 1f), () =>
                {
                    var chosen = Current();
                    var cb = _onPick;
                    Close();
                    cb?.Invoke(chosen);
                });
        }

        // -----------------------------------------------------------------
        // Mattoni
        // -----------------------------------------------------------------
        private static TextMeshProUGUI Label(Transform parent, string text, Color color, float size,
                                             TextAlignmentOptions align, Vector2 aMin, Vector2 aMax,
                                             FontStyles style = FontStyles.Normal)
        {
            var go = NewChild(parent, "Label");
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.sizeDelta = Vector2.zero;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text; tmp.fontSize = size; tmp.color = color;
            tmp.alignment = align; tmp.fontStyle = style;
            tmp.margin = new Vector4(4, 0, 4, 0);
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            Font(tmp);
            return tmp;
        }

        private static void Button(Transform parent, string text, Vector2 aMin, Vector2 aMax,
                                   Color background, Action onClick)
        {
            var go = NewChild(parent, "Btn");
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.sizeDelta = Vector2.zero;
            go.AddComponent<Image>().color = background;
            var b = go.AddComponent<Button>();
            b.transition = Selectable.Transition.None;
            b.onClick.AddListener(new UnityAction(() => onClick()));
            Label(go.transform, text, Color.white, 16, TextAlignmentOptions.Center, Vector2.zero, Vector2.one);
        }

        private static void Font(TMP_Text tmp)
        {
            var f = TmpFontHelper.Get();
            if (f != null) tmp.font = f;
        }

        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void Stretch(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero; rt.anchoredPosition = Vector2.zero;
        }
    }
}
