using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Il font che Arena sta usando **adesso**, per far sembrare nostro quello
    /// che disegniamo sopra il gioco.
    ///
    /// PERCHE' NON CHIEDIAMO UN FONT PER NOME: nei binari non c'e' nessun nome
    /// di font asset da cui partire (cercato: niente). I font sono asset Unity
    /// caricati a runtime, quindi l'unico modo solido e' guardare cosa e' gia'
    /// a schermo.
    ///
    /// DIFFERENZA CON <see cref="TmpFontHelper"/>: quello prende il primo font
    /// che trova e se lo tiene per sempre — va benissimo per una finestra di
    /// menu, ma se la prima chiamata capita nella schermata dei mazzi ci
    /// portiamo quel font dentro al duello. Qui invece si sceglie il font
    /// **piu' usato fra i testi attivi in questo momento**, che e' quello del
    /// corpo dello schermo corrente, e si ricontrolla quando la scena cambia.
    ///
    /// Si prende anche il materiale: e' li' che stanno contorno e ombra con cui
    /// Arena rende leggibile il testo sopra il tavolo da gioco.
    /// </summary>
    internal static class ArenaFont
    {
        private static TMP_FontAsset _font;
        private static Material _material;
        private static float _nextScan;

        private static readonly Dictionary<TMP_FontAsset, int> _tally =
            new Dictionary<TMP_FontAsset, int>();

        /// <summary>Ogni quanto ricontrollare: un duello non cambia font a meta'.</summary>
        private const float RescanInterval = 5f;

        public static TMP_FontAsset Font { get { Scan(); return _font; } }

        /// <summary>
        /// Materiale del font campionato. Da assegnare a
        /// <c>fontSharedMaterial</c> e mai modificato: e' condiviso con i testi
        /// del gioco, e cambiargli una proprieta' li cambierebbe tutti.
        /// </summary>
        public static Material Material { get { Scan(); return _material; } }

        /// <summary>Applica font e materiale a un'etichetta appena creata.</summary>
        public static void Apply(TMP_Text label)
        {
            if (label == null) return;
            var font = Font;
            if (font == null) return;

            label.font = font;
            if (_material != null) label.fontSharedMaterial = _material;
        }

        private static void Scan()
        {
            if (_font != null && Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + RescanInterval;

            var texts = UnityEngine.Object.FindObjectsOfType<TMP_Text>();
            if (texts == null || texts.Length == 0)
            {
                // Fuori da una scena con testo: teniamo quello che avevamo, e
                // in mancanza ripieghiamo sull'aiutante gia' esistente.
                if (_font == null) _font = TmpFontHelper.Get();
                return;
            }

            // Il font piu' frequente fra i testi attivi: e' il corpo dello
            // schermo corrente, non un titolo o un font speciale di una
            // singola etichetta. Un conteggio solo, non un confronto di tutti
            // con tutti: in un duello i TMP_Text sono centinaia.
            _tally.Clear();
            TMP_FontAsset best = null;
            Material bestMaterial = null;
            int bestCount = 0;

            for (int i = 0; i < texts.Length; i++)
            {
                var candidate = texts[i];
                if (candidate == null || candidate.font == null) continue;
                if (!candidate.isActiveAndEnabled) continue;

                int count;
                _tally.TryGetValue(candidate.font, out count);
                count++;
                _tally[candidate.font] = count;

                if (count > bestCount)
                {
                    bestCount = count;
                    best = candidate.font;
                    bestMaterial = candidate.fontSharedMaterial;
                }
            }

            if (best != null)
            {
                _font = best;
                _material = bestMaterial;
            }
            else if (_font == null)
            {
                _font = TmpFontHelper.Get();
            }
        }
    }
}
