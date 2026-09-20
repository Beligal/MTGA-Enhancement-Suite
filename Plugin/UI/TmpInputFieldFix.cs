using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Fa nascere l'oggetto che TMP usa per disegnare cursore e **selezione**
    /// dentro un <see cref="TMP_InputField"/> costruito a runtime.
    ///
    /// Il difetto, letto in <c>TMP_InputField.OnEnable</c>:
    ///
    /// <code>
    /// if (Application.isPlaying &amp;&amp; m_CachedInputRenderer == null &amp;&amp; m_TextComponent != null)
    /// {
    ///     GameObject gameObject = new GameObject("Caret", typeof(TMP_SelectionCaret));
    ///     ...
    ///     m_CachedInputRenderer = gameObject.GetComponent&lt;CanvasRenderer&gt;();
    /// }
    /// </code>
    ///
    /// Quell'oggetto si crea **una volta sola, in OnEnable, e solo se
    /// <c>m_TextComponent</c> e' gia' assegnato**. Facendo
    /// <c>AddComponent&lt;TMP_InputField&gt;()</c> l'OnEnable scatta subito,
    /// quando il textComponent non c'e' ancora perche' glielo diamo due righe
    /// dopo: la condizione e' falsa, l'oggetto non nasce, e
    /// <c>m_CachedInputRenderer</c> resta null per sempre. Da li' in poi
    /// <c>UpdateGeometry</c> esce subito e **non viene disegnato ne' il cursore
    /// ne' l'evidenziazione della selezione**.
    ///
    /// E' la stessa causa per cui esiste <see cref="BlinkingCaret"/>, che pero'
    /// rimedia solo al cursore. La selezione invisibile rendeva impossibile
    /// capire cosa si stava per colorare nell'editor del nome (segnalato in
    /// gioco il 2026-09-20).
    ///
    /// Il rimedio e' far rigirare OnEnable adesso che il textComponent c'e':
    /// spegnere e riaccendere il componente. Non ricrea niente due volte —
    /// la guardia <c>m_CachedInputRenderer == null</c> lo impedisce — e
    /// ripristina la strada nativa, che gestisce da sola cose che rifare a mano
    /// sarebbe facile sbagliare: lo scorrimento orizzontale quando il testo e'
    /// piu' lungo del campo, e il ritaglio dentro il viewport.
    ///
    /// <see cref="BlinkingCaret"/> resta utile e non da' fastidio: mette
    /// <c>caretWidth = 0</c>, quindi il cursore nativo esce comunque come un
    /// quadrilatero di larghezza zero e non si vede doppio.
    /// </summary>
    internal static class TmpInputFieldFix
    {
        private static FieldInfo _cachedRendererField;
        private static bool _loggedOutcome;

        /// <summary>
        /// Da chiamare **dopo** aver assegnato <c>textComponent</c> e
        /// <c>textViewport</c>. <paramref name="selectionColor"/> tinge
        /// l'evidenziazione: il default di TMP e' un azzurro chiaro che sul
        /// fondo scuro dei nostri pannelli si legge male.
        /// </summary>
        public static void EnableCaretAndSelection(TMP_InputField input, Color selectionColor)
        {
            if (input == null) return;

            input.selectionColor = selectionColor;
            input.customCaretColor = true;
            input.caretColor = new Color(0.85f, 0.85f, 0.90f, 1f);

            // Spegni/riaccendi: OnEnable rigira e stavolta trova il
            // textComponent. Se l'oggetto "Caret" esisteva gia', la guardia
            // interna lo lascia dov'e'.
            if (input.gameObject.activeInHierarchy)
            {
                input.enabled = false;
                input.enabled = true;
            }

            LogOutcomeOnce(input);
        }

        /// <summary>
        /// Una riga di log, la prima volta, che dice se l'oggetto e' nato
        /// davvero. Sta FUORI dai rami e riporta cosa ha trovato, non solo il
        /// caso buono: se domani TMP cambia e il rimedio smette di funzionare,
        /// deve dirlo il log e non l'utente che non vede la selezione.
        /// </summary>
        private static void LogOutcomeOnce(TMP_InputField input)
        {
            if (_loggedOutcome) return;
            _loggedOutcome = true;

            if (_cachedRendererField == null)
                _cachedRendererField = AccessTools.Field(typeof(TMP_InputField), "m_CachedInputRenderer");

            if (_cachedRendererField == null)
            {
                Plugin.Log.LogWarning(
                    "TmpInputFieldFix: campo m_CachedInputRenderer non trovato, " +
                    "non posso dire se cursore e selezione siano attivi.");
                return;
            }

            object renderer = null;
            try { renderer = _cachedRendererField.GetValue(input); } catch { }

            if (renderer == null)
                Plugin.Log.LogWarning(
                    "TmpInputFieldFix: dopo il riavvio del componente m_CachedInputRenderer e' ANCORA null: " +
                    "la selezione non verra' evidenziata.");
            else
                Plugin.Log.LogInfo("TmpInputFieldFix: cursore e selezione attivi (m_CachedInputRenderer creato).");
        }
    }
}
