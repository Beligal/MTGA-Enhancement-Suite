using UnityEngine;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Uno sprite con gli angoli arrotondati, generato una volta e riusato.
    ///
    /// PERCHE' SERVE: Unity da' a un <c>Image</c> senza sprite un rettangolo
    /// netto, e un rettangolo netto sopra il tavolo da gioco si vede che non e'
    /// di Arena — la sua interfaccia in duello e' fatta di lastre scure con gli
    /// angoli morbidi. Questo e' il singolo dettaglio che piu' fa sembrare
    /// "appiccicato" un pannello fatto da noi.
    ///
    /// Lo sprite e' bianco: il colore lo mette chi lo usa, tramite
    /// <c>Image.color</c>. Va montato con <c>Image.type = Sliced</c>, cosi' i
    /// quattro angoli restano della loro dimensione e solo i bordi si allungano
    /// — un pannello alto il doppio non ha angoli ovali.
    /// </summary>
    internal static class RoundedRect
    {
        private static Sprite _cached;
        private static int _cachedRadius;

        /// <summary>
        /// Lato della texture. Deve stare largo almeno il doppio del raggio,
        /// altrimenti i quattro angoli del 9-slice si sovrappongono.
        /// </summary>
        private const int Size = 48;

        public static Sprite Get(int radius = 10)
        {
            if (_cached != null && _cachedRadius == radius) return _cached;

            radius = Mathf.Clamp(radius, 1, Size / 2 - 1);

            var texture = new Texture2D(Size, Size, TextureFormat.ARGB32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    pixels[y * Size + x] = new Color(1f, 1f, 1f, Coverage(x, y, radius));
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();

            var border = new Vector4(radius, radius, radius, radius);
            _cached = Sprite.Create(texture, new Rect(0, 0, Size, Size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            _cached.hideFlags = HideFlags.HideAndDontSave;
            _cachedRadius = radius;
            return _cached;
        }

        /// <summary>
        /// Quanto quel pixel e' "dentro" la forma, da 0 a 1. Fuori dai quattro
        /// angoli e' sempre 1; dentro un angolo si guarda la distanza dal
        /// centro del cerchio e si sfuma su un pixel, cosi' il bordo non viene
        /// a scaletta.
        /// </summary>
        private static float Coverage(int x, int y, int radius)
        {
            // Centro del cerchio dell'angolo piu' vicino. Il +0.5 e' il centro
            // del pixel: senza, la curva risulta storta di mezzo pixel.
            float px = x + 0.5f;
            float py = y + 0.5f;

            float cx = px < radius ? radius : (px > Size - radius ? Size - radius : px);
            float cy = py < radius ? radius : (py > Size - radius ? Size - radius : py);

            float dx = px - cx;
            float dy = py - cy;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);

            return Mathf.Clamp01(radius - distance + 0.5f);
        }
    }
}
