using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.Features;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Phase 3/4 of deck organization. Injects four buttons into MTGA's
    /// deck-manager bottom action strip and swaps between two display modes
    /// driven by <see cref="DeckMultiSelectState"/>:
    ///
    ///   Normal mode    → MTGA's native per-deck buttons (Edit, Delete,
    ///                    Clone, Favorite, …) act on the focused deck;
    ///                    our "Select" button is also visible.
    ///   Multi-select   → native buttons hidden; we show Cancel,
    ///                    Delete N, Move to Folder instead.
    ///
    /// All four new buttons are clones of <c>_favoriteDeckButton</c> with
    /// their persistent inspector-wired listeners disabled (so the heart-
    /// favorite action doesn't fire), inner images destroyed, and a fresh
    /// vector icon painted via Image rectangles.
    /// </summary>
    [HarmonyPatch(typeof(DeckManagerController), "Awake")]
    internal static class DeckManagerControllerPatch
    {
        private const string SelectBtnName     = "MTGAES_SelectButton";
        private const string CancelBtnName     = "MTGAES_CancelButton";
        private const string DeleteBtnName     = "MTGAES_DeleteButton";
        private const string MoveBtnName       = "MTGAES_MoveButton";
        // Normal-mode equivalents: one-deck move (visible when a deck is
        // focused) and a standing "create a new folder" button (visible
        // any time the deck manager is open in normal mode).
        private const string MoveSingleBtnName = "MTGAES_MoveSingleButton";
        private const string NewFolderBtnName  = "MTGAES_NewFolderButton";
        // Local-deck conversion: "Make Local" shows for a focused cloud deck,
        // "Make Cloud" shows for a focused local deck.
        private const string MakeLocalBtnName  = "MTGAES_MakeLocalButton";
        private const string MakeCloudBtnName  = "MTGAES_MakeCloudButton";

        // Reflection caches
        private static FieldInfo _favoriteBtnField;
        private static FieldInfo _editBtnField;
        private static FieldInfo _deleteBtnField;
        private static FieldInfo _cloneBtnField;
        private static FieldInfo _craftAllBtnField;
        private static FieldInfo _deckDetailsBtnField;
        private static FieldInfo _exportBtnField;
        private static FieldInfo _importBtnField;
        private static FieldInfo _collectionBtnField;
        private static FieldInfo _selectorInstanceField;
        private static FieldInfo _deckViewInfosField;
        private static FieldInfo _deckBucketDropdownField;
        // The currently-focused deck (the one MTGA's per-deck buttons act on).
        // Used to drive the single-deck Move button's visibility + click target.
        private static FieldInfo _selectedDeckField;

        // Per-controller bookkeeping
        private static DeckManagerController _controller;
        private static readonly List<GameObject> _nativeButtons = new List<GameObject>();
        private static GameObject _cancelBtnGO;
        private static GameObject _deleteBtnGO;
        private static GameObject _moveBtnGO;
        private static GameObject _moveSingleBtnGO;
        private static GameObject _newFolderBtnGO;
        private static GameObject _makeLocalBtnGO;
        private static GameObject _makeCloudBtnGO;

        [HarmonyPostfix]
        private static void Postfix(DeckManagerController __instance)
        {
            try
            {
                EnsureFieldCache();
                _controller = __instance;

                var favoriteBtn = _favoriteBtnField?.GetValue(__instance) as Component;
                if (favoriteBtn == null) return;
                var sourceGO = favoriteBtn.gameObject;
                var parent = sourceGO.transform.parent;
                if (parent == null) return;

                GameObject selectGO = parent.Find(SelectBtnName)?.gameObject;
                if (selectGO == null)
                {
                    selectGO = BuildButton(sourceGO, parent, SelectBtnName,
                        sourceGO.transform.GetSiblingIndex() + 1,
                        PaintCheckIcon, OnSelectClicked);
                }
                ButtonTooltip.Attach(selectGO, "Select multiple decks");

                if (parent.Find(CancelBtnName) == null)
                {
                    _cancelBtnGO = BuildButton(sourceGO, parent, CancelBtnName,
                        sourceGO.transform.GetSiblingIndex() + 2,
                        PaintCancelIcon, OnCancelClicked);
                    _cancelBtnGO.SetActive(false);
                }
                ButtonTooltip.Attach(_cancelBtnGO, "Cancel");

                if (parent.Find(DeleteBtnName) == null)
                {
                    _deleteBtnGO = BuildButton(sourceGO, parent, DeleteBtnName,
                        sourceGO.transform.GetSiblingIndex() + 3,
                        PaintTrashIcon, OnDeleteClicked);
                    _deleteBtnGO.SetActive(false);
                }
                ButtonTooltip.Attach(_deleteBtnGO, "Delete selected decks");

                if (parent.Find(MoveBtnName) == null)
                {
                    _moveBtnGO = BuildButton(sourceGO, parent, MoveBtnName,
                        sourceGO.transform.GetSiblingIndex() + 4,
                        PaintFolderIcon, OnMoveClicked);
                    _moveBtnGO.SetActive(false);
                }
                ButtonTooltip.Attach(_moveBtnGO, "Move to folder");

                // Normal-mode: "Move this deck to folder" — visible only when
                // a deck is currently focused (mirrors how MTGA enables/
                // disables Edit, Delete, Clone, etc.). Visibility is kept in
                // sync by the Harmony postfix on UpdateSelectedDeckView below.
                if (parent.Find(MoveSingleBtnName) == null)
                {
                    _moveSingleBtnGO = BuildButton(sourceGO, parent, MoveSingleBtnName,
                        sourceGO.transform.GetSiblingIndex() + 5,
                        PaintFolderIcon, OnMoveSingleClicked);
                    _moveSingleBtnGO.SetActive(false);
                }
                else
                {
                    _moveSingleBtnGO = parent.Find(MoveSingleBtnName).gameObject;
                }
                ButtonTooltip.Attach(_moveSingleBtnGO, "Move this deck to folder");

                // Normal-mode: standing "+ New folder" — always visible in
                // normal mode so users can create folders without first
                // entering multi-select.
                if (parent.Find(NewFolderBtnName) == null)
                {
                    _newFolderBtnGO = BuildButton(sourceGO, parent, NewFolderBtnName,
                        sourceGO.transform.GetSiblingIndex() + 6,
                        PaintNewFolderIcon, OnNewFolderClicked);
                }
                else
                {
                    _newFolderBtnGO = parent.Find(NewFolderBtnName).gameObject;
                }
                ButtonTooltip.Attach(_newFolderBtnGO, "Create a new folder");

                // "Make Local": export the focused cloud deck to a local text
                // file and free its cloud slot. Visible only when a cloud deck
                // is focused (driven by OnSelectionFocusChanged).
                if (parent.Find(MakeLocalBtnName) == null)
                {
                    _makeLocalBtnGO = BuildButton(sourceGO, parent, MakeLocalBtnName,
                        sourceGO.transform.GetSiblingIndex() + 7,
                        PaintMakeLocalIcon, OnMakeLocalClicked);
                    _makeLocalBtnGO.SetActive(false);
                }
                else
                {
                    _makeLocalBtnGO = parent.Find(MakeLocalBtnName).gameObject;
                }
                ButtonTooltip.Attach(_makeLocalBtnGO, "Move to Local Decks (frees a cloud slot)");

                // "Make Cloud": import the focused local deck as a real cloud
                // deck (needs a free slot). Visible only when a local deck is
                // focused.
                if (parent.Find(MakeCloudBtnName) == null)
                {
                    _makeCloudBtnGO = BuildButton(sourceGO, parent, MakeCloudBtnName,
                        sourceGO.transform.GetSiblingIndex() + 8,
                        PaintMakeCloudIcon, OnMakeCloudClicked);
                    _makeCloudBtnGO.SetActive(false);
                }
                else
                {
                    _makeCloudBtnGO = parent.Find(MakeCloudBtnName).gameObject;
                }
                ButtonTooltip.Attach(_makeCloudBtnGO, "Make this a cloud deck");

                // Collect MTGA's native per-deck buttons so we can hide/show
                // them when multi-select toggles. Skip deckOrder/deckBucket
                // (sort + format filter) — those stay visible always.
                _nativeButtons.Clear();
                AddNative(_editBtnField, __instance);
                AddNative(_deleteBtnField, __instance);
                AddNative(_cloneBtnField, __instance);
                AddNative(_craftAllBtnField, __instance);
                AddNative(_deckDetailsBtnField, __instance);
                AddNative(_exportBtnField, __instance);
                AddNative(_importBtnField, __instance);
                AddNative(_collectionBtnField, __instance);
                AddNative(_favoriteBtnField, __instance);

                // Subscribe once. The handler reads DeckMultiSelectState live,
                // so even if our cached controller goes stale, the worst case
                // is a no-op (we null-check the buttons inside ApplyMode).
                DeckMultiSelectState.OnChanged -= ApplyMode;
                DeckMultiSelectState.OnChanged += ApplyMode;

                ApplyMode(); // ensure correct initial state

                Plugin.Log.LogInfo("DeckManagerControllerPatch: bottom-strip buttons injected");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckManagerControllerPatch.Awake: {ex.Message}");
            }
        }

        private static void EnsureFieldCache()
        {
            if (_favoriteBtnField != null) return;
            var t = typeof(DeckManagerController);
            _favoriteBtnField      = AccessTools.Field(t, "_favoriteDeckButton");
            _editBtnField          = AccessTools.Field(t, "_editDeckButton");
            _deleteBtnField        = AccessTools.Field(t, "_deleteDeckButton");
            _cloneBtnField         = AccessTools.Field(t, "_cloneDeckButton");
            _craftAllBtnField      = AccessTools.Field(t, "_craftAllButton");
            _deckDetailsBtnField   = AccessTools.Field(t, "_deckDetailsButton");
            _exportBtnField        = AccessTools.Field(t, "_exportDeckButton");
            _importBtnField        = AccessTools.Field(t, "_importDeckButton");
            _collectionBtnField    = AccessTools.Field(t, "_collectionButton");
            _selectorInstanceField = AccessTools.Field(t, "_deckSelectorInstance");
            _deckViewInfosField    = AccessTools.Field(t, "_deckViewInfos");
            _deckBucketDropdownField = AccessTools.Field(t, "_deckBucketDropdown");
            _selectedDeckField     = AccessTools.Field(t, "_selectedDeck");
        }

        private static void AddNative(FieldInfo field, object instance)
        {
            if (field == null) return;
            var btn = field.GetValue(instance) as Component;
            if (btn != null) _nativeButtons.Add(btn.gameObject);
        }

        // -----------------------------------------------------------------
        // Mode toggle: native buttons vs. bulk-action buttons
        // -----------------------------------------------------------------
        private static void ApplyMode()
        {
            bool multi = DeckMultiSelectState.IsActive;
            // Native per-deck buttons hide during multi-select. We don't
            // force-enable them on exit — MTGA's own selection logic flips
            // SetActive based on whether a deck is focused.
            foreach (var go in _nativeButtons)
                if (go != null) go.SetActive(!multi);

            if (_cancelBtnGO != null) _cancelBtnGO.SetActive(multi);
            if (_deleteBtnGO != null) _deleteBtnGO.SetActive(multi);
            if (_moveBtnGO   != null) _moveBtnGO.SetActive(multi);

            // Standing "+ New folder" lives in normal mode, hides in multi-
            // select (where Move-to-folder covers the same ground).
            if (_newFolderBtnGO != null) _newFolderBtnGO.SetActive(!multi);

            // Focus-dependent buttons. The UpdateSelectedDeckView postfix keeps
            // these in sync as the user clicks around; this call here covers the
            // mode-toggle case (e.g. exiting multi-select while a deck is
            // focused). Cloud focus drives Move + Make Local; local focus drives
            // Make Cloud.
            bool cloudFocus = !multi && IsDeckCurrentlyFocused();
            bool localFocus = !multi && Features.LocalDeckController.HasLocalSelection;
            if (_moveSingleBtnGO != null) _moveSingleBtnGO.SetActive(cloudFocus);
            if (_makeLocalBtnGO  != null) _makeLocalBtnGO.SetActive(cloudFocus);
            if (_makeCloudBtnGO  != null) _makeCloudBtnGO.SetActive(localFocus);

            // Refresh Delete-N label whenever the selection set changes.
            if (multi && _deleteBtnGO != null) UpdateDeleteCountLabel();
        }

        /// <summary>
        /// Reads <see cref="DeckManagerController._selectedDeck"/> via the
        /// cached <see cref="_selectedDeckField"/>. Null deck → no focus.
        /// </summary>
        private static bool IsDeckCurrentlyFocused()
        {
            if (_controller == null || _selectedDeckField == null) return false;
            try { return _selectedDeckField.GetValue(_controller) != null; }
            catch { return false; }
        }

        /// <summary>
        /// Called by the Harmony postfix on
        /// <c>DeckManagerController.UpdateSelectedDeckView</c> — the same hook
        /// MTGA uses to flip <c>Interactable</c> on its own per-deck buttons.
        /// Branches on whether the focused deck is one of our local decks:
        ///   - cloud deck focused → show Move-to-folder + Make Local
        ///   - local deck focused → show Make Cloud
        ///   - nothing focused    → hide all three
        /// Also forwards the selection to <see cref="Features.LocalDeckController"/>
        /// so the local-selection state stays current.
        /// </summary>
        internal static void OnSelectionFocusChanged(Wizards.Mtga.Decks.DeckViewInfo info)
        {
            Features.LocalDeckController.OnSelectionChanged(info);

            if (DeckMultiSelectState.IsActive) return;

            bool isLocal = info != null && Features.LocalDeckStore.IsLocal(info.deckId);
            bool cloudFocus = info != null && !isLocal;

            if (_moveSingleBtnGO != null) _moveSingleBtnGO.SetActive(cloudFocus);
            if (_makeLocalBtnGO  != null) _makeLocalBtnGO.SetActive(cloudFocus);
            if (_makeCloudBtnGO  != null) _makeCloudBtnGO.SetActive(isLocal);
        }

        private static void UpdateDeleteCountLabel()
        {
            // No native label on our painted buttons — but we paint a small
            // number bubble onto the delete icon. Implemented inside
            // PaintTrashIcon / RefreshTrashCount.
            RefreshTrashCount(_deleteBtnGO);
        }

        // -----------------------------------------------------------------
        // Button click handlers
        // -----------------------------------------------------------------
        private static void OnSelectClicked()
        {
            DeckMultiSelectState.ToggleMode();
        }

        private static void OnCancelClicked()
        {
            DeckMultiSelectState.ExitMode();
        }

        private static void OnDeleteClicked()
        {
            int n = DeckMultiSelectState.SelectionCount;
            if (n <= 0) return;
            var ids = new List<Guid>(DeckMultiSelectState.SelectedIds);
            ConfirmDeleteModal.Show(n, () => DoBulkDelete(ids));
        }

        private static void OnMoveClicked()
        {
            int n = DeckMultiSelectState.SelectionCount;
            if (n <= 0) return;
            var ids = new List<Guid>(DeckMultiSelectState.SelectedIds);
            // Folders are a cloud-deck concept; local decks already live in the
            // Local Decks folder. Reject a move if any local deck is selected.
            if (ids.Any(id => Features.LocalDeckStore.IsLocal(id)))
            {
                Toast.Warning("Local decks can't be moved into folders");
                return;
            }
            MoveToFolderModal.Show(ids);
        }

        // Normal-mode click: move the currently-focused deck. We re-read the
        // focused deck at click time rather than caching it — MTGA's
        // <c>_selectedDeck</c> is the source of truth, and depending on a
        // cached snapshot has burned us before (post-refresh focus changes).
        private static void OnMoveSingleClicked()
        {
            if (_controller == null || _selectedDeckField == null) return;
            var deck = _selectedDeckField.GetValue(_controller);
            if (deck == null) return;

            // Client_Deck.Id — could be exposed as a field OR a property
            // depending on MTGA's current patch. Try both.
            var type = deck.GetType();
            Guid id = Guid.Empty;
            try
            {
                var idField = AccessTools.Field(type, "Id");
                if (idField != null) id = (Guid)idField.GetValue(deck);
                else
                {
                    var idProp = AccessTools.Property(type, "Id");
                    if (idProp != null) id = (Guid)idProp.GetValue(deck);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"OnMoveSingleClicked: could not read deck Id: {ex.Message}");
                return;
            }
            if (id == Guid.Empty) return;

            MoveToFolderModal.Show(new List<Guid> { id });
        }

        private static void OnNewFolderClicked()
        {
            // Empty initial selection — just create a folder. The user can
            // populate it later via Move-to-folder.
            MoveToFolderModal.ShowCreateFolderModal();
        }

        // Make Local: act on the focused CLOUD deck.
        private static void OnMakeLocalClicked()
        {
            if (_controller == null || _selectedDeckField == null) return;
            var deck = _selectedDeckField.GetValue(_controller);
            if (deck == null) { Toast.Info("Select a deck first"); return; }
            Guid id = ReadDeckId(deck);
            if (id == Guid.Empty) return;
            string name = ReadDeckName(deck);
            ConfirmActionModal.Show(
                $"Move \"{name}\" to Local Decks?",
                "It will be exported to a text file and removed from your cloud decks, freeing a slot. You can convert it back any time.",
                "Move to Local",
                () => Features.LocalDeckConverter.MakeLocal(id, name));
        }

        // Make Cloud: act on the focused LOCAL deck.
        private static void OnMakeCloudClicked()
        {
            var localId = Features.LocalDeckController.SelectedLocalDeckId;
            if (!localId.HasValue) { Toast.Info("Select a local deck first"); return; }
            Features.LocalDeckConverter.MakeCloud(localId.Value);
        }

        private static Guid ReadDeckId(object deck)
        {
            var type = deck.GetType();
            try
            {
                var idField = AccessTools.Field(type, "Id");
                if (idField != null) return (Guid)idField.GetValue(deck);
                var idProp = AccessTools.Property(type, "Id");
                if (idProp != null) return (Guid)idProp.GetValue(deck);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"ReadDeckId: {ex.Message}"); }
            return Guid.Empty;
        }

        private static string ReadDeckName(object deck)
        {
            try
            {
                var summaryProp = AccessTools.Property(deck.GetType(), "Summary");
                var summary = summaryProp?.GetValue(deck);
                if (summary != null)
                {
                    var nameField = AccessTools.Field(summary.GetType(), "Name");
                    if (nameField != null) return nameField.GetValue(summary) as string ?? "this deck";
                }
            }
            catch { }
            return "this deck";
        }

        // -----------------------------------------------------------------
        // Mutations (called from the modals)
        // -----------------------------------------------------------------
        public static void DoBulkDelete(IReadOnlyList<Guid> deckIds)
        {
            if (deckIds == null || deckIds.Count == 0) return;
            if (_controller == null) return;
            // Run as a coroutine on the controller so we can yield until all
            // DeleteDeck promises resolve before refreshing — matching MTGA's
            // own OnDeckDeleteComplete → Coroutine_LoadDecks chain.
            _controller.StartCoroutine(BulkDeleteCoroutine(deckIds));
        }

        private static System.Collections.IEnumerator BulkDeleteCoroutine(IReadOnlyList<Guid> deckIds)
        {
            // Partition: local decks are files on disk; cloud decks go through
            // the server DeleteDeck path.
            var localIds = deckIds.Where(id => Features.LocalDeckStore.IsLocal(id)).ToList();
            var cloudIds = deckIds.Where(id => !Features.LocalDeckStore.IsLocal(id)).ToList();

            // Delete local decks immediately (synchronous file ops).
            foreach (var id in localIds) Features.LocalDeckStore.Delete(id);
            if (localIds.Count > 0) DeckViewSelectorPatch.RebuildLocalDecks();

            // Vedi DeckListReconcilePatch: TypeByName("Pantry") scansiona tutte
            // le assembly caricate. PantryGet la fa una volta sola per sessione.
            var dm = GameReflection.PantryGet<DecksManager>();
            if (dm == null)
            {
                Plugin.Log.LogWarning("Bulk delete: DecksManager not in Pantry");
                if (localIds.Count > 0) DeckMultiSelectState.ExitMode();
                yield break;
            }
            var deleteMethod = AccessTools.Method(typeof(DecksManager), "DeleteDeck",
                new[] { typeof(Guid) });
            if (deleteMethod == null)
            {
                Plugin.Log.LogWarning("Bulk delete: DecksManager.DeleteDeck(Guid) not found");
                if (localIds.Count > 0) DeckMultiSelectState.ExitMode();
                yield break;
            }

            // Fire all the cloud deletes; collect the returned Promise objects so
            // we can poll IsDone on each before refreshing. Each promise is
            // typed Promise<bool> in an unreferenced assembly, so we keep
            // them as `object` and reflect on IsDone per type.
            var promises = new List<object>();
            foreach (var id in cloudIds)
            {
                object promise = null;
                try { promise = deleteMethod.Invoke(dm, new object[] { id }); }
                catch (Exception ex) { Plugin.Log.LogWarning($"DeleteDeck({id}) threw: {ex.Message}"); }
                if (promise != null) promises.Add(promise);
            }
            Plugin.Log.LogInfo($"Bulk delete: {localIds.Count} local, dispatched {promises.Count}/{cloudIds.Count} cloud promises");

            // Drop cloud ids from the org map immediately; reconcile will tidy up
            // later, but this avoids transient stale state.
            foreach (var id in cloudIds) DeckOrganizationManager.ForgetDeck(id);

            // Wait for all promises. Cap total wait at 30s so a stuck
            // promise doesn't lock the UI forever.
            const float MaxWait = 30f;
            float t = 0f;
            while (t < MaxWait)
            {
                bool allDone = true;
                foreach (var p in promises)
                {
                    var isDoneProp = AccessTools.Property(p.GetType(), "IsDone");
                    bool done = isDoneProp != null && (bool)isDoneProp.GetValue(p);
                    if (!done) { allDone = false; break; }
                }
                if (allDone) break;
                yield return null;
                t += UnityEngine.Time.unscaledDeltaTime;
            }

            DeckMultiSelectState.ExitMode();
            // Now invoke MTGA's own canonical refresh — it re-fetches decks
            // from the server (which has them gone), rebuilds buckets,
            // updates the count text, and re-distributes via SetDecks.
            ReloadDecksLikeMTGADoes();
        }

        /// <summary>
        /// Reproduces MTGA's <c>OnDeckDeleteComplete</c> tail: start the
        /// private <c>Coroutine_LoadDecks(selectedBucket, null)</c> on the
        /// controller. We can't call the method via reflection like a normal
        /// function because it's a coroutine — we need StartCoroutine on
        /// the returned IEnumerator.
        /// </summary>
        internal static void ReloadDecksLikeMTGADoes()
        {
            try
            {
                if (_controller == null) return;
                var coroutineMethod = AccessTools.Method(typeof(DeckManagerController),
                    "Coroutine_LoadDecks", new[] { typeof(int), typeof(Guid?) });
                if (coroutineMethod == null)
                {
                    // Fallback: just re-run SelectDeckBucket. Counts may
                    // still be stale if the server hasn't caught up.
                    RefreshDeckGrid();
                    return;
                }

                int bucketIndex = 0;
                if (_deckBucketDropdownField != null)
                {
                    var dropdown = _deckBucketDropdownField.GetValue(_controller) as TMP_Dropdown;
                    if (dropdown != null) bucketIndex = dropdown.value;
                }

                var enumerator = coroutineMethod.Invoke(
                    _controller, new object[] { bucketIndex, (Guid?)null }) as System.Collections.IEnumerator;
                if (enumerator != null) _controller.StartCoroutine(enumerator);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ReloadDecksLikeMTGADoes: {ex.Message}");
            }
        }

        /// <summary>
        /// Full deck-list refresh. Invokes MTGA's <c>SelectDeckBucket</c>
        /// — the same method MTGA itself uses when the format-filter
        /// dropdown changes — so all derived state (the deck-count text
        /// in the corner, the underlying <c>_decks</c> list, the sort
        /// pass, and our injected folder distribution via the SetDecks
        /// prefix) all stay in sync.
        ///
        /// Used after bulk delete / move / create-folder mutations. The
        /// earlier "call SetDecks only" path was lighter but left the
        /// "N decks" counter stale until the user navigated away.
        /// </summary>
        public static void RefreshDeckGrid()
        {
            try
            {
                if (_controller == null) return;

                var selectBucket = AccessTools.Method(typeof(DeckManagerController),
                    "SelectDeckBucket", new[] { typeof(int), typeof(Guid?) });
                if (selectBucket == null)
                {
                    Plugin.Log.LogWarning("RefreshDeckGrid: SelectDeckBucket not found");
                    return;
                }

                int bucketIndex = 0;
                if (_deckBucketDropdownField != null)
                {
                    var dropdown = _deckBucketDropdownField.GetValue(_controller) as TMP_Dropdown;
                    if (dropdown != null) bucketIndex = dropdown.value;
                }

                selectBucket.Invoke(_controller, new object[] { bucketIndex, (Guid?)null });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"RefreshDeckGrid: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------
        // Button construction + icon painting
        // -----------------------------------------------------------------
        private static GameObject BuildButton(GameObject sourceGO, Transform parent,
            string name, int siblingIndex, Action<GameObject> paintIcon, UnityAction onClick)
        {
            var clone = UnityEngine.Object.Instantiate(sourceGO, parent);
            clone.name = name;
            clone.transform.SetSiblingIndex(siblingIndex);
            // Strip every child component that paints something: text labels,
            // inner Image icons, and MTGA's tooltip widgets. The favorite
            // button has a DeluxeTooltip child carrying the "Favorite"
            // label — without removing it, every cloned button shows that
            // tooltip on hover.
            foreach (var tmp in clone.GetComponentsInChildren<TextMeshProUGUI>(true))
                UnityEngine.Object.Destroy(tmp);
            foreach (var img in clone.GetComponentsInChildren<Image>(true))
                if (img.gameObject != clone) UnityEngine.Object.Destroy(img);
            // Destroy any tooltip component or child tooltip GameObject.
            foreach (var comp in clone.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                var n = comp.GetType().Name;
                if (n.IndexOf("Tooltip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Localize", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    UnityEngine.Object.Destroy(comp);
                }
            }
            paintIcon(clone);
            WireOnClick(clone, onClick);
            return clone;
        }

        private static void WireOnClick(GameObject clone, UnityAction onClick)
        {
            foreach (var comp in clone.GetComponents<Component>())
            {
                if (comp == null) continue;
                var t = comp.GetType();
                if (t.Name.IndexOf("CustomButton", StringComparison.OrdinalIgnoreCase) < 0 &&
                    t.Name.IndexOf("CustomTouchButton", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // Silence every UnityEvent (OnClick + OnClickDown + …) — both
                // runtime and inspector-wired listeners — so nothing else
                // fires from this button's click stream.
                foreach (var prop in t.GetProperties())
                {
                    if (!typeof(UnityEventBase).IsAssignableFrom(prop.PropertyType)) continue;
                    var ev = prop.GetValue(comp) as UnityEventBase;
                    if (ev == null) continue;
                    int count = ev.GetPersistentEventCount();
                    for (int i = 0; i < count; i++)
                        ev.SetPersistentListenerState(i, UnityEventCallState.Off);
                    ev.RemoveAllListeners();
                }

                var onClickProp = AccessTools.Property(t, "OnClick") ?? AccessTools.Property(t, "onClick");
                var ev2 = onClickProp?.GetValue(comp) as UnityEvent;
                ev2?.AddListener(onClick);
                return;
            }
            var unityBtn = clone.GetComponent<Button>();
            if (unityBtn != null) { unityBtn.onClick.RemoveAllListeners(); unityBtn.onClick.AddListener(onClick); }
        }

        // ---- icon painters ----

        private static readonly Color IconGrey = new Color(0.78f, 0.80f, 0.85f, 1f);

        private static void PaintCheckIcon(GameObject clone)
        {
            var host = MakeIconHost(clone, "MTGAES_Check");
            if (TryPaintSprite(host.transform, "check", IconGrey)) return;
            PaintGlyph(host.transform, "✓", IconGrey, fontSize: 48);
        }

        private static void PaintCancelIcon(GameObject clone)
        {
            var host = MakeIconHost(clone, "MTGAES_Cancel");
            if (TryPaintSprite(host.transform, "cancel", IconGrey)) return;
            PaintGlyph(host.transform, "×", IconGrey, fontSize: 60);
        }

        /// <summary>
        /// Draws a Unicode glyph centered in <paramref name="parent"/>. Pulls
        /// a usable TMP font from the running scene via TmpFontHelper —
        /// without an explicit font, TMP-at-runtime silently renders nothing.
        /// Falls back to rectangle strokes if no font is available.
        /// </summary>
        private static void PaintGlyph(Transform parent, string glyph, Color color, int fontSize)
        {
            var go = new GameObject("Glyph");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            var font = TmpFontHelper.Get();
            if (font != null) tmp.font = font;
            tmp.text = glyph;
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            tmp.extraPadding = false;
        }

        private static void PaintTrashIcon(GameObject clone)
        {
            var host = MakeIconHost(clone, "MTGAES_Trash");
            if (TryPaintSprite(host.transform, "trash", IconGrey))
            {
                BuildTrashCountBubble(host);
                return;
            }
            // Lid (top thin bar)
            MakeRect(host.transform, "Lid", IconGrey, new Vector2(22f, 3f),
                pivot: new Vector2(0.5f, 0.5f), pos: new Vector2(0f, 10f));
            // Handle (small bump on top of lid)
            MakeRect(host.transform, "Handle", IconGrey, new Vector2(8f, 2f),
                pivot: new Vector2(0.5f, 0.5f), pos: new Vector2(0f, 13f));
            // Bin sides + bottom (drawn as a U using 3 rectangles)
            MakeRect(host.transform, "Side_L", IconGrey, new Vector2(2.5f, 16f),
                pivot: new Vector2(0.5f, 0.5f), pos: new Vector2(-7f, 0f));
            MakeRect(host.transform, "Side_R", IconGrey, new Vector2(2.5f, 16f),
                pivot: new Vector2(0.5f, 0.5f), pos: new Vector2(7f, 0f));
            MakeRect(host.transform, "Bin_Bottom", IconGrey, new Vector2(15f, 2.5f),
                pivot: new Vector2(0.5f, 0.5f), pos: new Vector2(0f, -8f));
            // (Optional) count bubble — refreshed in RefreshTrashCount
            BuildTrashCountBubble(host);
        }

        private static void BuildTrashCountBubble(GameObject host)
        {
            var bubble = new GameObject("MTGAES_CountBubble");
            bubble.transform.SetParent(host.transform, false);
            var rt = bubble.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            rt.anchoredPosition = new Vector2(-2f, -2f);
            rt.sizeDelta = new Vector2(18f, 14f);
            var bg = bubble.AddComponent<Image>();
            bg.color = new Color(0.85f, 0.25f, 0.25f, 1f); // red badge
            bg.raycastTarget = false;
            bubble.SetActive(false);
        }

        private static void RefreshTrashCount(GameObject deleteBtn)
        {
            if (deleteBtn == null) return;
            var host = deleteBtn.transform.Find("MTGAES_Trash");
            var bubble = host?.Find("MTGAES_CountBubble")?.gameObject;
            if (bubble == null) return;
            int n = DeckMultiSelectState.SelectionCount;
            bubble.SetActive(n > 0);
        }

        private static void PaintFolderIcon(GameObject clone)
        {
            var host = MakeIconHost(clone, "MTGAES_Folder");
            if (TryPaintSprite(host.transform, "folder", IconGrey)) return;
            // Folder body — main rectangle
            MakeRect(host.transform, "Body", IconGrey, new Vector2(26f, 18f),
                pivot: new Vector2(0.5f, 0.5f), pos: new Vector2(0f, -2f));
            // Tab — the little tab on top-left
            MakeRect(host.transform, "Tab", IconGrey, new Vector2(10f, 4f),
                pivot: new Vector2(0f, 0f), pos: new Vector2(-13f, 7f));
        }

        // Make Local (cloud -> local file): folder icon + a down arrow,
        // "pull down to your local folder".
        private static void PaintMakeLocalIcon(GameObject clone)
        {
            var host = MakeIconHost(clone, "MTGAES_MakeLocal");
            if (TryPaintSprite(host.transform, "makelocal", IconGrey)) return;
            PaintGlyph(host.transform, "↓", IconGrey, fontSize: 46);
        }

        // Make Cloud (local -> cloud): an up arrow, "push up to the cloud".
        private static void PaintMakeCloudIcon(GameObject clone)
        {
            var host = MakeIconHost(clone, "MTGAES_MakeCloud");
            if (TryPaintSprite(host.transform, "makecloud", IconGrey)) return;
            PaintGlyph(host.transform, "↑", IconGrey, fontSize: 46);
        }

        // Reuses the folder sprite (or vector folder) and overlays a small
        // "+" in the bottom-right corner so the New-Folder button is
        // visually distinct from the Move-to-Folder button at a glance.
        private static void PaintNewFolderIcon(GameObject clone)
        {
            PaintFolderIcon(clone);
            var host = clone.transform.Find("MTGAES_Folder");
            if (host == null) return;

            // Small "+" badge in the bottom-right corner. We use a TMP
            // glyph for crisp rendering at the small badge size; two
            // crossed rects would look chunky.
            var badge = new GameObject("MTGAES_PlusBadge");
            badge.transform.SetParent(host, false);
            var rt = badge.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot     = new Vector2(1f, 0f);
            rt.sizeDelta = new Vector2(18f, 18f);
            rt.anchoredPosition = new Vector2(2f, -2f);

            // Subtle dark backplate so the + reads against the folder body
            // regardless of what icon tint we're using.
            var bg = badge.AddComponent<Image>();
            bg.color = new Color(0.08f, 0.10f, 0.16f, 0.95f);
            bg.raycastTarget = false;

            PaintGlyph(badge.transform, "+", new Color(0.95f, 0.95f, 1f, 1f), fontSize: 26);
        }

        /// <summary>
        /// If a PNG asset exists at icons/&lt;name&gt;.png, paints it as an
        /// Image filling the parent and returns true. Otherwise returns false
        /// so the caller falls through to vector geometry / glyph.
        /// </summary>
        private static bool TryPaintSprite(Transform parent, string name, Color tint)
        {
            var sprite = IconLoader.Get(name);
            if (sprite == null) return false;

            var go = new GameObject("Sprite_" + name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(4, 4);   // small inset
            rt.offsetMax = new Vector2(-4, -4);
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = tint;
            img.preserveAspect = true;
            img.raycastTarget = false;
            return true;
        }

        // ---- shared geometry helpers ----

        private static GameObject MakeIconHost(GameObject clone, string name)
        {
            var host = new GameObject(name);
            host.transform.SetParent(clone.transform, false);
            var rt = host.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(40, 40);
            return host;
        }

        private static void BuildCheckStrokes(Transform parent, Color color,
            float shortLen, float longLen, float thickness, Vector2 tip)
        {
            const float Overshoot = 0.18f;
            var s1 = MakeStroke(parent, "Stroke_Short", color,
                size: new Vector2(shortLen, thickness),
                pivot: new Vector2(1f - Overshoot, 0.5f));
            s1.anchoredPosition = tip;
            s1.localRotation = Quaternion.Euler(0, 0, 225f);

            var s2 = MakeStroke(parent, "Stroke_Long", color,
                size: new Vector2(longLen, thickness),
                pivot: new Vector2(Overshoot, 0.5f));
            s2.anchoredPosition = tip;
            s2.localRotation = Quaternion.Euler(0, 0, 135f);
        }

        private static RectTransform MakeStroke(Transform parent, string name, Color color,
            Vector2 size, Vector2 pivot)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = pivot;
            rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return rt;
        }

        private static void MakeRect(Transform parent, string name, Color color,
            Vector2 size, Vector2 pivot, Vector2 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }
    }

    /// <summary>
    /// Mirrors how MTGA's own per-deck buttons (Edit, Delete, Clone, …)
    /// react to selection changes: <c>UpdateSelectedDeckView</c> fires
    /// every time the focused deck changes, with a null
    /// <see cref="DeckViewInfo"/> meaning "nothing is selected". We hide
    /// our single-deck Move button when nothing is focused so it never
    /// appears to do nothing.
    ///
    /// Lives in its own patch class so the <see cref="HarmonyPatch"/>
    /// attribute on <see cref="DeckManagerControllerPatch"/> stays anchored
    /// to <c>Awake</c>.
    /// </summary>
    [HarmonyPatch(typeof(DeckManagerController), "UpdateSelectedDeckView")]
    internal static class DeckManagerSelectionChangedPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Wizards.Mtga.Decks.DeckViewInfo deckViewInfo)
        {
            try { DeckManagerControllerPatch.OnSelectionFocusChanged(deckViewInfo); }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnSelectionFocusChanged: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Makes MTGA's native Delete button work for local decks. When a local
    /// deck is focused, <c>_selectedDeck</c> is null (the synthetic id isn't in
    /// MTGA's list), so the stock <c>Delete_OnClick</c> no-ops. We intercept,
    /// confirm, and delete the local file instead — so the same trash button
    /// the user already knows works for both cloud and local decks.
    /// </summary>
    [HarmonyPatch(typeof(DeckManagerController), "Delete_OnClick")]
    internal static class DeckManagerDeletePatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            try
            {
                var localId = Features.LocalDeckController.SelectedLocalDeckId;
                if (!localId.HasValue) return true; // cloud deck (or nothing) → native delete

                var local = Features.LocalDeckStore.Get(localId.Value);
                var name = local?.Name ?? "this deck";
                ConfirmDeleteModal.Show(1, () =>
                {
                    Features.LocalDeckStore.Delete(localId.Value);
                    Features.LocalDeckController.ClearSelection();
                    DeckViewSelectorPatch.RebuildLocalDecks();
                    Toast.Success($"Deleted local deck '{name}'");
                });
                return false; // skip the native (no-op) handler
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckManagerDeletePatch.Prefix: {ex.Message}");
                return true;
            }
        }
    }
}
