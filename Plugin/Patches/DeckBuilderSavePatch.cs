using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.Features;
using MTGAEnhancementSuite.UI;
using Wizards.Mtga.FrontDoorModels;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Redirects the deck builder's save to disk when the deck being edited is
    /// one of our local decks. <c>WrapperDeckBuilder.Coroutine_SaveDeck</c> is
    /// the single choke point every save route funnels through (Done button,
    /// nav-bar exit, handheld back, ForceSaveDeck), so one prefix covers them
    /// all. The MTGA validation chain (SaveStep_*) still runs BEFORE this, so
    /// the user gets the normal "deck invalid / needs sideboard" UX — we only
    /// change where the result is persisted.
    ///
    /// Detection: the edited deck's id matches a GUID in
    /// <see cref="LocalDeckStore"/>. We open the builder with that id
    /// (see <see cref="LocalDeckEditor"/>), and DeckInfo carries it through
    /// editing unchanged.
    /// </summary>
    [HarmonyPatch(typeof(WrapperDeckBuilder), GameRefs.WrapperDeckBuilder_SaveCoroutine)]
    internal static class DeckBuilderSavePatch
    {
        private static FieldInfo _isSavingDeckField;
        private static FieldInfo _isDeckSaveSuccessField;

        [HarmonyPrefix]
        private static bool Prefix(DeckInfo deckToSave, ref IEnumerator __result, WrapperDeckBuilder __instance)
        {
            try
            {
                // Identify the local deck by id. Prefer the edited deck's id,
                // but also consult the builder context's deck id — the model's
                // GetServerModel() id has burned others before, whereas the
                // context still holds the DeckInfo we opened with.
                var id = ResolveLocalId(deckToSave, __instance);
                if (id == Guid.Empty)
                    return true; // not ours — let MTGA save to the cloud normally

                // Serialize the edited deck and write it back to the local file.
                var text = LocalDeckBridge.ExportTextFromDeckInfo(deckToSave);
                if (string.IsNullOrWhiteSpace(text))
                {
                    Toast.Error("Couldn't save local deck (export failed)");
                    // Fall through to a no-op coroutine so the builder doesn't
                    // crash, but DON'T let it hit the cloud.
                    __result = Empty();
                    MarkSaved(__instance, success: false);
                    return false;
                }

                var newName = string.IsNullOrWhiteSpace(deckToSave.name) ? null : deckToSave.name;

                // Build updated meta: format + deck-box art from the edited deck,
                // preserving folder membership (and any field the builder left
                // at 0) from what we had stored.
                var existing = LocalDeckStore.Get(id)?.Meta;
                var meta = new LocalDeckMeta
                {
                    Format = string.IsNullOrWhiteSpace(deckToSave.format) ? existing?.Format : deckToSave.format,
                    DeckTileId = deckToSave.deckTileId != 0 ? deckToSave.deckTileId : (existing?.DeckTileId ?? 0),
                    DeckArtId  = deckToSave.deckArtId  != 0 ? deckToSave.deckArtId  : (existing?.DeckArtId ?? 0),
                    FolderId   = existing?.FolderId,
                    FolderName = existing?.FolderName,
                };
                LocalDeck savedDeck;
                string saveError;
                if (!LocalDeckStore.TryUpdate(id, newName, text, meta, out savedDeck, out saveError))
                {
                    // The write failed: the file on disk is untouched and the
                    // edits are still in the builder. Say so and keep the user
                    // here, exactly like the export-failure case above.
                    // Reporting success is what used to lose work — the old
                    // Update() swallowed the error and handed back the OLD deck,
                    // so this caller had no way to tell, marked the save good,
                    // dropped the cached deck and let the builder navigate away.
                    Toast.Error($"Couldn't save local deck: {saveError ?? "write failed"}");
                    PerPlayerLog.Info($"DeckBuilderSavePatch: local deck save FAILED ({id}): {saveError}");
                    __result = Empty();
                    MarkSaved(__instance, success: false);
                    return false;
                }

                // The deck is on disk. Tell the builder the save succeeded so it
                // navigates away cleanly (Coroutine_SaveDeckAndExit checks
                // _isDeckSaveSuccess), and clear the in-flight flag.
                MarkSaved(__instance, success: true);
                try { WrapperDeckBuilder.ClearCachedDeck(); } catch { }

                // Refresh the Local Decks folder with the edited contents.
                DeckViewSelectorPatch.RebuildLocalDecks();

                Toast.Success("Saved to Local Decks");
                PerPlayerLog.Info($"DeckBuilderSavePatch: local deck saved to disk ({id} '{savedDeck.Name}')");

                __result = Empty();
                return false; // skip the cloud CreateDeck/UpdateDeck path
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DeckBuilderSavePatch.Prefix failed: {ex}");
                return true; // safest fallback: let MTGA handle it
            }
        }

        // Returns the local-deck GUID being saved, or Guid.Empty if this is a
        // normal cloud-deck save. Checks the edited deck's id first, then the
        // builder context's deck id.
        private static Guid ResolveLocalId(DeckInfo deckToSave, WrapperDeckBuilder instance)
        {
            var id = deckToSave?.id ?? Guid.Empty;
            if (id != Guid.Empty && LocalDeckStore.IsLocal(id)) return id;

            try
            {
                var provider = Helpers.GameReflection.PantryGet<Core.Code.Decks.DeckBuilderContextProvider>();
                var ctxDeck = provider?.Context?.Deck;
                var ctxId = ctxDeck?.id ?? Guid.Empty;
                if (ctxId != Guid.Empty && LocalDeckStore.IsLocal(ctxId)) return ctxId;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckBuilderSavePatch.ResolveLocalId: {ex.Message}");
            }
            return Guid.Empty;
        }

        private static void MarkSaved(WrapperDeckBuilder instance, bool success)
        {
            try
            {
                if (_isSavingDeckField == null)
                {
                    _isSavingDeckField = AccessTools.Field(typeof(WrapperDeckBuilder), GameRefs.WrapperDeckBuilder_IsSavingField);
                    _isDeckSaveSuccessField = AccessTools.Field(typeof(WrapperDeckBuilder), GameRefs.WrapperDeckBuilder_SaveSuccessField);
                }
                _isDeckSaveSuccessField?.SetValue(instance, success);
                _isSavingDeckField?.SetValue(instance, false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckBuilderSavePatch.MarkSaved: {ex.Message}");
            }
        }

        // A valid, already-complete IEnumerator. Returned as __result so callers
        // that do `yield return Coroutine_SaveDeck(...)` or
        // `StartCoroutine(Coroutine_SaveDeck(...))` get something runnable
        // instead of null (StartCoroutine(null) throws).
        private static IEnumerator Empty() { yield break; }
    }
}
