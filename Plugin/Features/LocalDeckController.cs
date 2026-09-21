using System;
using HarmonyLib;
using MTGAEnhancementSuite.Patches;
using MTGAEnhancementSuite.UI;
using Wizards.Mtga.Decks;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Coordinates user interaction with local decks: which local deck is
    /// currently selected (drives the Make Cloud / Make Local button visibility),
    /// double-click-to-edit, and tile rename. The actual operations live in
    /// <see cref="LocalDeckConverter"/> (make local / make cloud) and
    /// <see cref="LocalDeckBridge"/> (text ↔ Client_Deck).
    ///
    /// Selection model: the Local Decks folder wires its tile-click delegate to
    /// MTGA's own <c>DeckViewSelector.SelectDeck</c> (same as native folders), so
    /// the deck-box-open animation and cross-folder deselect work for free. MTGA
    /// then sets <c>DeckManagerController._selectedDeck = null</c> (the synthetic
    /// id isn't in its server-backed list), which is what keeps the native
    /// Edit/Delete/Clone buttons inert for local decks. We detect "a local deck
    /// was selected" in the <c>UpdateSelectedDeckView</c> postfix by testing the
    /// clicked id against <see cref="LocalDeckStore.IsLocal"/>.
    /// </summary>
    internal static class LocalDeckController
    {
        /// <summary>The local deck currently focused, or null if a cloud deck (or nothing) is focused.</summary>
        public static Guid? SelectedLocalDeckId { get; private set; }

        public static bool HasLocalSelection => SelectedLocalDeckId.HasValue;

        /// <summary>
        /// Called from the UpdateSelectedDeckView postfix for every selection
        /// change. Sets or clears the local-selection state based on whether the
        /// focused deck is one of ours.
        /// </summary>
        public static void OnSelectionChanged(DeckViewInfo info)
        {
            if (info != null && LocalDeckStore.IsLocal(info.deckId))
                SelectedLocalDeckId = info.deckId;
            else
                SelectedLocalDeckId = null;
        }

        public static void ClearSelection() => SelectedLocalDeckId = null;

        // -----------------------------------------------------------------
        // Tile double-click → edit in place (Phase 5 wires the save-intercept)
        // -----------------------------------------------------------------
        public static void OnDoubleClick(DeckViewInfo info)
        {
            if (info == null) return;
            if (!LocalDeckStore.IsLocal(info.deckId)) return;
            OpenForEdit(info.deckId);
        }

        /// <summary>
        /// Opens MTGA's deck builder on a synthetic deck reconstructed from the
        /// local file. The builder save-intercept (DeckBuilderSavePatch) detects
        /// the local id and writes the result back to disk instead of the cloud.
        /// Implemented in Phase 5.
        /// </summary>
        public static void OpenForEdit(Guid localId)
        {
            LocalDeckEditor.OpenForEdit(localId);
        }

        // -----------------------------------------------------------------
        // Tile rename (the inline name field on a deck tile)
        // -----------------------------------------------------------------
        public static void OnNameEdit(DeckViewInfo info, string newName)
        {
            if (info == null || string.IsNullOrWhiteSpace(newName)) return;
            if (!LocalDeckStore.IsLocal(info.deckId)) return;
            try
            {
                var text = LocalDeckStore.ReadText(info.deckId);
                if (text == null) return;
                LocalDeck renamed;
                string error;
                if (!LocalDeckStore.TryUpdate(info.deckId, newName.Trim(), text, null, out renamed, out error))
                {
                    // Nothing was written and nothing was deleted: the deck is
                    // still there under its old name. Don't claim otherwise —
                    // this is the pure-rename path, the one where the old file
                    // used to be deleted before the new one existed.
                    Toast.Error($"Couldn't rename local deck: {error ?? "write failed"}");
                    return;
                }
                // Report the name that actually landed on disk: sanitizing and
                // the duplicate guard can both change what the user typed.
                Toast.Info($"Renamed local deck to '{renamed.Name}'");
                DeckViewSelectorPatch.RebuildLocalDecks();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LocalDeckController.OnNameEdit: {ex.Message}");
            }
        }
    }
}
