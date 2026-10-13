using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.Features;
using MTGAEnhancementSuite.Diagnostics;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using TMPro;
using UnityEngine;
using Wizards.Mtga.Decks;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Phase 2 of deck organization: co-opts MTGA's built-in DeckFolderView
    /// system (the "My Decks / Starter Decks / Example Decks" expandable
    /// sections) to render user-defined folders.
    ///
    /// On <see cref="DeckViewSelector.Initialize"/> postfix:
    ///   - For each folder in DeckOrganization.Folders, instantiate the same
    ///     _deckFolderViewPrefab MTGA uses and add it to the internal
    ///     _deckFolders list. Wire its name + GUID.
    ///
    /// On <see cref="DeckViewSelector.SetDecks"/> prefix:
    ///   - For each deck the user has assigned to one of our folders, mutate
    ///     its DeckViewInfo.NetDeckFolderId to point at our folder's GUID.
    ///     MTGA's existing per-folder filter then routes it to our folder
    ///     view naturally — no parallel populate path required.
    ///
    /// On <see cref="DeckViewSelector.SetDecks"/> postfix:
    ///   - Restore the NetDeckFolderId values we mutated. MTGA may reuse
    ///     DeckViewInfo objects across calls; leaving our GUIDs in place
    ///     would poison code paths that read NetDeckFolderId for other
    ///     reasons.
    /// </summary>
    [HarmonyPatch]
    internal static class DeckViewSelectorPatch
    {
        // ---- Reflection caches ----
        private static FieldInfo _folderParentField;
        private static FieldInfo _deckFoldersField;
        private static FieldInfo _onDeckSelectedField;
        private static FieldInfo _onDeckDoubleClickedField;
        private static FieldInfo _onDeckNameEndEditField;
        private static FieldInfo _onDeckClickAndHoldField;
        private static FieldInfo _getCurrentModeField;

        // Tracks DeckViewInfos whose NetDeckFolderId we've mutated this call,
        // so the postfix can put them back. Single-call scope; cleared in
        // the postfix even on exception.
        private static readonly List<DeckViewInfo> _mutatedDecks = new List<DeckViewInfo>();

        // Cached references to the live DeckViewSelector + the folder views
        // we've injected. Used by RebuildUserFolders() so modal-driven
        // mutations (CreateFolder etc.) can re-sync the UI without waiting
        // for MTGA to re-trigger Initialize.
        private static DeckViewSelector _instance;
        private static readonly List<DeckFolderView> _injectedViews = new List<DeckFolderView>();

        // The single "Local Decks" folder pinned to the bottom. Separate from
        // the user-folder system: its tiles are synthetic decks backed by local
        // text files, not server decks routed via NetDeckFolderId.
        private const string LocalFolderName = "Local Decks";
        private static DeckFolderView _localFolderView;
        // Stable GUID for the local folder (distinct from any user folder id).
        private static readonly Guid LocalFolderId =
            new Guid("10ca1dec-0000-0000-0000-000000000001");
        // Cache of synthetic DeckViewInfos so we don't re-parse every local deck
        // on every SetDecks pass (which fires on each bucket/format change).
        // Invalidated by RebuildLocalDecks after any mutation.
        private static List<DeckViewInfo> _localInfoCache;

        private static void EnsureReflectionCache()
        {
            if (_folderParentField != null) return;
            var t = typeof(DeckViewSelector);
            _folderParentField        = AccessTools.Field(t, "_folderParent");
            _deckFoldersField         = AccessTools.Field(t, "_deckFolders");
            _onDeckSelectedField      = AccessTools.Field(t, "_onDeckSelected");
            _onDeckDoubleClickedField = AccessTools.Field(t, "_onDeckDoubleClicked");
            _onDeckNameEndEditField   = AccessTools.Field(t, "_onDeckNameEndEdit");
            _onDeckClickAndHoldField  = AccessTools.Field(t, "_onDeckClickAndHold");
            _getCurrentModeField      = AccessTools.Field(t, "_getCurrentMode");
            if (_folderParentField == null || _deckFoldersField == null)
                Plugin.Log.LogWarning("DeckViewSelectorPatch: required private fields not found via reflection");
        }

        // -----------------------------------------------------------------
        // Initialize postfix: inject our folder views
        // -----------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DeckViewSelector), "Initialize")]
        private static void Initialize_Postfix(DeckViewSelector __instance, bool simpleSelect)
        {
            // The catch lives here, not only in the Core: if an Arena update
            // changes a signature the Core calls, the MissingMethodException
            // is thrown while JIT-compiling the Core — before its own try is
            // entered — and would escape into DeckSelectBlade.Init, leaving
            // the Home play blade half-built (Arena update of 2026-10-13).
            try
            {
                using (FrameCost.Measure("DeckViewSelector.Initialize"))
                    Initialize_PostfixCore(__instance, simpleSelect);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DeckViewSelectorPatch.Initialize_Postfix: {ex}");
            }
        }

        private static void Initialize_PostfixCore(DeckViewSelector __instance, bool simpleSelect)
        {
            // simpleSelect is the deck-picker variant used elsewhere (e.g. in
            // challenge views) — no native folder UI, no place for ours either.
            if (simpleSelect) return;

            try
            {
                EnsureReflectionCache();
                if (_folderParentField == null || _deckFoldersField == null) return;

                _instance = __instance;
                _injectedViews.Clear();

                var folderParent = _folderParentField.GetValue(__instance) as Transform;
                var deckFolders  = _deckFoldersField.GetValue(__instance) as List<DeckFolderView>;
                if (folderParent == null || deckFolders == null) return;

                // The click handler we wire into our folder views must be
                // DeckViewSelector.SelectDeck (the method) — that's what
                // MTGA's own folders use. SelectDeck does the per-folder
                // SetIsSelected(true) (the deck-box-open animation on the
                // clicked tile) and THEN invokes _onDeckSelected (the
                // caller's callback). Wiring our folders directly to
                // _onDeckSelected skips the animation step.
                //
                // The DeckViewInfo overload of SelectDeck is the one that
                // does the animation; the string overload just defers to it.
                var onDeckSelected      = new Action<DeckViewInfo>(__instance.SelectDeck);
                var onDeckDoubleClicked = _onDeckDoubleClickedField?.GetValue(__instance) as Action<DeckViewInfo>;
                var onDeckNameEndEdit   = _onDeckNameEndEditField?.GetValue(__instance)   as Action<DeckViewInfo, string>;
                // Multi-select (click-and-hold) wiring, same as native folders.
                var onDeckClickAndHold  = _onDeckClickAndHoldField?.GetValue(__instance)  as Action<DeckViewInfo>;
                var getCurrentMode      = _getCurrentModeField?.GetValue(__instance)      as Func<DeckManagerMode>;

                // Use the same prefab MTGA uses for its server-side folders
                // (My Decks / Starter Decks / Example Decks). Public field.
                var prefab = __instance._deckFolderViewPrefab;
                if (prefab == null)
                {
                    Plugin.Log.LogWarning("DeckViewSelectorPatch: _deckFolderViewPrefab is null");
                    return;
                }

                // We want user folders at the TOP — above My Decks, Starter
                // Decks, Example Decks. Folders that already existed in
                // _deckFolders / _folderParent at this point are MTGA's
                // native ones; we insert ours at index 0 (and sibling
                // index 0 in the Transform) so they sort above.
                int injected = 0;
                for (int i = 0; i < DeckOrganizationManager.Folders.Count; i++)
                {
                    var folder = DeckOrganizationManager.Folders[i];
                    var view = UnityEngine.Object.Instantiate(prefab, folderParent);
                    // OnFolderToggle is a private member on DeckViewSelector;
                    // build a no-op delegate matching its signature. MTGA's
                    // selection logic doesn't rely on it firing.
                    view.Initialize(onDeckSelected, onDeckDoubleClicked, onDeckClickAndHold,
                        getCurrentMode, onDeckNameEndEdit, _ => { });
                    view.FolderId = folder.Id;

                    // Set the visible folder name. MTGA's Localize component
                    // expects a localization key + fallback; we pass an
                    // empty key that won't resolve so the fallback (the
                    // user-entered folder name) is what shows.
                    var nameLoc = view.FolderNameLocKey;
                    if (nameLoc != null)
                    {
                        nameLoc.SetText("", null, folder.Name ?? "Untitled");
                        // Defense in depth: also write the raw TMP text on
                        // any TextMeshProUGUI under the loc component.
                        var tmp = nameLoc.GetComponentInChildren<TextMeshProUGUI>(true);
                        if (tmp != null) tmp.text = folder.Name ?? "Untitled";
                    }

                    // Visual order: pin user folders to the top, preserving
                    // their own relative ordering.
                    view.transform.SetSiblingIndex(i);
                    // Logical order (drives SetDecks iteration): same.
                    deckFolders.Insert(i, view);
                    _injectedViews.Add(view);

                    // Attach right-click handler so the user can rename or
                    // delete this folder via context menu. Only user-created
                    // folders get this; built-in folders are left alone.
                    //
                    // The handler has to live on the Toggle's GameObject
                    // (the actual raycast hit target on the header) — if we
                    // put it on the DeckFolderView's root, UGUI's event
                    // dispatch stops at the Toggle's inherited
                    // IPointerClickHandler and never reaches us.
                    AttachContextHandler(view, folder.Id);

                    injected++;
                }

                if (injected > 0)
                    Plugin.Log.LogInfo($"DeckViewSelectorPatch: injected {injected} user folder view(s)");

                // Pin the "Local Decks" folder to the very bottom.
                InjectLocalDecksFolder(__instance, folderParent, deckFolders, prefab,
                    onDeckNameEndEdit);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckViewSelectorPatch.Initialize_Postfix failed: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------
        // Local Decks folder (bottom): synthetic, text-file-backed decks
        // -----------------------------------------------------------------
        private static void InjectLocalDecksFolder(DeckViewSelector instance, Transform folderParent,
            List<DeckFolderView> deckFolders, DeckFolderView prefab, Action<DeckViewInfo, string> _unused)
        {
            try
            {
                var view = UnityEngine.Object.Instantiate(prefab, folderParent);
                // Tile click → MTGA's own SelectDeck (animation + cross-folder
                // deselect; sets _selectedDeck=null for our synthetic ids so
                // native buttons stay inert). Double-click → our editor.
                // Name-edit → rename the local file.
                view.Initialize(
                    new Action<DeckViewInfo>(instance.SelectDeck),
                    new Action<DeckViewInfo>(Features.LocalDeckController.OnDoubleClick),
                    // No click-and-hold / manager mode: multi-select acts on
                    // server decks and our tiles are synthetic. Both are
                    // null-checked by DeckView.
                    null,
                    null,
                    new Action<DeckViewInfo, string>(Features.LocalDeckController.OnNameEdit),
                    _ => { });
                view.FolderId = LocalFolderId;

                var nameLoc = view.FolderNameLocKey;
                if (nameLoc != null)
                {
                    nameLoc.SetText("", null, LocalFolderName);
                    var tmp = nameLoc.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (tmp != null) tmp.text = LocalFolderName;
                }

                // Bottom of the list (after native + user folders).
                view.transform.SetAsLastSibling();
                deckFolders.Add(view);
                _localFolderView = view;

                PopulateLocalFolder();
                Plugin.Log.LogInfo("DeckViewSelectorPatch: injected Local Decks folder");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"InjectLocalDecksFolder failed: {ex.Message}");
                _localFolderView = null;
            }
        }

        /// <summary>
        /// Rebuilds the synthetic tiles inside the Local Decks folder from
        /// <see cref="Features.LocalDeckStore"/>. Safe to call repeatedly.
        /// </summary>
        public static void PopulateLocalFolder()
        {
            if (_localFolderView == null) return;
            try
            {
                if (_localInfoCache == null)
                {
                    _localInfoCache = new List<DeckViewInfo>();
                    foreach (var local in Features.LocalDeckStore.All)
                    {
                        var info = Features.LocalDeckBridge.BuildDeckViewInfo(local);
                        if (info != null) _localInfoCache.Add(info);
                    }
                }
                // allowUnownedCards: true — local decks may reference cards the
                // player doesn't own; we still want to show them.
                _localFolderView.SetDecks(_localInfoCache, true);
                if (!_localFolderView.gameObject.activeSelf)
                    _localFolderView.gameObject.SetActive(true);

                // Show the same "+ New Deck" button MTGA puts on My Decks,
                // wired to create a brand-new local deck. (Idempotent — the
                // view reuses the button instance after the first call.)
                _localFolderView.ShowCreateDeckButton(
                    new Action(Features.LocalDeckEditor.CreateNew));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"PopulateLocalFolder failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-reads local decks from disk and refreshes the folder. Call after
        /// any make-local / make-cloud / rename / edit mutation.
        /// </summary>
        public static void RebuildLocalDecks()
        {
            Features.LocalDeckStore.Load();
            _localInfoCache = null; // force rebuild of synthetic tiles
            PopulateLocalFolder();
        }

        // -----------------------------------------------------------------
        // SetDecks prefix: temporarily mutate NetDeckFolderId so MTGA's own
        // filter routes decks into our folder views.
        // -----------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckViewSelector), "SetDecks")]
        private static void SetDecks_Prefix(System.Collections.Generic.IReadOnlyList<DeckViewInfo> decks)
        {
            using (FrameCost.Measure("SetDecks.Prefix")) SetDecks_PrefixCore(decks);
        }

        private static void SetDecks_PrefixCore(System.Collections.Generic.IReadOnlyList<DeckViewInfo> decks)
        {
            _mutatedDecks.Clear();
            if (decks == null) return;
            if (DeckOrganizationManager.Folders.Count == 0) return;

            // Build a deckId -> folderId map once. Linear scan over folders
            // (handful) × DeckIds (handful per folder) is fine.
            var assignments = new Dictionary<Guid, Guid>();
            foreach (var folder in DeckOrganizationManager.Folders)
                foreach (var deckId in folder.DeckIds)
                    assignments[deckId] = folder.Id;

            foreach (var d in decks)
            {
                if (d == null) continue;
                // We only override decks the user explicitly assigned to one
                // of our folders. Decks already inside a NetDeckFolder
                // (Starter / Example / event-curated) stay where they are.
                if (d.NetDeckFolderId.HasValue) continue;
                if (!assignments.TryGetValue(d.deckId, out var folderId)) continue;
                d.NetDeckFolderId = folderId;
                _mutatedDecks.Add(d);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DeckViewSelector), "SetDecks")]
        private static void SetDecks_Postfix()
        {
            using (FrameCost.Measure("SetDecks.Postfix")) SetDecks_PostfixCore();
        }

        private static void SetDecks_PostfixCore()
        {
            // Restore. MTGA may reuse the same DeckViewInfo across calls;
            // leaving our GUID on NetDeckFolderId would mislead other code.
            foreach (var d in _mutatedDecks)
                if (d != null) d.NetDeckFolderId = null;
            _mutatedDecks.Clear();

            // Force our user-folder views visible regardless of deck count.
            // MTGA's SetDecks deactivates any DeckFolderView whose deck list
            // came up empty (this is what powers the "Starter Decks folder
            // hides when no starter decks match the format filter" behavior).
            // For user folders we want the opposite: a freshly-created
            // empty folder, or a folder that's only emptied by the current
            // format filter, should still show its header so the user can
            // see + interact with it. Otherwise creating a new folder looks
            // like it didn't work — the data lands fine but MTGA hides it
            // before the user can see it.
            foreach (var view in _injectedViews)
            {
                if (view == null) continue;
                if (!view.gameObject.activeSelf)
                    view.gameObject.SetActive(true);
            }

            // MTGA's SetDecks also ran on our Local Decks folder with the cloud
            // deck list (which never matches our synthetic ids), so it emptied
            // and hid it. Repopulate from disk and force-show.
            PopulateLocalFolder();
        }

        // Attaches the right-click context menu handler to the Toggle's
        // GameObject (where UGUI raycasts actually land for the header)
        // plus the root, as a bubble-up safety net.
        //
        // We deliberately do NOT attach to every raycast-target Image under
        // the header. UGUI's ExecuteEvents.ExecuteHierarchy stops walking
        // up the parent chain at the first GameObject with an
        // IPointerClickHandler. The chevron `>` is a child raycast Image:
        // if we put a FolderContextMenuHandler on it, left-clicks land on
        // the chevron, hit our handler first (which only acts on right-
        // clicks), and never bubble to the Toggle — so the chevron stops
        // expanding/collapsing the folder. Skipping the per-image attach
        // lets chevron clicks travel up to the Toggle's own
        // IPointerClickHandler.
        private static FieldInfo _folderToggleField;
        private static void AttachContextHandler(DeckFolderView view, Guid folderId)
        {
            if (view == null) return;

            void Add(GameObject go)
            {
                if (go == null) return;
                var h = go.GetComponent<FolderContextMenuHandler>()
                        ?? go.AddComponent<FolderContextMenuHandler>();
                h.FolderId = folderId;
            }

            // 1) The DeckFolderView root (safety net).
            Add(view.gameObject);

            // 2) The Toggle's GameObject — the most reliable click target,
            //    found via reflection on the private _folderToggle field.
            //    Both the Toggle's own OnPointerClick (flips state on left-
            //    click) and our FolderContextMenuHandler (shows context
            //    menu on right-click) coexist on this GameObject; UGUI
            //    invokes both for any pointer click event on the GO.
            if (_folderToggleField == null)
                _folderToggleField = AccessTools.Field(typeof(DeckFolderView), "_folderToggle");
            var toggle = _folderToggleField?.GetValue(view) as Component;
            if (toggle != null) Add(toggle.gameObject);
        }

        /// <summary>
        /// Destroys our currently-injected DeckFolderView clones and re-runs
        /// the injection against the current <see cref="DeckOrganization.Folders"/>.
        /// Call after any folder-set mutation (Create, Delete, Rename, Reorder)
        /// so newly-added folders show up without waiting for the user to leave
        /// and re-enter the deck screen.
        /// </summary>
        public static void RebuildUserFolders()
        {
            try
            {
                if (_instance == null) return;
                EnsureReflectionCache();
                var deckFolders = _deckFoldersField?.GetValue(_instance) as List<DeckFolderView>;
                if (deckFolders == null) return;

                // Tear down old injected views.
                foreach (var view in _injectedViews)
                {
                    if (view == null) continue;
                    deckFolders.Remove(view);
                    UnityEngine.Object.Destroy(view.gameObject);
                }
                _injectedViews.Clear();

                // Tear down the old Local Decks folder too — Initialize_Postfix
                // re-injects it, so leaving the old one would duplicate it.
                if (_localFolderView != null)
                {
                    deckFolders.Remove(_localFolderView);
                    UnityEngine.Object.Destroy(_localFolderView.gameObject);
                    _localFolderView = null;
                }

                // Pretend we never ran the Initialize injection — re-run it.
                Initialize_Postfix(_instance, simpleSelect: false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckViewSelectorPatch.RebuildUserFolders: {ex.Message}");
            }
        }
    }
}
