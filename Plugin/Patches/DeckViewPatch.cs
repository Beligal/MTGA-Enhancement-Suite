using System;
using HarmonyLib;
using MTGAEnhancementSuite.Diagnostics;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using UnityEngine;
using Wizards.Mtga.Decks;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Phase 3 of deck organization: hooks individual deck tiles
    /// (<see cref="DeckView"/>) so that
    ///   1. when multi-select mode is active, clicking a tile toggles its
    ///      selection instead of opening the deck.
    ///   2. each tile shows a checkmark overlay when selected.
    ///
    /// Overlay management lives on <see cref="DeckTileSelectionOverlay"/>, a
    /// MonoBehaviour we ensure exists on every DeckView after MTGA binds it
    /// to a deck (in SetDeckModel postfix). The overlay subscribes to
    /// <see cref="DeckMultiSelectState.OnChanged"/> for cheap reactive updates.
    /// </summary>
    [HarmonyPatch]
    internal static class DeckViewPatch
    {
        /// <summary>
        /// Click intercept. When multi-select is active, swallow the click and
        /// toggle this deck's selection instead of letting MTGA open it.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckView), "OnDeckClick")]
        private static bool OnDeckClick_Prefix(DeckView __instance)
        {
            if (!DeckMultiSelectState.IsActive) return true; // run MTGA's handler
            try
            {
                var deckId = __instance.GetDeckId();
                if (deckId == Guid.Empty) return true; // create-new-deck tile etc.
                DeckMultiSelectState.ToggleDeck(deckId);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckViewPatch.OnDeckClick_Prefix: {ex.Message}");
            }
            return false; // skip the original
        }

        /// <summary>
        /// Tile is being (re-)bound to a deck. Make sure the selection overlay
        /// component exists on it. The overlay itself decides what to render
        /// based on multi-select state.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DeckView), "SetDeckModel")]
        private static void SetDeckModel_Postfix(DeckView __instance)
        {
            // Scatta una volta per tessera: con 192 mazzi in lista sono 192
            // chiamate di fila, ed e' il tipo di costo che si vede solo sommato.
            using (FrameCost.Measure("DeckView.SetDeckModel"))
                SetDeckModel_PostfixCore(__instance);
        }

        private static void SetDeckModel_PostfixCore(DeckView __instance)
        {
            try
            {
                var overlay = __instance.GetComponent<DeckTileSelectionOverlay>();
                if (overlay == null) overlay = __instance.gameObject.AddComponent<DeckTileSelectionOverlay>();
                overlay.Refresh();

                // Tasto destro sulla tessera -> editor del nome. Bind aggiorna
                // l'id del mazzo a ogni riciclo della tessera, ma si iscrive
                // all'evento una volta sola: vedi DeckNameRightClick.
                var rename = __instance.GetComponent<DeckNameRightClick>();
                if (rename == null) rename = __instance.gameObject.AddComponent<DeckNameRightClick>();
                rename.Bind(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckViewPatch.SetDeckModel_Postfix: {ex.Message}");
            }
        }
    }
}
