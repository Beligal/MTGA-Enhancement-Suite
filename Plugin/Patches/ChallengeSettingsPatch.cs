using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.Firebase;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using MTGAEnhancementSuite.Helpers;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Cleans up Firebase lobby when the challenge blade is disabled (host navigates away).
    /// Only handles Firebase cleanup — does NOT reset IsJoining/SelectedFormat since
    /// OnDisable fires during navigation transitions (old blade disabled before new one created).
    /// Full state reset happens in OnChallengeClosed handler.
    /// </summary>
    [HarmonyPatch(typeof(UnifiedChallengeBladeWidget), "OnDisable")]
    internal static class ChallengeBladeDisablePatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                // Don't cleanup during match transitions or active lobby sessions
                if (ChallengeFormatState.IsInMatch)
                {
                    Plugin.Log.LogInfo("OnDisable: skipping cleanup (in match)");
                    return;
                }

                if (ChallengeFormatState.IsJoining)
                {
                    Plugin.Log.LogInfo("OnDisable: skipping cleanup (joiner)");
                    return;
                }

                // If we're in a lobby session, this OnDisable is from a blade being
                // recycled (e.g., scene transition after match). Don't reset.
                // The lobby session only ends when the user actually navigates away,
                // which we detect by checking if a NEW blade is being created.
                if (ChallengeFormatState.InLobbySession)
                {
                    Plugin.Log.LogInfo("OnDisable: skipping cleanup (in lobby session)");
                    PerPlayerLog.Info("OnDisable: skipping cleanup (in lobby session)");
                    return;
                }

                var challengeId = ChallengeFormatState.ActiveChallengeId;
                if (challengeId == Guid.Empty) return;

                // Host is leaving the challenge view — cleanup Firebase
                Plugin.Log.LogInfo($"OnDisable: host leaving, deleting lobby {challengeId}");
                FirebaseClient.Instance.StopHeartbeat();
                FirebaseClient.Instance.DeleteLobby(challengeId.ToString());
                FirebaseSseListener.Instance?.Dispose();

                ChallengeFormatState.Reset();
                ChallengeSettingsPatch._registeredChallengeId = Guid.Empty;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ChallengeBladeDisablePatch failed: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(UnifiedChallengeBladeWidget), "OnEnable")]
    internal static class ChallengeSettingsPatch
    {
        private const string FormatSpinnerName = "MTGAES_FormatSpinner";
        private const string CopyLinkBtnName = "MTGAES_CopyLink";
        private const string MakePublicBtnName = "MTGAES_MakePublic";

        // Keep a reference to the current spinner for SSE updates
        private static WeakReference _currentSpinnerRef;
        private static WeakReference _currentWidgetRef;

        [HarmonyPostfix]
        static void Postfix(UnifiedChallengeBladeWidget __instance)
        {
            try
            {
                // Only reset joining state on a truly fresh challenge screen.
                // If ActiveChallengeId is set, we're returning from a match — preserve state.
                if (!ChallengeFormatState.HasPendingJoin && ChallengeFormatState.ActiveChallengeId == Guid.Empty)
                {
                    ChallengeFormatState.IsJoining = false;
                }

                // Returning from a match — clear the flag but preserve lobby state
                bool returningFromMatch = ChallengeFormatState.IsInMatch;
                if (returningFromMatch)
                {
                    ChallengeFormatState.IsInMatch = false;
                    ChallengeFormatState.LastMatchEndTime = UnityEngine.Time.unscaledTime;
                    Plugin.Log.LogInfo($"Returned from match, restoring lobby state (format={ChallengeFormatState.SelectedFormat}, joining={ChallengeFormatState.IsJoining})");
                    PerPlayerLog.Info($"Returned from match, format={ChallengeFormatState.SelectedFormat}, joining={ChallengeFormatState.IsJoining}");

                    // Resume heartbeat for host
                    if (!ChallengeFormatState.IsJoining && ChallengeFormatState.ActiveChallengeId != Guid.Empty)
                    {
                        FirebaseClient.Instance.StartHeartbeat(ChallengeFormatState.ActiveChallengeId.ToString());
                    }

                    // Joiner: refresh auth token and restart SSE listener
                    if (ChallengeFormatState.IsJoining && ChallengeFormatState.ActiveChallengeId != Guid.Empty)
                    {
                        Plugin.Log.LogInfo("Post-match: refreshing auth and restarting SSE for joiner");
                        FirebaseClient.Instance.RefreshTokenIfNeeded(() =>
                        {
                            Plugin.Log.LogInfo("Post-match: token refreshed, SSE will restart via InjectFormatSpinner");
                        });
                    }
                }

                InjectFormatSpinner(__instance);
                InjectButtons(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ChallengeSettingsPatch failed: {ex}");
            }
        }

        private static void InjectFormatSpinner(UnifiedChallengeBladeWidget widget)
        {
            // Always try to capture the ChallengeId from the widget.
            // During OnEnable the widget's CurrentChallengeId may not be set yet
            // (it's set asynchronously via OnChallengeDataChanged). If we're in a
            // lobby session, start a coroutine to poll for it.
            if (ChallengeFormatState.ActiveChallengeId == Guid.Empty)
            {
                try
                {
                    var cidField = AccessTools.Field(typeof(UnifiedChallengeBladeWidget), "CurrentChallengeId")
                        ?? AccessTools.Field(typeof(UnifiedChallengeBladeWidget).BaseType, "CurrentChallengeId");
                    if (cidField != null)
                    {
                        var cid = (Guid)cidField.GetValue(widget);
                        if (cid != Guid.Empty)
                        {
                            ChallengeFormatState.ActiveChallengeId = cid;
                            Plugin.Log.LogInfo($"Captured ActiveChallengeId from widget: {cid}");
                            PerPlayerLog.Info($"Captured ActiveChallengeId from widget: {cid}");

                            // Re-register lobby in Firebase with the new challenge ID
                            if (ChallengeFormatState.InLobbySession && !ChallengeFormatState.IsJoining &&
                                ChallengeFormatState.HasFormat && FirebaseClient.Instance.IsAuthenticated)
                            {
                                RegisterLobbyIfNeeded(cid);
                            }
                        }
                        else if (ChallengeFormatState.InLobbySession)
                        {
                            // Widget doesn't have the ID yet — poll for it
                            FirebaseClient.Instance.StartCoroutine(PollForChallengeId(widget, cidField));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Failed to capture ChallengeId from widget: {ex.Message}");
                }
            }

            var spinnerField = AccessTools.Field(typeof(UnifiedChallengeBladeWidget), "_startingPlayerSpinner");
            if (spinnerField == null) return;

            var startingPlayerSpinner = spinnerField.GetValue(widget) as Spinner_OptionSelector;
            if (startingPlayerSpinner == null) return;

            var spinnerGO = startingPlayerSpinner.gameObject;
            var parent = spinnerGO.transform.parent;

            Spinner_OptionSelector spinner;
            var existing = parent.Find(FormatSpinnerName);
            if (existing != null)
            {
                // Spinner already exists — just update its state
                spinner = existing.GetComponent<Spinner_OptionSelector>();
                if (spinner != null)
                {
                    ApplyFormatSpinnerState(spinner, widget);
                }
                return;
            }

            var clone = UnityEngine.Object.Instantiate(spinnerGO, parent);
            clone.name = FormatSpinnerName;
            clone.SetActive(true);

            var sourceRect = spinnerGO.GetComponent<RectTransform>();
            var cloneRect = clone.GetComponent<RectTransform>();
            float newY = sourceRect.anchoredPosition.y - sourceRect.sizeDelta.y;
            cloneRect.anchoredPosition = new Vector2(sourceRect.anchoredPosition.x, newY);

            // Hide ALL spinner visuals — the clone is now purely a hidden state holder
            // (we still call its SelectOption / ValueIndex to drive selection). The
            // visible UI is a fresh combobox sibling below.
            var animator = clone.GetComponent<Animator>();
            if (animator != null) animator.enabled = false;
            for (int i = 0; i < clone.transform.childCount; i++)
            {
                clone.transform.GetChild(i).gameObject.SetActive(false);
            }

            foreach (var loc in clone.GetComponentsInChildren<Wotc.Mtga.Loc.Localize>(true))
                UnityEngine.Object.Destroy(loc);

            spinner = clone.GetComponent<Spinner_OptionSelector>();
            if (spinner == null) return;

            spinner.onValueChanged.RemoveAllListeners();
            spinner.ClearOptions();
            spinner.AddOptions(new List<string>(ChallengeFormatState.FormatOptions));

            // Build a fresh inline combobox in the same parent at the spinner's
            // position. The combobox calls spinner.SelectOption(idx) so the
            // existing onValueChanged path drives all format-change side effects.
            UI.FormatComboBox.Mount(parent, cloneRect, spinner);

            // Wire the underlying spinner's onValueChanged: this is THE
            // canonical "format selection changed" path. The combobox triggers
            // it via spinner.SelectOption(idx).
            var widgetRef = widget;
            spinner.onValueChanged.AddListener(new UnityAction<int, string>((index, value) =>
            {
                if (index < 0 || index >= ChallengeFormatState.FormatKeys.Length) return;

                // Always keep the visible combobox in sync with the selection,
                // including for joiner SSE updates and post-match restores.
                UI.FormatComboBox.SyncDisplay(index);

                if (ChallengeFormatState.IsJoining) return;

                var newFormat = ChallengeFormatState.FormatKeys[index];
                // Detect "did the user actually change modes?" vs "we're just
                // re-selecting the existing one as part of UI restore". The
                // listener fires on every SelectOption call (including
                // programmatic ones). Only the user-driven case should
                // re-apply the mode's defaults (deck type, Bo3-default).
                bool isRealChange = !string.Equals(newFormat, ChallengeFormatState.SelectedFormat,
                    StringComparison.OrdinalIgnoreCase);
                ChallengeFormatState.SelectedFormat = newFormat;

                Plugin.Log.LogInfo($"Format selected: {newFormat} (realChange={isRealChange})");
                PerPlayerLog.Info($"Format selected: {newFormat} (ActiveChallengeId={ChallengeFormatState.ActiveChallengeId}, realChange={isRealChange}, IsJoining={ChallengeFormatState.IsJoining})");

                if (isRealChange && ChallengeFormatState.HasFormat)
                {
                    try { ClearSelectedDeck(widgetRef); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"Could not clear deck: {ex.Message}"); }
                }

                if (!ChallengeFormatState.IsJoining && ChallengeFormatState.ActiveChallengeId != Guid.Empty)
                {
                    FirebaseClient.Instance.UpdateLobbyFormat(
                        ChallengeFormatState.ActiveChallengeId.ToString(),
                        ChallengeFormatState.SelectedFormat);
                    Plugin.Log.LogInfo($"Pushed format change to Firebase: {ChallengeFormatState.SelectedFormat}");
                    PerPlayerLog.Info($"Pushed format change to Firebase: {ChallengeFormatState.SelectedFormat}");
                }

                // Apply the mode's defaults (matchType + Bo3-default) on real
                // user-driven changes regardless of whether a challenge has
                // been created yet — PushMatchType drives MTGA's UI widgets
                // directly when no challenge ID exists, and only commits to
                // SetGameSettings + Firebase when one does. Skip on programmatic
                // restores (would clobber a user's manual Bo1-on-a-Bo3-mode toggle).
                if (!ChallengeFormatState.IsJoining && isRealChange)
                    PushMatchTypeForCurrentMode();

                UpdateButtonVisibility(parent);
            }));

            ApplyFormatSpinnerState(spinner, widget);

            Plugin.Log.LogInfo("Format dropdown injected (spinner repurposed as button)");
        }

        /// <summary>Hides the prev/next arrows on a cloned Spinner_OptionSelector.</summary>
        private static void HideSpinnerArrows(Transform spinnerRoot)
        {
            foreach (var btn in spinnerRoot.GetComponentsInChildren<Button>(true))
            {
                var n = btn.gameObject.name.ToLower();
                if (n.Contains("next") || n.Contains("previous") || n.Contains("prev") ||
                    n.Contains("arrow") || n.Contains("left") || n.Contains("right"))
                {
                    btn.gameObject.SetActive(false);
                }
            }
        }

        /// <summary>
        /// Adds a full-area Button on the spinner root so clicking anywhere on
        /// the spinner opens the GameModePicker. Also appends a small ▾ glyph
        /// to the value label so users see this is a dropdown.
        /// </summary>
        private static void ConvertSpinnerToDropdownButton(GameObject spinnerRoot,
            Spinner_OptionSelector spinner, Transform parent, UnifiedChallengeBladeWidget widget)
        {
            // Add an Image (transparent, raycast-only) sitting over the whole spinner
            var hitObj = new GameObject("MTGAES_DropdownHit");
            hitObj.transform.SetParent(spinnerRoot.transform, false);
            var hitRect = hitObj.AddComponent<RectTransform>();
            hitRect.anchorMin = Vector2.zero;
            hitRect.anchorMax = Vector2.one;
            hitRect.sizeDelta = Vector2.zero;
            hitRect.SetAsLastSibling();

            var hitImg = hitObj.AddComponent<Image>();
            hitImg.color = new Color(0f, 0f, 0f, 0f); // transparent
            hitImg.raycastTarget = true;

            var btnComp = hitObj.AddComponent<Button>();
            btnComp.transition = Selectable.Transition.None;
            btnComp.onClick.AddListener(new UnityAction(() =>
            {
                if (ChallengeFormatState.IsJoining)
                {
                    UI.Toast.Info("Format is locked when joining a lobby.");
                    return;
                }
                var spinnerRef = spinner;
                UI.GameModePicker.Open(ChallengeFormatState.SelectedFormat, mode =>
                {
                    if (mode == null) return;
                    int idx = Array.IndexOf(ChallengeFormatState.FormatKeys, mode.Id);
                    if (idx >= 0 && spinnerRef != null) spinnerRef.SelectOption(idx);
                });
            }));

            // Append a small ▾ next to the value label (or wherever the label is)
            // by adding a child to the spinner with right-aligned text.
            var caretObj = new GameObject("MTGAES_DropdownCaret");
            caretObj.transform.SetParent(spinnerRoot.transform, false);
            var caretRect = caretObj.AddComponent<RectTransform>();
            caretRect.anchorMin = new Vector2(0.85f, 0f);
            caretRect.anchorMax = new Vector2(0.98f, 1f);
            caretRect.sizeDelta = Vector2.zero;
            var caretTmp = caretObj.AddComponent<TMPro.TextMeshProUGUI>();
            caretTmp.text = "▾";
            caretTmp.fontSize = 22;
            caretTmp.color = new Color(0.7f, 0.7f, 0.85f);
            caretTmp.alignment = TMPro.TextAlignmentOptions.MidlineRight;
            caretTmp.raycastTarget = false;
        }

        private const string FormatSearchBtnName = "MTGAES_FormatSearchBtn"; // legacy, kept for compat

        // (Legacy path no longer used — kept temporarily so existing callers compile.)
        private static void InjectSearchButton(Transform parent, RectTransform spinnerRect,
            UnifiedChallengeBladeWidget widget, Spinner_OptionSelector spinner)
        {
            // Don't double-inject
            if (parent.Find(FormatSearchBtnName) != null) return;

            var btn = new GameObject(FormatSearchBtnName);
            btn.transform.SetParent(parent, false);

            var rect = btn.AddComponent<RectTransform>();
            // Place to the right of the spinner, narrow strip
            rect.anchorMin = new Vector2(0.86f, 1f);
            rect.anchorMax = new Vector2(0.99f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, spinnerRect.anchoredPosition.y - 4);
            rect.sizeDelta = new Vector2(0f, spinnerRect.sizeDelta.y * 0.7f);

            // Canvas override so we render on top of MTGA's UI
            var canvas = btn.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 51;
            btn.AddComponent<GraphicRaycaster>();

            btn.AddComponent<Image>().color = new Color(0.2f, 0.3f, 0.5f, 0.9f);
            var button = btn.AddComponent<Button>();

            var labelObj = new GameObject("Text");
            labelObj.transform.SetParent(btn.transform, false);
            var labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.sizeDelta = Vector2.zero;
            var tmp = labelObj.AddComponent<TMPro.TextMeshProUGUI>();
            tmp.text = "Search…";
            tmp.fontSize = 13;
            tmp.color = Color.white;
            tmp.alignment = TMPro.TextAlignmentOptions.Center;

            var widgetRef = widget;
            var spinnerRef = spinner;
            button.onClick.AddListener(new UnityAction(() =>
            {
                if (ChallengeFormatState.IsJoining)
                {
                    UI.Toast.Info("Format is locked when joining a lobby.");
                    return;
                }
                UI.GameModePicker.Open(ChallengeFormatState.SelectedFormat, mode =>
                {
                    if (mode == null) return;
                    int idx = Array.IndexOf(ChallengeFormatState.FormatKeys, mode.Id);
                    if (idx >= 0 && spinnerRef != null) spinnerRef.SelectOption(idx);
                });
            }));
        }

        private static void ApplyFormatSpinnerState(Spinner_OptionSelector spinner, UnifiedChallengeBladeWidget widget)
        {
            // Lock the combobox if joining — joiners see the host's format read-only.
            UI.FormatComboBox.SetLocked(ChallengeFormatState.IsJoining);

            if (ChallengeFormatState.IsJoining)
            {
                int formatIndex = Array.IndexOf(ChallengeFormatState.FormatKeys, ChallengeFormatState.SelectedFormat);
                if (formatIndex < 0) formatIndex = 0;
                spinner.SelectOption(formatIndex);
                UI.FormatComboBox.SyncDisplay(formatIndex);

                Plugin.Log.LogInfo($"Format dropdown locked to {ChallengeFormatState.SelectedFormat} (joining)");

                FirebaseClient.Instance.RefreshTokenIfNeeded(() =>
                {
                    if (!FirebaseClient.Instance.IsAuthenticated)
                    {
                        Plugin.Log.LogWarning("Cannot start format sync — auth failed");
                        return;
                    }
                    FetchCurrentLobbyFormat(spinner, widget);
                    StartSseListener(spinner, widget);
                });
            }
            else
            {
                if (ChallengeFormatState.HasFormat)
                {
                    int idx = Array.IndexOf(ChallengeFormatState.FormatKeys, ChallengeFormatState.SelectedFormat);
                    if (idx >= 0)
                    {
                        spinner.SelectOption(idx);
                        UI.FormatComboBox.SyncDisplay(idx);
                    }
                    Plugin.Log.LogInfo($"Format dropdown restored to {ChallengeFormatState.SelectedFormat}");
                }
                else
                {
                    spinner.SelectOption(0);
                    UI.FormatComboBox.SyncDisplay(0);
                    ChallengeFormatState.SelectedFormat = "none";
                }
            }

            _currentSpinnerRef = new WeakReference(spinner);
            _currentWidgetRef = new WeakReference(widget);
        }

        private static void InjectButtons(UnifiedChallengeBladeWidget widget)
        {
            var spinnerField = AccessTools.Field(typeof(UnifiedChallengeBladeWidget), "_startingPlayerSpinner");
            if (spinnerField == null) return;

            var startingPlayerSpinner = spinnerField.GetValue(widget) as Spinner_OptionSelector;
            if (startingPlayerSpinner == null) return;

            var parent = startingPlayerSpinner.transform.parent;

            // Don't add buttons if joining (not the host)
            if (ChallengeFormatState.IsJoining) return;

            if (parent.Find(CopyLinkBtnName) != null) return;

            var sourceRect = startingPlayerSpinner.GetComponent<RectTransform>();
            float spinnerHeight = sourceRect.sizeDelta.y; // 53
            float formatY = sourceRect.anchoredPosition.y - spinnerHeight; // -219
            // The new FormatComboBox row reads slightly taller than the
            // original spinner (different text metrics + the inline caret),
            // so the original 10px gap let the Copy Link / Make Public
            // buttons clip the bottom of the format row. Bump to 30px.
            float buttonsY = formatY - spinnerHeight - 30f; // -302

            // Copy Link button (left half)
            var copyLinkObj = CreateSimpleButton(parent, CopyLinkBtnName, "Copy Link",
                sourceRect.anchoredPosition.x, buttonsY,
                sourceRect.sizeDelta.x > 0 ? sourceRect.sizeDelta.x / 2 - 5 : -5f, 40f,
                new Color(0.2f, 0.3f, 0.5f, 0.9f));

            // Position: left half
            var copyRect = copyLinkObj.GetComponent<RectTransform>();
            copyRect.anchorMin = new Vector2(0f, 1f);
            copyRect.anchorMax = new Vector2(0.48f, 1f);
            copyRect.anchoredPosition = new Vector2(0f, buttonsY);
            copyRect.sizeDelta = new Vector2(0f, 40f);

            var widgetRef = widget;
            copyLinkObj.GetComponent<Button>().onClick.AddListener(new UnityAction(() =>
            {
                CopyLinkClicked(widgetRef);
            }));

            // Make Public button (right half)
            var publicObj = CreateSimpleButton(parent, MakePublicBtnName, "Make Public",
                0, buttonsY, 0, 40f,
                new Color(0.3f, 0.45f, 0.2f, 0.9f));

            var publicRect = publicObj.GetComponent<RectTransform>();
            publicRect.anchorMin = new Vector2(0.52f, 1f);
            publicRect.anchorMax = new Vector2(1f, 1f);
            publicRect.anchoredPosition = new Vector2(0f, buttonsY);
            publicRect.sizeDelta = new Vector2(0f, 40f);

            publicObj.GetComponent<Button>().onClick.AddListener(new UnityAction(() =>
            {
                MakePublicClicked(widgetRef);
            }));

            // Initially hidden until format is selected
            UpdateButtonVisibility(parent);
        }

        private static void UpdateButtonVisibility(Transform parent)
        {
            var copyLink = parent.Find(CopyLinkBtnName);
            var makePublic = parent.Find(MakePublicBtnName);

            bool show = ChallengeFormatState.HasFormat && !ChallengeFormatState.IsJoining;

            if (copyLink != null) copyLink.gameObject.SetActive(show);
            if (makePublic != null) makePublic.gameObject.SetActive(show);
        }

        private static void CopyLinkClicked(UnifiedChallengeBladeWidget widget)
        {
            try
            {
                var challengeIdProp = AccessTools.Field(typeof(UnifiedChallengeBladeWidget), "CurrentChallengeId")
                    ?? AccessTools.Field(typeof(UnifiedChallengeBladeWidget).BaseType, "CurrentChallengeId");

                if (challengeIdProp == null)
                {
                    Toast.Error("Could not get challenge ID");
                    return;
                }

                var challengeId = (Guid)challengeIdProp.GetValue(widget);
                if (challengeId == Guid.Empty)
                {
                    Toast.Warning("No active challenge to share");
                    return;
                }

                // Ensure ActiveChallengeId is set so format pushes work
                ChallengeFormatState.ActiveChallengeId = challengeId;
                ChallengeFormatState.InLobbySession = true;
                PerPlayerLog.Info($"CopyLink: set ActiveChallengeId={challengeId}, InLobbySession=true");

                // Register lobby in Firebase (private) if not already registered
                if (FirebaseClient.Instance.IsAuthenticated)
                {
                    RegisterLobbyIfNeeded(challengeId);
                }

                var link = $"https://mtga-enhancement-suite.web.app/join/{challengeId}/{ChallengeFormatState.SelectedFormat}";
                GUIUtility.systemCopyBuffer = link;
                Toast.Success("Invite link copied!");
                Plugin.Log.LogInfo($"Copied link: {link}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CopyLink failed: {ex}");
                Toast.Error("Failed to copy link");
            }
        }

        private static void MakePublicClicked(UnifiedChallengeBladeWidget widget)
        {
            try
            {
                var challengeIdProp = AccessTools.Field(typeof(UnifiedChallengeBladeWidget), "CurrentChallengeId")
                    ?? AccessTools.Field(typeof(UnifiedChallengeBladeWidget).BaseType, "CurrentChallengeId");

                if (challengeIdProp == null)
                {
                    Toast.Error("Could not get challenge ID");
                    return;
                }

                var challengeId = (Guid)challengeIdProp.GetValue(widget);
                if (challengeId == Guid.Empty)
                {
                    Toast.Warning("No active challenge");
                    return;
                }

                // Get player info
                var pantryType = GameReflection.TypeByName("Pantry");
                var accountClientType = GameReflection.TypeByName("IAccountClient");
                var getMethod = pantryType.GetMethod("Get").MakeGenericMethod(accountClientType);
                var accountClient = getMethod.Invoke(null, null);
                var accountInfoProp = AccessTools.Property(accountClientType, "AccountInformation");
                var accountInfo = accountInfoProp.GetValue(accountClient);

                var personaId = AccessTools.Field(accountInfo.GetType(), "PersonaID").GetValue(accountInfo) as string;
                var displayName = AccessTools.Field(accountInfo.GetType(), "DisplayName").GetValue(accountInfo) as string;

                ChallengeFormatState.ActiveChallengeId = challengeId;
                ChallengeFormatState.InLobbySession = true;
                var challengeIdStr = challengeId.ToString();
                Plugin.Log.LogInfo($"MakePublic: challengeId={challengeIdStr}, format={ChallengeFormatState.SelectedFormat}");

                // Register the lobby (full PUT) if it doesn't exist yet, then start heartbeat.
                // ChallengeCreatePatch may or may not have created it depending on timing.
                // Read Bo3 state from the game's bestOf spinner
                bool isBo3 = ChallengeFormatState.IsBestOf3;
                try
                {
                    var bestOfField = AccessTools.Field(typeof(UnifiedChallengeBladeWidget), "_bestOfSpinner");
                    if (bestOfField != null)
                    {
                        var bestOfSpinner = bestOfField.GetValue(widget) as Spinner_OptionSelector;
                        if (bestOfSpinner != null)
                        {
                            isBo3 = bestOfSpinner.ValueIndex == 1;
                            ChallengeFormatState.IsBestOf3 = isBo3;
                        }
                    }
                }
                catch (Exception ex2)
                {
                    Plugin.Log.LogWarning($"Could not read bestOf spinner: {ex2.Message}");
                }

                FirebaseClient.Instance.RegisterLobby(
                    challengeIdStr,
                    ChallengeFormatState.SelectedFormat,
                    displayName,
                    personaId,
                    "DirectGame",
                    true, // isPublic
                    success =>
                    {
                        if (success)
                        {
                            ChallengeFormatState.IsLobbyPublic = true;
                            _registeredChallengeId = challengeId;
                            FirebaseClient.Instance.StartHeartbeat(challengeIdStr);
                            StartHostSseListener();
                            Toast.Success("Lobby is now public!");
                            Plugin.Log.LogInfo($"MakePublic succeeded for {challengeIdStr}");
                        }
                        else
                        {
                            Toast.Error("Failed to make lobby public");
                            Plugin.Log.LogError($"MakePublic failed for {challengeIdStr}");
                        }
                    },
                    isBo3
                );
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"MakePublic failed: {ex}");
                Toast.Error("Failed to make lobby public");
            }
        }

        /// <summary>
        /// Tracks which challenge ID currently has a Firebase lobby record.
        /// <see cref="Guid.Empty"/> means no lobby is currently registered.
        ///
        /// Keyed on the ID rather than a bool flag so we self-heal: if the
        /// state ever drifts (e.g. <see cref="OnChallengeClosed"/> clears
        /// <see cref="ChallengeFormatState.ActiveChallengeId"/> but a stale
        /// "registered" flag carries over), the next call sees a mismatch
        /// and re-registers. The old bool-flag version had the opposite
        /// failure mode — after a post-match challenge recycle it would
        /// short-circuit registration of the new lobby, leaving "Request
        /// Invite" from the web link with no Firebase record to write to.
        /// </summary>
        internal static Guid _registeredChallengeId = Guid.Empty;

        private static void RegisterLobbyIfNeeded(Guid challengeId)
        {
            // Don't re-register if already registered for THIS specific
            // challenge ID. Re-registering would overwrite public state.
            if (_registeredChallengeId == challengeId)
            {
                PerPlayerLog.Info($"Lobby {challengeId} already registered, skipping re-registration");
                return;
            }

            try
            {
                var pantryType = GameReflection.TypeByName("Pantry");
                var accountClientType = GameReflection.TypeByName("IAccountClient");
                if (pantryType == null || accountClientType == null) return;

                var getMethod = pantryType.GetMethod("Get").MakeGenericMethod(accountClientType);
                var accountClient = getMethod.Invoke(null, null);
                var accountInfoProp = AccessTools.Property(accountClientType, "AccountInformation");
                var accountInfo = accountInfoProp.GetValue(accountClient);

                var playerId = AccessTools.Field(accountInfo.GetType(), "PersonaID").GetValue(accountInfo) as string;
                var playerName = AccessTools.Field(accountInfo.GetType(), "DisplayName").GetValue(accountInfo) as string;

                FirebaseClient.Instance.RegisterLobby(
                    challengeId.ToString(),
                    ChallengeFormatState.SelectedFormat,
                    playerName, playerId, "DirectGame",
                    false, // private by default
                    success =>
                    {
                        if (success)
                        {
                            _registeredChallengeId = challengeId;
                            PerPlayerLog.Info($"Lobby {challengeId} registered (private) for format sync");
                            FirebaseClient.Instance.StartHeartbeat(challengeId.ToString());
                            StartHostSseListener();
                        }
                        else
                        {
                            PerPlayerLog.Warning($"Failed to register lobby {challengeId}");
                        }
                    },
                    ChallengeFormatState.IsBestOf3
                );
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"RegisterLobbyIfNeeded failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies the active game mode's match settings (deck type, Bo3-default)
        /// in two layers:
        ///
        ///   (1) Always: drive MTGA's UI widgets directly via the widget's
        ///       private SetChallengeOption_ChallengeType / _BestOfSettings
        ///       methods. This works even before any challenge has been
        ///       created, so the user sees the deck type and Bo toggle flip
        ///       immediately when they pick a mode in the format dropdown.
        ///
        ///   (2) When a challenge exists: also call
        ///       PVPChallengeController.SetGameSettings(...) to commit the
        ///       same values to MTGA's challenge state, and mirror isBestOf3
        ///       to Firebase so joiners see it via SSE.
        /// </summary>
        internal static void PushMatchTypeForCurrentMode()
        {
            try
            {
                var gameMode = ChallengeFormatState.GetGameMode(ChallengeFormatState.SelectedFormat);
                if (gameMode == null || string.IsNullOrEmpty(gameMode.MatchType)) return;

                // ChallengeMatchTypes enum lives on UnifiedChallengeBladeWidget's
                // SetChallengeOption_ChallengeType signature. Find it.
                var widget = (_currentWidgetRef != null && _currentWidgetRef.IsAlive)
                    ? _currentWidgetRef.Target as UnifiedChallengeBladeWidget : null;
                if (widget == null) widget = UnityEngine.Object.FindObjectOfType<UnifiedChallengeBladeWidget>();

                Type matchTypeEnum = null;
                if (widget != null)
                {
                    var setChalType = AccessTools.Method(typeof(UnifiedChallengeBladeWidget),
                        "SetChallengeOption_ChallengeType");
                    if (setChalType != null)
                    {
                        var p = setChalType.GetParameters();
                        if (p.Length > 0) matchTypeEnum = p[0].ParameterType;
                    }
                }
                // Fallback: pull the enum from the controller's SetGameSettings signature.
                if (matchTypeEnum == null && ChallengeCreatePatch.CachedControllerType != null)
                {
                    var sgs = AccessTools.Method(ChallengeCreatePatch.CachedControllerType, "SetGameSettings");
                    if (sgs != null && sgs.GetParameters().Length >= 2)
                        matchTypeEnum = sgs.GetParameters()[1].ParameterType;
                }
                if (matchTypeEnum == null || !matchTypeEnum.IsEnum)
                {
                    PerPlayerLog.Warning("PushMatchType: ChallengeMatchTypes enum not found");
                    return;
                }

                object matchTypeValue;
                try { matchTypeValue = Enum.Parse(matchTypeEnum, gameMode.MatchType); }
                catch
                {
                    PerPlayerLog.Warning($"PushMatchType: unknown MatchType '{gameMode.MatchType}', defaulting to DirectGame");
                    matchTypeValue = Enum.Parse(matchTypeEnum, "DirectGame");
                }

                bool desiredBestOf3 = gameMode.IsBestOf3Default;
                ChallengeFormatState.IsBestOf3 = desiredBestOf3;

                // ---- Layer 1: drive the widget's UI directly ----
                // SetChallengeOption_ChallengeType updates the deck-type +
                // challenge-type spinners (60-card vs 60-card-rebalanced etc.).
                // SetChallengeOption_BestOfSettings flips the bo toggle.
                // Both are private methods on UnifiedChallengeBladeWidget.
                //
                // We invoke twice — once now and once after a frame — because
                // the very first invocation often races MTGA's own widget
                // initialization (animator state still settling, listeners
                // being re-bound). The second call after WaitForEndOfFrame
                // lands after MTGA's frame has finished and reliably sticks.
                if (widget != null)
                {
                    ApplyMatchTypeToWidget(widget, matchTypeValue, desiredBestOf3);
                    FirebaseClient.Instance.StartCoroutine(
                        ReapplyMatchTypeNextFrame(widget, matchTypeValue, desiredBestOf3,
                            $"{gameMode.MatchType} + Bo{(desiredBestOf3 ? "3" : "1")}"));
                }
                else
                {
                    PerPlayerLog.Info("PushMatchType: no live widget — skipped UI update");
                }

                // ---- Layer 2: commit to MTGA's challenge state (only if it exists) ----
                if (ChallengeFormatState.ActiveChallengeId == Guid.Empty)
                {
                    PerPlayerLog.Info("PushMatchType: no active challenge yet — UI updated only");
                    return;
                }

                var controller = ChallengeCreatePatch.CachedController;
                var controllerType = ChallengeCreatePatch.CachedControllerType;
                if (controller == null || controllerType == null)
                {
                    PerPlayerLog.Warning("PushMatchType: no cached PVPChallengeController");
                    return;
                }

                var setGameSettings = AccessTools.Method(controllerType, "SetGameSettings");
                if (setGameSettings == null)
                {
                    PerPlayerLog.Warning("PushMatchType: SetGameSettings not found");
                    return;
                }

                var paramTypes = setGameSettings.GetParameters();
                var whoPlaysFirstEnum = paramTypes.Length >= 3 ? paramTypes[2].ParameterType : null;
                object whoPlaysFirstValue = whoPlaysFirstEnum != null
                    ? Enum.GetValues(whoPlaysFirstEnum).GetValue(0)
                    : null;

                setGameSettings.Invoke(controller, new[]
                {
                    (object)ChallengeFormatState.ActiveChallengeId,
                    matchTypeValue,
                    whoPlaysFirstValue,
                    (object)desiredBestOf3,
                });

                // Mirror to Firebase so the lobby record + joiners see the new bo
                try
                {
                    FirebaseClient.Instance.UpdateLobbyBestOf(
                        ChallengeFormatState.ActiveChallengeId.ToString(), desiredBestOf3);
                }
                catch (Exception fbEx)
                {
                    Plugin.Log.LogWarning($"PushMatchType: UpdateLobbyBestOf failed: {fbEx.Message}");
                }

                PerPlayerLog.Info($"PushMatchType: committed to challenge {ChallengeFormatState.ActiveChallengeId}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"PushMatchType failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Calls UnifiedChallengeBladeWidget.SetChallengeOption_ChallengeType +
        /// SetChallengeOption_BestOfSettings via reflection. Pulled out so we
        /// can invoke it both immediately and again next-frame.
        /// </summary>
        private static void ApplyMatchTypeToWidget(UnifiedChallengeBladeWidget widget,
            object matchTypeValue, bool desiredBestOf3)
        {
            if (widget == null) return;
            var setChalType = AccessTools.Method(typeof(UnifiedChallengeBladeWidget),
                "SetChallengeOption_ChallengeType");
            var setBestOf = AccessTools.Method(typeof(UnifiedChallengeBladeWidget),
                "SetChallengeOption_BestOfSettings");
            if (setChalType != null)
            {
                try { setChalType.Invoke(widget, new[] { matchTypeValue }); }
                catch (Exception ex) { Plugin.Log.LogWarning($"SetChallengeOption_ChallengeType failed: {ex.Message}"); }
            }
            if (setBestOf != null)
            {
                try { setBestOf.Invoke(widget, new object[] { desiredBestOf3 }); }
                catch (Exception ex) { Plugin.Log.LogWarning($"SetChallengeOption_BestOfSettings failed: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Re-applies the match-type and best-of after one frame. The very
        /// first invocation can race MTGA's own widget initialization
        /// (animator transitions, OnEnable's SetSpinnersFromChallenge), so a
        /// second pass after WaitForEndOfFrame ensures the values stick.
        /// </summary>
        private static IEnumerator ReapplyMatchTypeNextFrame(
            UnifiedChallengeBladeWidget widget, object matchTypeValue,
            bool desiredBestOf3, string label)
        {
            yield return new WaitForEndOfFrame();
            if (widget == null) yield break;
            ApplyMatchTypeToWidget(widget, matchTypeValue, desiredBestOf3);
            PerPlayerLog.Info($"PushMatchType: UI re-applied next frame ({label})");
        }

        private static void ClearSelectedDeck(UnifiedChallengeBladeWidget widget)
        {
            try
            {
                var setDeckMethod = AccessTools.Method(typeof(UnifiedChallengeBladeWidget), "SetChallengeDeck");
                if (setDeckMethod != null)
                {
                    setDeckMethod.Invoke(widget, new object[] { null, null });
                    Plugin.Log.LogInfo("Cleared deck selection for format change");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ClearSelectedDeck failed: {ex.Message}");
            }
        }

        /// <summary>
        /// One-time fetch of current lobby format from Firebase.
        /// Catches any changes that happened while we were in a match or before SSE connects.
        /// </summary>
        private static void FetchCurrentLobbyFormat(Spinner_OptionSelector spinner, UnifiedChallengeBladeWidget widget)
        {
            if (ChallengeFormatState.ActiveChallengeId == Guid.Empty) return;

            var client = FirebaseClient.Instance;
            if (!client.IsAuthenticated) return;

            var lobbyPath = $"lobbies/{ChallengeFormatState.ActiveChallengeId}/format";
            Plugin.Log.LogInfo($"Fetching current lobby format from Firebase: {lobbyPath}");

            client.DatabaseGet(lobbyPath, data =>
            {
                if (data == null) return;

                var currentFormat = data.ToString().Trim('"');
                if (string.IsNullOrEmpty(currentFormat) || currentFormat == "null") return;

                if (currentFormat != ChallengeFormatState.SelectedFormat)
                {
                    Plugin.Log.LogInfo($"Format sync: Firebase has '{currentFormat}', we had '{ChallengeFormatState.SelectedFormat}' — updating");
                    ChallengeFormatState.SelectedFormat = currentFormat;

                    // Update spinner
                    int idx = Array.IndexOf(ChallengeFormatState.FormatKeys, currentFormat);
                    if (idx >= 0 && spinner != null)
                    {
                        spinner.SelectOption(idx);
                    }

                    // Clear deck since format changed
                    if (widget != null)
                    {
                        try { ClearSelectedDeck(widget); }
                        catch (Exception ex) { Plugin.Log.LogWarning($"FetchFormat: Could not clear deck: {ex.Message}"); }
                    }

                    int nameIdx = Array.IndexOf(ChallengeFormatState.FormatKeys, currentFormat);
                    string displayName = nameIdx >= 0 ? ChallengeFormatState.FormatOptions[nameIdx] : currentFormat;
                    Toast.Info($"Format synced: {displayName}");
                }
                else
                {
                    Plugin.Log.LogInfo($"Format sync: already in sync ({currentFormat})");
                }
            });
        }

        /// <summary>
        /// Polls the widget's CurrentChallengeId until it's set (the game sets it
        /// asynchronously via OnChallengeDataChanged after OnEnable).
        /// </summary>
        private static System.Collections.IEnumerator PollForChallengeId(
            UnifiedChallengeBladeWidget widget, System.Reflection.FieldInfo cidField)
        {
            for (int i = 0; i < 30; i++) // poll for up to 15 seconds
            {
                yield return new WaitForSeconds(0.5f);

                if (widget == null) yield break;
                if (ChallengeFormatState.ActiveChallengeId != Guid.Empty) yield break; // already captured

                try
                {
                    var cid = (Guid)cidField.GetValue(widget);
                    if (cid != Guid.Empty)
                    {
                        ChallengeFormatState.ActiveChallengeId = cid;
                        Plugin.Log.LogInfo($"PollForChallengeId: captured {cid}");
                        PerPlayerLog.Info($"PollForChallengeId: captured {cid}");

                        // Host: re-register lobby with new challenge ID
                        if (!ChallengeFormatState.IsJoining && ChallengeFormatState.HasFormat &&
                            FirebaseClient.Instance.IsAuthenticated)
                        {
                            RegisterLobbyIfNeeded(cid);
                        }

                        // Joiner: start SSE on the new lobby
                        if (ChallengeFormatState.IsJoining)
                        {
                            if (_currentSpinnerRef?.Target is Spinner_OptionSelector spinner)
                            {
                                FetchCurrentLobbyFormat(spinner, widget);
                                StartSseListener(spinner, widget);
                            }
                        }

                        yield break;
                    }
                }
                catch { }
            }

            Plugin.Log.LogWarning("PollForChallengeId: timed out after 15s");
            PerPlayerLog.Warning("PollForChallengeId: timed out");
        }

        /// <summary>
        /// Start the SSE listener for the joiner to track host format changes.
        /// </summary>
        private static void StartSseListener(Spinner_OptionSelector spinner, UnifiedChallengeBladeWidget widget)
        {
            if (ChallengeFormatState.ActiveChallengeId == Guid.Empty) return;

            var client = FirebaseClient.Instance;
            if (!client.IsAuthenticated) return;

            // Stop any existing listener before creating a new one
            FirebaseSseListener.Instance?.Dispose();

            var listener = new FirebaseSseListener();
            listener.OnDataChanged += (path, data) => HandleSseDataChange(path, data);

            var token = client.IdToken;
            if (string.IsNullOrEmpty(token))
            {
                Plugin.Log.LogWarning("SSE: No auth token available, cannot start listener");
                return;
            }

            Plugin.Log.LogInfo($"SSE: Starting listener for lobby {ChallengeFormatState.ActiveChallengeId}");
            listener.Start($"lobbies/{ChallengeFormatState.ActiveChallengeId}", token);
        }

        /// <summary>
        /// Starts an SSE listener on the HOST side to receive join requests from the web page.
        /// Called when the lobby is registered or made public.
        /// </summary>
        internal static void StartHostSseListener()
        {
            if (ChallengeFormatState.ActiveChallengeId == Guid.Empty) return;
            if (ChallengeFormatState.IsJoining) return; // Only host, not joiner

            var client = FirebaseClient.Instance;
            if (!client.IsAuthenticated) return;

            // Don't restart if already listening
            if (FirebaseSseListener.Instance != null) return;

            var listener = new FirebaseSseListener();
            listener.OnDataChanged += (path, data) => HandleSseDataChange(path, data);

            var token = client.IdToken;
            if (string.IsNullOrEmpty(token))
            {
                Plugin.Log.LogWarning("Host SSE: No auth token available");
                return;
            }

            Plugin.Log.LogInfo($"Host SSE: Starting listener for lobby {ChallengeFormatState.ActiveChallengeId}");
            PerPlayerLog.Info($"Host SSE: Starting listener for lobby {ChallengeFormatState.ActiveChallengeId}");
            listener.Start($"lobbies/{ChallengeFormatState.ActiveChallengeId}", token);
        }

        /// <summary>
        /// Handles all SSE data changes on the lobby path — format changes (joiner)
        /// and join requests (host).
        /// </summary>
        private static void HandleSseDataChange(string path, Newtonsoft.Json.Linq.JToken data)
        {
            // --- Format change handling (for joiner) ---
            string newFormat = null;

            if (path == "/" && data is Newtonsoft.Json.Linq.JObject obj)
            {
                newFormat = obj["format"]?.ToString();

                // Also check for join requests in full object updates (host)
                if (!ChallengeFormatState.IsJoining)
                {
                    var joinRequests = obj["joinRequests"] as Newtonsoft.Json.Linq.JObject;
                    if (joinRequests != null)
                    {
                        foreach (var prop in joinRequests.Properties())
                        {
                            var req = prop.Value as Newtonsoft.Json.Linq.JObject;
                            if (req != null && req["status"]?.ToString() == "pending")
                            {
                                HandleJoinRequest(prop.Name, req);
                            }
                        }
                    }
                }
            }
            else if (path == "/format")
            {
                newFormat = data?.ToString();
            }

            // --- Join request handling (for host) ---
            if (!ChallengeFormatState.IsJoining && path.StartsWith("/joinRequests/"))
            {
                // Path like /joinRequests/{pushId} or /joinRequests/{pushId}/status
                var segments = path.Split('/');
                if (segments.Length >= 3)
                {
                    var pushId = segments[2];
                    // Only handle new requests, not status updates
                    if (segments.Length == 3 && data is Newtonsoft.Json.Linq.JObject reqObj)
                    {
                        if (reqObj["status"]?.ToString() == "pending")
                        {
                            HandleJoinRequest(pushId, reqObj);
                        }
                    }
                }
                return; // Don't process as format change
            }

            // --- Process format change ---
            if (newFormat == null || newFormat == ChallengeFormatState.SelectedFormat) return;

            Plugin.Log.LogInfo($"SSE: Host changed format to {newFormat}");
            PerPlayerLog.Info($"SSE: Host changed format to {newFormat}");
            ChallengeFormatState.SelectedFormat = newFormat;

            // Update spinner if still alive
            if (_currentSpinnerRef?.Target is Spinner_OptionSelector spinner && spinner != null)
            {
                int idx = Array.IndexOf(ChallengeFormatState.FormatKeys, newFormat);
                if (idx >= 0)
                {
                    spinner.SelectOption(idx);
                }
            }

            // Clear deck
            if (_currentWidgetRef?.Target is UnifiedChallengeBladeWidget widget && widget != null)
            {
                try { ClearSelectedDeck(widget); }
                catch (Exception ex) { Plugin.Log.LogWarning($"SSE: Could not clear deck: {ex.Message}"); }
            }

            int nameIdx = Array.IndexOf(ChallengeFormatState.FormatKeys, newFormat);
            string displayName = nameIdx >= 0 ? ChallengeFormatState.FormatOptions[nameIdx] : newFormat;
            Toast.Info($"Host changed format to {displayName}");
        }

        // --- Join Request Invite Flow ---

        private static readonly HashSet<string> _processedJoinRequests = new HashSet<string>();

        private static void HandleJoinRequest(string pushId, Newtonsoft.Json.Linq.JObject data)
        {
            if (_processedJoinRequests.Contains(pushId)) return;
            _processedJoinRequests.Add(pushId);

            var username = data["username"]?.ToString();
            if (string.IsNullOrEmpty(username)) return;

            PerPlayerLog.Info($"Join request received: {username} (id={pushId})");
            Toast.Info($"Invite request from {ChallengeFormatState.StripDiscriminator(username)}...");

            FirebaseClient.Instance.StartCoroutine(ResolveAndInvitePlayer(username, pushId));
        }

        private static IEnumerator ResolveAndInvitePlayer(string username, string pushId)
        {
            // Step 1: Resolve username → PersonaId via ISocialManager
            var pantryType = GameReflection.TypeByName("Pantry");
            Type socialManagerType = null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                foreach (var t in asm.GetTypes())
                    if (t.Name == "ISocialManager" && t.IsInterface)
                    { socialManagerType = t; break; }

            if (pantryType == null || socialManagerType == null)
            {
                PerPlayerLog.Error("ResolveAndInvite: Pantry or ISocialManager not found");
                Toast.Error($"Could not invite {ChallengeFormatState.StripDiscriminator(username)}");
                UpdateJoinRequestStatus(pushId, "failed");
                yield break;
            }

            object socialManager;
            try
            {
                var getMethod = pantryType.GetMethod("Get").MakeGenericMethod(socialManagerType);
                socialManager = getMethod.Invoke(null, null);
            }
            catch (Exception ex)
            {
                PerPlayerLog.Error($"ResolveAndInvite: Failed to get ISocialManager: {ex.Message}");
                UpdateJoinRequestStatus(pushId, "failed");
                yield break;
            }

            var resolveMethod = AccessTools.Method(socialManager.GetType(), "GetPlayerIdFromFullPlayerName");
            if (resolveMethod == null)
            {
                PerPlayerLog.Error("ResolveAndInvite: GetPlayerIdFromFullPlayerName not found");
                UpdateJoinRequestStatus(pushId, "failed");
                yield break;
            }

            PerPlayerLog.Info($"Resolving player: {username}");
            var promise = resolveMethod.Invoke(socialManager, new object[] { username });

            // Poll promise for up to 10 seconds
            var isDoneProp = AccessTools.Property(promise.GetType(), "IsDone");
            var successProp = AccessTools.Property(promise.GetType(), "Successful");
            var resultProp = AccessTools.Property(promise.GetType(), "Result");

            for (int i = 0; i < 20; i++)
            {
                yield return new WaitForSeconds(0.5f);
                if (isDoneProp != null && (bool)isDoneProp.GetValue(promise)) break;
            }

            bool resolved = successProp != null && (bool)successProp.GetValue(promise);
            if (!resolved)
            {
                PerPlayerLog.Warning($"Could not find player: {username}");
                Toast.Warning($"Player not found: {ChallengeFormatState.StripDiscriminator(username)}");
                UpdateJoinRequestStatus(pushId, "failed");
                yield break;
            }

            var playerId = resultProp?.GetValue(promise)?.ToString();
            if (string.IsNullOrEmpty(playerId))
            {
                PerPlayerLog.Warning($"Player resolve returned empty: {username}");
                Toast.Warning($"Player not found: {ChallengeFormatState.StripDiscriminator(username)}");
                UpdateJoinRequestStatus(pushId, "failed");
                yield break;
            }

            PerPlayerLog.Info($"Resolved {username} → {playerId}");

            // Step 2: Send invite via PVPChallengeController
            var controller = ChallengeCreatePatch.CachedController;
            var controllerType = ChallengeCreatePatch.CachedControllerType;

            if (controller == null || controllerType == null)
            {
                PerPlayerLog.Error("ResolveAndInvite: No cached PVPChallengeController");
                Toast.Error("Could not send invite (no challenge controller)");
                UpdateJoinRequestStatus(pushId, "failed");
                yield break;
            }

            var challengeId = ChallengeFormatState.ActiveChallengeId;
            if (challengeId == Guid.Empty)
            {
                PerPlayerLog.Error("ResolveAndInvite: No active challenge ID");
                UpdateJoinRequestStatus(pushId, "failed");
                yield break;
            }

            try
            {
                var addInviteMethod = AccessTools.Method(controllerType, "AddChallengeInvite");
                var sendInvitesMethod = AccessTools.Method(controllerType, "SendChallengeInvites");

                if (addInviteMethod == null || sendInvitesMethod == null)
                {
                    PerPlayerLog.Error($"ResolveAndInvite: Methods not found — add={addInviteMethod != null}, send={sendInvitesMethod != null}");
                    UpdateJoinRequestStatus(pushId, "failed");
                    yield break;
                }

                PerPlayerLog.Info($"Calling AddChallengeInvite({challengeId}, {username}, {playerId})");
                addInviteMethod.Invoke(controller, new object[] { challengeId, username, playerId });

                PerPlayerLog.Info($"Calling SendChallengeInvites({challengeId})");
                sendInvitesMethod.Invoke(controller, new object[] { challengeId });

                Toast.Success($"Invite sent to {ChallengeFormatState.StripDiscriminator(username)}!");
                PerPlayerLog.Info($"Invite sent to {username} ({playerId})");
                UpdateJoinRequestStatus(pushId, "sent");
            }
            catch (Exception ex)
            {
                PerPlayerLog.Error($"Failed to send invite: {ex}");
                Toast.Error($"Failed to invite {ChallengeFormatState.StripDiscriminator(username)}");
                UpdateJoinRequestStatus(pushId, "failed");
            }
        }

        private static void UpdateJoinRequestStatus(string pushId, string status)
        {
            if (ChallengeFormatState.ActiveChallengeId == Guid.Empty) return;
            var challengeId = ChallengeFormatState.ActiveChallengeId.ToString();
            FirebaseClient.Instance.PatchLobby(challengeId,
                $"{{\"joinRequests/{pushId}/status\":\"{status}\"}}");
            PerPlayerLog.Info($"Updated join request {pushId} status to: {status}");
        }

        private static GameObject CreateSimpleButton(Transform parent, string name, string label,
            float x, float y, float width, float height, Color color)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            var rect = obj.AddComponent<RectTransform>();

            // Add canvas to render on top
            var canvas = obj.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 51;
            obj.AddComponent<GraphicRaycaster>();

            obj.AddComponent<Image>().color = color;
            obj.AddComponent<Button>();

            var textObj = new GameObject("Text");
            textObj.transform.SetParent(obj.transform, false);
            var textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;

            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 15;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;

            return obj;
        }
    }
}
