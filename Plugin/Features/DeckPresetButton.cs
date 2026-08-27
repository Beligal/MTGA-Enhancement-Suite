using System;
using System.Text;
using MTGAEnhancementSuite.UI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Inietta un pulsante a destra della barra filtri del deck editor.
    ///
    /// Aggancio: cloniamo <c>SearchAndFilterBar.AdvancedFiltersButton</c> e lo
    /// riparentiamo allo stesso contenitore, cosi eredita stile, dimensione e
    /// materiale invece di doverli riprodurre.
    ///
    /// Posizionamento: se il contenitore ha un LayoutGroup lasciamo che sia lui
    /// a disporre il pulsante (basta metterlo per ultimo); altrimenti lo
    /// spostiamo a mano a destra dell originale. Non sappiamo staticamente
    /// quale dei due casi sia, quindi gestiamo entrambi e logghiamo cosa
    /// abbiamo trovato per poter tarare.
    ///
    /// Questa versione verifica solo il posizionamento: il click mostra un
    /// Toast. La logica dei preset arriva dopo, una volta confermata la resa.
    /// </summary>
    internal static class DeckPresetButton
    {
        private const string ButtonName = "MTGAES_PresetButton";

        /// <summary>Scena in cui vive la barra filtri: fuori di qui non cerchiamo nulla.</summary>
        private const string DeckBuilderScene = "DeckBuilder";

        /// <summary>Spazio fra il pulsante originale e il nostro, se posizioniamo a mano.</summary>
        private const float GapPixels = 8f;

        /// <summary>Nome del PNG in icons/ da usare come icona.</summary>
        private const string IconName = "107582";

        /// <summary>Oro. L Image moltiplica il colore per lo sprite, quindi
        /// l icona va fornita bianca su trasparente perche' questo funzioni.</summary>
        private static readonly Color Gold = new Color(0.831f, 0.686f, 0.216f, 1f);

        private const float ScanInterval = 1f;

        private static float _nextScan;
        private static GameObject _button;
        private static SearchAndFilterBar _bar;

        public static void Tick()
        {
            try
            {
                if (Time.unscaledTime < _nextScan) return;
                _nextScan = Time.unscaledTime + ScanInterval;

                // La barra filtri esiste solo nel deck builder. Il controllo sulla
                // scena e' una lookup per nome; FindObjectOfType qui sotto e' una
                // passata su tutta la scena, che senza questa guardia pagavamo una
                // volta al secondo anche in duello e nei menu.
                if (!SceneManager.GetSceneByName(DeckBuilderScene).isLoaded)
                {
                    _bar = null;
                    _button = null;
                    return;
                }

                var bar = UnityEngine.Object.FindObjectOfType<SearchAndFilterBar>();
                if (bar == null)
                {
                    // Fuori dal deck editor: il pulsante muore con la barra.
                    _bar = null;
                    _button = null;
                    return;
                }

                // Gia' iniettato in questa istanza della barra?
                if (_bar == bar && _button != null) return;

                _bar = bar;
                Inject(bar);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DeckPresetButton.Tick: {ex}");
                _nextScan = Time.unscaledTime + 15f;
            }
        }

        private static void Inject(SearchAndFilterBar bar)
        {
            var source = bar.AdvancedFiltersButton;
            if (source == null)
            {
                Plugin.Log.LogWarning("DeckPresetButton: AdvancedFiltersButton nullo, barra non pronta.");
                _bar = null;
                return;
            }

            var parent = source.transform.parent;
            if (parent == null)
            {
                Plugin.Log.LogWarning("DeckPresetButton: il pulsante filtri non ha parent.");
                _bar = null;
                return;
            }

            // Guardia anti doppia iniezione (la barra puo' essere ricreata).
            var existing = parent.Find(ButtonName);
            if (existing != null)
            {
                _button = existing.gameObject;
                return;
            }

            var clone = UnityEngine.Object.Instantiate(source.gameObject, parent);
            clone.name = ButtonName;
            clone.SetActive(true);

            var btn = clone.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(OnClicked);
                btn.interactable = true;
            }

            // Il clone porta con se' l'icona dei filtri avanzati: la
            // sostituiamo con la nostra e la tingiamo d'oro. Un pulsante che
            // resta identico all'originale confonde e basta.
            ApplySkin(clone);

            // Se il clone ha un'etichetta di testo la togliamo: l'icona basta.
            var label = clone.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = string.Empty;

            PositionIt(source.gameObject, clone, parent);
            _button = clone;

            LogLayout(bar, source.gameObject, clone, parent, label != null);
        }

        /// <summary>
        /// Sostituisce lo sprite con la nostra icona e applica la tinta oro.
        /// Un clone puo' avere piu' Image (sfondo + glifo): tocchiamo quella
        /// che uno sprite ce l ha gia', cioe' il glifo.
        /// </summary>
        private static void ApplySkin(GameObject clone)
        {
            var sprite = IconLoader.Get(IconName);
            if (sprite == null)
            {
                Plugin.Log.LogWarning($"DeckPresetButton: icona '{IconName}.png' non trovata in icons/, tengo quella originale.");
            }

            var images = clone.GetComponentsInChildren<Image>(true);
            bool applied = false;
            foreach (var img in images)
            {
                if (img == null) continue;
                if (img.sprite == null) continue;   // sfondo/riempimento: non tocchiamo
                if (sprite != null)
                {
                    img.sprite = sprite;
                    img.preserveAspect = true;
                }
                img.color = Gold;
                applied = true;
            }

            // Nessuna Image con sprite: tingiamo comunque la prima disponibile,
            // cosi il pulsante si distingue anche senza icona.
            if (!applied && images.Length > 0 && images[0] != null)
            {
                if (sprite != null) { images[0].sprite = sprite; images[0].preserveAspect = true; }
                images[0].color = Gold;
            }

            Plugin.Log.LogInfo($"DeckPresetButton: skin applicata (Image trovate={images.Length}, sprite={(sprite != null ? IconName : "originale")}).");
        }

        /// <summary>
        /// Con un LayoutGroup sul contenitore la posizione la decide Unity: ci
        /// limitiamo a mettere il pulsante in fondo. Senza, lo spostiamo a
        /// destra dell originale della sua stessa larghezza piu' un margine.
        /// </summary>
        private static void PositionIt(GameObject source, GameObject clone, Transform parent)
        {
            var layout = parent.GetComponent<LayoutGroup>();
            var srcRect = source.GetComponent<RectTransform>();
            var rect = clone.GetComponent<RectTransform>();
            if (rect == null || srcRect == null) return;

            if (layout != null)
            {
                clone.transform.SetAsLastSibling();
                return;
            }

            rect.anchorMin = srcRect.anchorMin;
            rect.anchorMax = srcRect.anchorMax;
            rect.pivot = srcRect.pivot;
            rect.sizeDelta = srcRect.sizeDelta;
            rect.localScale = srcRect.localScale;
            rect.localRotation = srcRect.localRotation;

            float width = srcRect.rect.width;
            if (width <= 1f) width = 40f;
            rect.anchoredPosition = srcRect.anchoredPosition + new Vector2(width + GapPixels, 0f);
        }

        private static void OnClicked()
        {
            // Il pulsante vive sul canvas di Arena, che e' scalato: le sue
            // coordinate "mondo" NON sono pixel di schermo. Vanno convertite,
            // altrimenti il menu finisce vicino all origine (angolo in basso a
            // sinistra). Per un canvas overlay la camera e' null.
            // Input System: Mouse.current e' null senza mouse collegato.
            var mouse = Mouse.current;
            Vector2 at = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
            if (_button != null)
            {
                var rt = _button.GetComponent<RectTransform>();
                var canvas = _button.GetComponentInParent<Canvas>();
                if (rt != null)
                {
                    Camera cam = null;
                    if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                        cam = canvas.worldCamera;

                    var corners = new Vector3[4];
                    rt.GetWorldCorners(corners);
                    // corners[0] = basso-sinistra: il menu si apre da li' verso il basso.
                    at = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                }
            }
            Plugin.Log.LogInfo($"DeckPresetButton: click, menu a schermo {at} (screen {Screen.width}x{Screen.height}).");
            PresetMenu.Show(at);
        }

        /// <summary>
        /// Dump della disposizione trovata: serve a tarare la posizione senza
        /// dover indovinare al buio.
        /// </summary>
        private static void LogLayout(SearchAndFilterBar bar, GameObject source, GameObject clone,
                                      Transform parent, bool hadLabel)
        {
            var sb = new StringBuilder();
            var layout = parent.GetComponent<LayoutGroup>();
            var srcRect = source.GetComponent<RectTransform>();
            var rect = clone.GetComponent<RectTransform>();

            sb.AppendLine("[PresetBtn] ===== iniezione =====");
            sb.AppendLine($"[PresetBtn] barra='{bar.name}' parent='{parent.name}' figli={parent.childCount}");
            sb.AppendLine($"[PresetBtn] LayoutGroup sul parent: {(layout == null ? "NO (posizione manuale)" : layout.GetType().Name)}");
            sb.AppendLine($"[PresetBtn] sorgente pos={srcRect.anchoredPosition} size={srcRect.rect.size}");
            sb.AppendLine($"[PresetBtn] clone    pos={rect.anchoredPosition} size={rect.rect.size} etichetta={hadLabel}");

            sb.AppendLine("[PresetBtn] fratelli nel contenitore (nome | attivo | x | larghezza):");
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                var cr = child as RectTransform;
                string x = cr != null ? cr.anchoredPosition.x.ToString("0.#") : "?";
                string w = cr != null ? cr.rect.width.ToString("0.#") : "?";
                sb.AppendLine($"[PresetBtn]    {i,2}. {child.name} | {child.gameObject.activeSelf} | {x} | {w}");
            }
            Plugin.Log.LogInfo(sb.ToString().TrimEnd());
        }
    }
}
