using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using Wizards.Mtga.Decks;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Tasto destro su una tessera della lista mazzi: apre il menu del mazzo
    /// (statistiche, stile del nome).
    ///
    /// L'aggancio e' <c>CustomTouchButton.OnRightClick</c>, un UnityEvent
    /// pubblico che Arena invoca al rilascio del tasto destro
    /// (<c>OnPointerUp</c>, ramo <c>InputButton.Right</c>). Non serve nessun
    /// patch: basta iscriversi.
    ///
    /// **Perche' non un IPointerClickHandler nostro sulla tessera:** il click
    /// vero lo riceve il <c>CustomTouchButton</c>, che sta su un altro oggetto
    /// della gerarchia. L'EventSystem di Unity risale dall'oggetto colpito fino
    /// al PRIMO che gestisce l'evento e li' si ferma, quindi un gestore messo
    /// sulla radice della tessera non verrebbe mai chiamato.
    ///
    /// Le tessere vengono riciclate: <c>Bind</c> viene richiamato a ogni
    /// <c>SetDeckModel</c> e aggiorna l'id, ma l'iscrizione si fa **una volta
    /// sola** — l'ascoltatore legge il campo, quindi resta sempre aggiornato
    /// senza doversi riscrivere. Iscriversi di nuovo a ogni giro vorrebbe dire
    /// aprire N finestre a un solo click.
    /// </summary>
    internal sealed class DeckNameRightClick : MonoBehaviour
    {
        private static FieldInfo _customButtonField;
        private static bool _loggedMissingButton;

        private Guid _deckId;
        private bool _subscribed;

        public void Bind(DeckView view)
        {
            if (view == null) return;

            try { _deckId = view.GetDeckId(); }
            catch { _deckId = Guid.Empty; }

            if (_subscribed) return;

            if (_customButtonField == null)
                _customButtonField = AccessTools.Field(typeof(DeckView), "_customButton");

            var button = _customButtonField?.GetValue(view) as CustomTouchButton;
            if (button == null)
            {
                if (!_loggedMissingButton)
                {
                    _loggedMissingButton = true;
                    Plugin.Log.LogWarning(
                        "DeckNameRightClick: DeckView._customButton non trovato, " +
                        "il tasto destro sulle tessere non funzionera'.");
                }
                return;
            }

            button.OnRightClick.AddListener(new UnityAction(HandleRightClick));
            _subscribed = true;
        }

        /// <summary>
        /// Il nome corrente del mazzo, per l'intestazione del menu e della
        /// finestra delle statistiche. Null se non si riesce a leggerlo: la
        /// finestra sa gia' cavarsela senza.
        /// </summary>
        private static string DeckNameOf(Guid deckId)
        {
            try
            {
                var dm = Helpers.GameReflection.PantryGet<DecksManager>();
                return dm?.GetDeck(deckId)?.Summary?.Name;
            }
            catch { return null; }
        }

        private void HandleRightClick()
        {
            if (_deckId == Guid.Empty) return;      // tessera "nuovo mazzo" e simili
            try
            {
                // Dal 2026-09-20 apre un menu invece dell'editor: le voci sono
                // due (statistiche e nome) e un gesto solo non basta piu'.
                DeckContextMenu.Show(_deckId, DeckNameOf(_deckId));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DeckNameRightClick: {ex}");
            }
        }
    }
}
