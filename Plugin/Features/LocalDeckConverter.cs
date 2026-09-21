using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.Patches;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using UnityEngine;
using Wizards.Mtga.Decks;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// The two conversion operations between cloud and local decks:
    ///   - <see cref="MakeLocal"/>: export a cloud deck to text, save it as a
    ///     local file, then delete the cloud deck (frees a slot).
    ///   - <see cref="MakeCloud"/>: import a local deck's text as a new cloud
    ///     deck (requires a free slot), then delete the local file.
    ///
    /// All the DecksManager CRUD calls return <c>Promise&lt;T&gt;</c> from an
    /// unreferenced assembly, so they're invoked reflectively and awaited via
    /// <see cref="GameReflection.AwaitPromise"/>.
    /// </summary>
    internal static class LocalDeckConverter
    {
        private static MethodInfo _getFullDeck;
        private static MethodInfo _deleteDeck;
        private static MethodInfo _createDeck;

        private static void EnsureMethods()
        {
            if (_getFullDeck != null) return;
            var t = typeof(DecksManager);
            _getFullDeck = AccessTools.Method(t, "GetFullDeck", new[] { typeof(Guid) });
            _deleteDeck  = AccessTools.Method(t, "DeleteDeck", new[] { typeof(Guid) });
            _createDeck  = AccessTools.Method(t, "CreateDeck",
                new[] { typeof(Client_Deck), typeof(string), typeof(bool) });
        }

        // -----------------------------------------------------------------
        // Public entry points (fire-and-forget; run on the controller)
        // -----------------------------------------------------------------
        public static void MakeLocal(Guid cloudDeckId, string deckName)
        {
            var ctrl = LocalDeckBridge.Controller;
            if (ctrl == null) { Toast.Error("Deck manager not available"); return; }
            ctrl.StartCoroutine(MakeLocalCoroutine(cloudDeckId, deckName));
        }

        public static void MakeCloud(Guid localId)
        {
            var ctrl = LocalDeckBridge.Controller;
            if (ctrl == null) { Toast.Error("Deck manager not available"); return; }
            ctrl.StartCoroutine(MakeCloudCoroutine(localId));
        }

        // -----------------------------------------------------------------
        // Make Local: cloud deck -> text file, then delete cloud deck
        // -----------------------------------------------------------------
        private static IEnumerator MakeLocalCoroutine(Guid cloudDeckId, string deckName)
        {
            EnsureMethods();
            var dm = LocalDeckBridge.GetDecksManager();
            if (dm == null || _getFullDeck == null || _deleteDeck == null)
            {
                Toast.Error("Couldn't access deck service");
                yield break;
            }

            // 1. Fetch full contents (cache-only; resolves fast).
            object fullDeckPromise = SafeInvoke(() => _getFullDeck.Invoke(dm, new object[] { cloudDeckId }),
                "GetFullDeck");
            if (fullDeckPromise == null) { Toast.Error("Couldn't load deck contents"); yield break; }

            bool gotDeck = false; object deckObj = null;
            yield return GameReflection.AwaitPromise(fullDeckPromise, (ok, res) => { gotDeck = ok; deckObj = res; });
            var clientDeck = deckObj as Client_Deck;
            if (!gotDeck || clientDeck == null) { Toast.Error("Couldn't load deck contents"); yield break; }

            // 2. Serialize to Arena export text + capture everything the text
            //    can't carry: format, deck-box art, and user-folder membership.
            string text = LocalDeckBridge.ExportText(clientDeck);
            if (string.IsNullOrWhiteSpace(text)) { Toast.Error("Couldn't export deck text"); yield break; }

            var meta = new LocalDeckMeta();
            try
            {
                var summary = clientDeck.Summary;
                if (summary != null)
                {
                    meta.Format = summary.Format;
                    meta.DeckTileId = summary.DeckTileId;
                    meta.DeckArtId = summary.DeckArtId;
                }
                // Remember which user folder it lived in so Make Cloud can
                // put it back.
                var folder = DeckOrganizationManager.FindFolderContaining(cloudDeckId);
                if (folder != null)
                {
                    meta.FolderId = folder.Id.ToString();
                    meta.FolderName = folder.Name;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"MakeLocal capture meta: {ex.Message}"); }

            // 3. Write the local file FIRST and verify before deleting anything.
            var saved = LocalDeckStore.Save(deckName ?? clientDeck.Summary?.Name ?? "Untitled", text, meta);
            if (saved == null) { Toast.Error("Couldn't write local deck file"); yield break; }

            // 4. Delete the cloud deck.
            object delPromise = SafeInvoke(() => _deleteDeck.Invoke(dm, new object[] { cloudDeckId }), "DeleteDeck");
            var outcome = PromiseOutcome.Unknown;
            if (delPromise != null)
                yield return GameReflection.AwaitPromiseStatus(delPromise, (o, _) => { outcome = o; });

            if (outcome != PromiseOutcome.Succeeded)
            {
                // The local copy is NEVER removed here, whatever the reason.
                //
                // It used to be deleted on anything that wasn't a confirmed
                // success, which is how both copies could disappear. The
                // narrower version that replaced that still deleted on a
                // "confirmed refusal WITH the deck still in the cloud" — but
                // nothing in this code can establish either half. Failed only
                // means the promise reported Successful == false, which covers
                // a server refusal and a client-side error alike; and the
                // corroborating look at the deck list answered "still there"
                // when it could not read the list at all, so a broken check
                // AUTHORISED the delete instead of preventing it.
                //
                // Rather than keep refining a test that can't be made sound,
                // drop the destructive branch. A duplicate the user removes in
                // two clicks beats a deck that exists nowhere.
                DeckManagerControllerPatch.ReloadDecksLikeMTGADoes();
                DeckViewSelectorPatch.RebuildLocalDecks();

                ConfirmActionModal.ShowInfo(
                    "Couldn't confirm the move",
                    $"'{saved.Name}' has been saved to Local Decks, but the game didn't confirm that the " +
                    "cloud copy was removed. The local copy has been kept on purpose.\n\n" +
                    "Check your cloud decks: if it's still there, you have a duplicate and can delete " +
                    "either one. If it's gone, the move worked and there's nothing to do.");
                PerPlayerLog.Info($"MakeLocal: delete of {cloudDeckId} not confirmed ({outcome}), local copy KEPT");
                yield break;
            }

            // 5. Refresh both lists.
            DeckManagerControllerPatch.ReloadDecksLikeMTGADoes();
            DeckViewSelectorPatch.RebuildLocalDecks();
            Toast.Success($"Moved '{saved.Name}' to Local Decks");
            PerPlayerLog.Info($"MakeLocal: {cloudDeckId} -> local '{saved.Name}'");
        }

        // -----------------------------------------------------------------
        // Make Cloud: local text file -> new cloud deck, then delete file
        // -----------------------------------------------------------------
        private static IEnumerator MakeCloudCoroutine(Guid localId)
        {
            EnsureMethods();
            var dm = LocalDeckBridge.GetDecksManager();
            if (dm == null || _createDeck == null) { Toast.Error("Couldn't access deck service"); yield break; }

            // 1. Slot check — abort with a clear modal if the cloud deck cap is
            //    reached (net/event decks don't count toward the cap).
            bool atLimit = false; int limit = 0;
            try { atLimit = dm.DeckLimitReached; limit = dm.GetDeckLimit(); }
            catch (Exception ex) { Plugin.Log.LogWarning($"deck-limit check: {ex.Message}"); }
            if (atLimit)
            {
                ConfirmActionModal.ShowInfo(
                    "No free deck slots",
                    $"You're at the {limit}-deck cloud limit. Delete or move a cloud deck to Local Decks " +
                    "to free a slot, then try again.");
                yield break;
            }

            var local = LocalDeckStore.Get(localId);
            if (local == null) { Toast.Error("Local deck not found"); yield break; }

            // 2. Reconstruct the deck via MTGA's importer.
            bool clamped;
            var deck = LocalDeckBridge.BuildClientDeck(local, out clamped);
            if (deck == null) { Toast.Error($"Couldn't import '{local.Name}'"); yield break; }

            // 3. Stamp a fresh cloud identity + unique name.
            try
            {
                deck.Summary.DeckId = Guid.NewGuid();
                deck.Summary.Description = "";
                deck.Summary.IsNetDeck = false;
                deck.Summary.NetDeckFolderId = null;
                deck.Summary.Name = WrapperDeckUtilities.GetUniqueName(
                    local.Name, dm.GetAllDeckNames());
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"MakeCloud stamp: {ex.Message}"); }

            // 4. Create the cloud deck.
            object createPromise = SafeInvoke(
                () => _createDeck.Invoke(dm, new object[] { deck, "Imported", false }), "CreateDeck");
            if (createPromise == null) { Toast.Error("Couldn't create cloud deck"); yield break; }

            bool created = false; object resultObj = null;
            yield return GameReflection.AwaitPromise(createPromise, (ok, res) => { created = ok; resultObj = res; });
            if (!created)
            {
                Toast.Error($"Server rejected '{local.Name}' (deck invalid or limit reached)");
                yield break;
            }

            // 4b. Restore user-folder membership (if the deck was in one when it
            //     was made local). The new cloud deck's authoritative id is on
            //     the deck instance after CreateDeck (UpdateWith), with the
            //     promise result as a fallback.
            try
            {
                Guid newId = deck.Summary?.DeckId ?? Guid.Empty;
                if (newId == Guid.Empty)
                {
                    var idField = AccessTools.Field(resultObj?.GetType(), "DeckId");
                    if (idField != null) newId = (Guid)idField.GetValue(resultObj);
                }
                var fmeta = local.Meta;
                if (newId != Guid.Empty && fmeta != null &&
                    (!string.IsNullOrEmpty(fmeta.FolderId) || !string.IsNullOrEmpty(fmeta.FolderName)))
                {
                    DeckFolder folder = null;
                    if (Guid.TryParse(fmeta.FolderId, out var fg))
                        folder = DeckOrganizationManager.FindFolderById(fg);
                    if (folder == null && !string.IsNullOrEmpty(fmeta.FolderName))
                        folder = DeckOrganizationManager.Folders
                            .FirstOrDefault(f => string.Equals(f.Name, fmeta.FolderName, StringComparison.OrdinalIgnoreCase));
                    if (folder != null)
                    {
                        DeckOrganizationManager.MoveDeckToFolder(newId, folder.Id);
                        Plugin.Log.LogInfo($"MakeCloud: restored '{local.Name}' to folder '{folder.Name}'");
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"MakeCloud restore folder: {ex.Message}"); }

            // 5. Remove the local file and refresh.
            LocalDeckStore.Delete(localId);
            DeckManagerControllerPatch.ReloadDecksLikeMTGADoes();
            DeckViewSelectorPatch.RebuildLocalDecks();
            if (clamped)
                Toast.Warning($"'{deck.Summary.Name}' imported — some unowned cards were reduced");
            else
                Toast.Success($"'{deck.Summary.Name}' is now a cloud deck");
            PerPlayerLog.Info($"MakeCloud: local '{local.Name}' -> cloud {deck.Summary.DeckId}");
        }

        private static object SafeInvoke(Func<object> f, string label)
        {
            try { return f(); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LocalDeckConverter: {label} threw: {ex.Message}");
                return null;
            }
        }
    }
}
