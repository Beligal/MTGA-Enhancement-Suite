using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using MTGAEnhancementSuite.Diagnostics;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using MTGAEnhancementSuite.UrlScheme;
using MTGAEnhancementSuite.Helpers;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace MTGAEnhancementSuite
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("Awake called");

            // Enable TLS 1.2 for all HTTP requests (Mono compatibility)
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |=
                    System.Net.SecurityProtocolType.Tls12;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Could not set TLS 1.2: {ex.Message}");
            }

            // Check for URL scheme argument FIRST — before anything else.
            // If another MTGA instance is running, forward the URL and kill this process immediately.
            var url = UrlSchemeRegistrar.GetUrlFromArgs();
            if (url != null)
            {
                if (TcpIpcServer.TrySendToExisting(url))
                {
                    Log.LogInfo("Sent URL to existing MTGA instance, killing this process");
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                    return; // unreachable, but makes the compiler happy
                }
                else
                {
                    // We're the only instance — store as pending join
                    if (UrlSchemeRegistrar.ParseJoinUrl(url, out var challengeId, out var format))
                    {
                        ChallengeFormatState.SetPendingJoin(challengeId, format);
                        Log.LogInfo($"Pending join stored: {challengeId} ({format})");
                    }
                }
            }

            // Register URL scheme (idempotent)
            UrlSchemeRegistrar.Register();

            try
            {
                _harmony = new Harmony(PluginInfo.GUID);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.LogInfo("Attribute-based Harmony patches applied");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to apply attribute-based Harmony patches: {ex}");
            }

            try
            {
                Patches.ChallengeCreatePatch.Apply(_harmony);
                Patches.DeckValidationPatch.Apply(_harmony);
                Patches.ChallengeJoinNotifyPatch.Apply(_harmony);
                Patches.BestOfPatch.Apply(_harmony);
                Patches.CompanionDisablePatch.Apply(_harmony);
                Patches.CardVFXPatch.Apply(_harmony);
                Patches.ForceSimplifiedPatch.Apply(_harmony);
                Log.LogInfo("Manual Harmony patches applied");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to apply manual Harmony patches: {ex}");
            }

            var persistentObj = new GameObject("MTGAEnhancementSuite_Persistent");
            persistentObj.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(persistentObj);
            persistentObj.AddComponent<PluginBehaviour>();

            Log.LogInfo("Persistent behaviour created");
        }

        private void OnDestroy()
        {
            Log.LogInfo("Plugin OnDestroy called (this is expected)");
        }
    }

    internal class PluginBehaviour : MonoBehaviour
    {
        private void Awake()
        {
            Plugin.Log.LogInfo("PluginBehaviour.Awake - starting coroutines");

            // Load local decks from disk so they're ready when the deck
            // manager first opens. Cheap file scan; safe if the folder is empty.
            try { Features.LocalDeckStore.Load(); }
            catch (Exception ex) { Plugin.Log.LogWarning($"LocalDeckStore.Load on startup: {ex.Message}"); }

            SceneManager.sceneLoaded += OnSceneLoaded;
            StartCoroutine(PollForNavBar());
            StartCoroutine(Patches.AuthPatch.WaitForLoginAndAuth());
            // Auto-update dalle release del fork (Beligal/MTGA-Enhancement-Suite),
            // firmate con la nostra chiave. Era disattivato finche' puntava a
            // upstream, perche' avrebbe sovrascritto questa build con quella
            // ufficiale a ogni avvio.
            //
            // Finche' il repo non ha release, GitHub risponde 404: la richiesta
            // fallisce, viene loggata una riga di warning e non succede altro.
            StartCoroutine(AutoUpdater.CheckForUpdate());
            StartCoroutine(Features.IconBootstrapper.EnsureIcons());

            // Start named pipe server for receiving URLs from other instances
            TcpIpcServer.OnUrlReceived = OnUrlReceivedFromPipe;
            TcpIpcServer.Start();

            // Process pending join after auth completes
            StartCoroutine(ProcessPendingJoinAfterAuth());

            // Hook challenge lifecycle events after Pantry is ready
            StartCoroutine(HookChallengeLifecycleEvents());
        }

        // F7 = toggle 100x fast-forward on the duel's animation queue.
        // Bound on the persistent behaviour so it works in every scene;
        // safe to mash outside a duel because Toggle() no-ops when
        // GameManager is missing. wasPressedThisFrame is edge-trigger so
        // holding doesn't re-fire every frame.
        //
        // Tick() runs every frame to watch the queue and auto-stop the
        // fast-forward as soon as the backlog is drained — that's the
        // "we've caught up" signal. It also handles the failsafe case
        // where the duel scene tears down mid-fast-forward.
        //
        // Ogni feature gira nel proprio try: un errore in una non deve
        // spegnere le altre. Fino al 26/08/2026 c'era un unico try attorno a
        // tutto, e quando Arena e' passata all'Input System il primo
        // GetKeyDown ha fatto sparire in silenzio fast-forward, timer di turno
        // e pulsante preset. Il log gridava; l'interfaccia taceva.
        private void Update()
        {
            // unscaledDeltaTime qui e' la durata del frame *precedente*, e i costi
            // accumulati sono i suoi: il resoconto va stampato prima di azzerarli.
            ReportHitchIfAny(Time.unscaledDeltaTime * 1000f);

            RunFeature("Hotkeys", () =>
            {
                if (KeyPressed(Key.F7))
                    Features.AnimationFastForward.Toggle();
            });

            RunFeature("AnimationFastForward", Features.AnimationFastForward.Tick);

            // Pulsante preset a destra della barra filtri del deck editor.
            RunFeature("DeckPresetButton", Features.DeckPresetButton.Tick);
        }

        // Arena gira con l'active input handling impostato su Input System
        // package: la vecchia UnityEngine.Input lancia InvalidOperationException
        // invece di rispondere. Keyboard.current e' null finche' il sistema non
        // e' inizializzato (o se non c'e' tastiera), quindi va sempre controllato.
        private static bool KeyPressed(Key key)
        {
            var keyboard = Keyboard.current;
            return keyboard != null && keyboard[key].wasPressedThisFrame;
        }

        private void RunFeature(string name, Action body)
        {
            using (FrameCost.Measure(name))
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    LogFeatureError(name, ex);
                }
            }
        }

        /// <summary>
        /// Diagnostica di fluidita'. Quando un frame supera la soglia, stampa
        /// quanto ci abbiamo messo *noi* in quel frame, feature per feature.
        ///
        /// Serve a rispondere a una domanda che a occhio non si risponde: se il
        /// client si impunta per un secondo, e' colpa nostra o di Arena? Una riga
        /// che dice "frame da 900 ms, di cui 0,3 nostri" e' un'assoluzione, e vale
        /// quanto la riga opposta. Silenziosa finche' non succede, e comunque non
        /// piu' di una riga al secondo: durante i caricamenti i frame lunghi sono
        /// la norma e non serve un muro di log.
        /// </summary>
        private const float HitchThresholdMs = 100f;
        private const float HitchReportInterval = 1f;
        private static float _nextHitchReport;

        private static void ReportHitchIfAny(float frameMs)
        {
            if (frameMs < HitchThresholdMs || FrameCost.IsEmpty)
            {
                FrameCost.Clear();
                return;
            }

            if (Time.unscaledTime < _nextHitchReport)
            {
                FrameCost.Clear();
                return;
            }
            _nextHitchReport = Time.unscaledTime + HitchReportInterval;

            string detail;
            var ours = FrameCost.Total(out detail);
            FrameCost.Clear();

            Plugin.Log.LogInfo($"Hitch: frame {frameMs:F0} ms, nostri {ours:F2} ms{detail}");
        }

        /// <summary>
        /// Un errore dentro Update() si ripete a ogni frame: loggarlo sempre
        /// vuol dire 12.000 righe identiche in una sessione (successo davvero,
        /// il 25/08/2026). Ne teniamo uno per feature al minuto, col conteggio
        /// di quelli soppressi nel mezzo.
        /// </summary>
        private static void LogFeatureError(string name, Exception ex)
        {
            FeatureErrorState state;
            if (!_featureErrors.TryGetValue(name, out state))
            {
                state = new FeatureErrorState();
                _featureErrors[name] = state;
            }

            if (Time.unscaledTime < state.NextLog)
            {
                state.Suppressed++;
                return;
            }

            var suppressed = state.Suppressed > 0
                ? $" [+{state.Suppressed} occorrenze soppresse]"
                : "";
            state.Suppressed = 0;
            state.NextLog = Time.unscaledTime + FeatureErrorLogInterval;

            Plugin.Log.LogError($"{name}: {ex}{suppressed}");
        }

        private const float FeatureErrorLogInterval = 60f;

        private sealed class FeatureErrorState
        {
            public float NextLog;
            public int Suppressed;
        }

        private static readonly Dictionary<string, FeatureErrorState> _featureErrors =
            new Dictionary<string, FeatureErrorState>();

        private void OnUrlReceivedFromPipe(string url)
        {
            Plugin.Log.LogInfo($"URL received from pipe: {url}");

            if (UrlSchemeRegistrar.ParseJoinUrl(url, out var challengeId, out var format))
            {
                // Queue the join on the main thread
                MainThreadDispatcher.Enqueue(() =>
                {
                    EnhancementSuitePanel.JoinLobby(challengeId, format);
                });
            }
        }

        private IEnumerator ProcessPendingJoinAfterAuth()
        {
            // Wait until Firebase auth is complete
            while (!Firebase.FirebaseClient.Instance.IsAuthenticated)
                yield return new WaitForSeconds(2f);

            // Check for pending join
            if (ChallengeFormatState.HasPendingJoin)
            {
                Plugin.Log.LogInfo($"Processing pending join: {ChallengeFormatState.PendingJoinChallengeId}");

                // Wait for the main menu to fully load:
                // 1. NavBarController must exist (main menu is rendered)
                // 2. IChallengeCommunicationWrapper must be in Pantry (challenge system ready)
                Plugin.Log.LogInfo("Waiting for main menu and challenge system...");

                // Wait for NavBarController
                float timeout = 120f; // 2 minute max wait
                while (timeout > 0f)
                {
                    var navBar = UnityEngine.Object.FindObjectOfType<NavBarController>();
                    if (navBar != null)
                    {
                        Plugin.Log.LogInfo("NavBarController found — main menu is loaded");
                        break;
                    }
                    yield return new WaitForSeconds(2f);
                    timeout -= 2f;
                }

                if (timeout <= 0f)
                {
                    Plugin.Log.LogWarning("Timed out waiting for main menu");
                    ChallengeFormatState.ClearPendingJoin();
                    yield break;
                }

                // Wait for challenge system to be ready in Pantry
                var pantryType = GameReflection.TypeByName("Pantry");
                Type commWrapperType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == "IChallengeCommunicationWrapper" && t.IsInterface)
                        { commWrapperType = t; break; }
                    }
                    if (commWrapperType != null) break;
                }

                if (commWrapperType != null && pantryType != null)
                {
                    timeout = 30f;
                    while (timeout > 0f)
                    {
                        try
                        {
                            var wrapper = pantryType.GetMethod("Get")
                                .MakeGenericMethod(commWrapperType)
                                .Invoke(null, null);
                            if (wrapper != null)
                            {
                                Plugin.Log.LogInfo("IChallengeCommunicationWrapper ready in Pantry");
                                break;
                            }
                        }
                        catch { }
                        yield return new WaitForSeconds(2f);
                        timeout -= 2f;
                    }
                }

                // Extra buffer for UI to fully stabilize
                yield return new WaitForSeconds(5f);

                if (!ChallengeFormatState.HasPendingJoin)
                {
                    Plugin.Log.LogInfo("Pending join was cleared while waiting");
                    yield break;
                }

                var challengeId = ChallengeFormatState.PendingJoinChallengeId;
                var format = ChallengeFormatState.PendingJoinFormat;
                ChallengeFormatState.ClearPendingJoin();

                Plugin.Log.LogInfo($"Executing pending join: {challengeId} format={format}");
                EnhancementSuitePanel.JoinLobby(challengeId, format);
            }
        }

        private IEnumerator HookChallengeLifecycleEvents()
        {
            // Wait for Pantry to be available and populated
            Type pantryType = null;
            while (pantryType == null)
            {
                yield return new WaitForSeconds(3f);
                pantryType = GameReflection.TypeByName("Pantry");
            }

            // Wait for IChallengeCommunicationWrapper to be registered
            Type commWrapperType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "IChallengeCommunicationWrapper" && t.IsInterface)
                    {
                        commWrapperType = t;
                        break;
                    }
                }
                if (commWrapperType != null) break;
            }

            if (commWrapperType == null)
            {
                Plugin.Log.LogWarning("Could not find IChallengeCommunicationWrapper for lifecycle hooks");
                yield break;
            }

            object wrapper = null;
            for (int i = 0; i < 30; i++)
            {
                yield return new WaitForSeconds(2f);
                try
                {
                    wrapper = pantryType.GetMethod("Get").MakeGenericMethod(commWrapperType)
                        .Invoke(null, null);
                    if (wrapper != null) break;
                }
                catch { }
            }

            if (wrapper == null)
            {
                Plugin.Log.LogWarning("IChallengeCommunicationWrapper not available in Pantry");
                yield break;
            }

            Plugin.Log.LogInfo($"Hooking challenge lifecycle on {wrapper.GetType().Name}");

            // Hook OnChallengeCountdownStart — Action<Guid, int>
            try
            {
                var countdownProp = AccessTools.Property(wrapper.GetType(), "OnChallengeCountdownStart");
                if (countdownProp != null)
                {
                    var existing = countdownProp.GetValue(wrapper) as Delegate;
                    Action<Guid, int> handler = OnChallengeCountdownStart;
                    var combined = Delegate.Combine(existing, handler);
                    countdownProp.SetValue(wrapper, combined);
                    Plugin.Log.LogInfo("Hooked OnChallengeCountdownStart");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to hook OnChallengeCountdownStart: {ex}");
            }

            // Hook OnChallengeClosedMessage — Action<Guid>
            try
            {
                var closedProp = AccessTools.Property(wrapper.GetType(), "OnChallengeClosedMessage");
                if (closedProp != null)
                {
                    var existing = closedProp.GetValue(wrapper) as Delegate;
                    Action<Guid> handler = OnChallengeClosed;
                    var combined = Delegate.Combine(existing, handler);
                    closedProp.SetValue(wrapper, combined);
                    Plugin.Log.LogInfo("Hooked OnChallengeClosedMessage");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to hook OnChallengeClosedMessage: {ex}");
            }
        }

        private static void OnChallengeCountdownStart(Guid challengeId, int countdownSeconds)
        {
            Plugin.Log.LogInfo($"Match countdown started: {challengeId} ({countdownSeconds}s)");
            ChallengeFormatState.IsInMatch = true;

            // Pause SSE listener during match
            Firebase.FirebaseSseListener.Instance?.Stop();
        }

        private static void OnChallengeClosed(Guid challengeId)
        {
            Plugin.Log.LogInfo($"Challenge closed: {challengeId}");
            PerPlayerLog.Info($"Challenge closed: {challengeId} (InLobbySession={ChallengeFormatState.InLobbySession}, IsInMatch={ChallengeFormatState.IsInMatch}, format={ChallengeFormatState.SelectedFormat})");

            // MTGA's server sends ChallengeClose after every match — it removes the old
            // challenge and the game creates a fresh one for rematches. Our format/lobby
            // state is independent of MTGA's challenge lifecycle.
            //
            // If we're in a lobby session, preserve our format state. The new challenge ID
            // will be captured from the widget when the blade re-opens.
            // Firebase lobby cleanup for the OLD challenge ID is fine — we'll register a
            // new one when the host clicks Copy Link / Make Public again, or the
            // InjectFormatSpinner captures the new ID.
            if (ChallengeFormatState.InLobbySession)
            {
                Plugin.Log.LogInfo($"Challenge closed in lobby session — preserving format state, clearing old challenge ID");
                PerPlayerLog.Info($"Preserving lobby session (format={ChallengeFormatState.SelectedFormat})");

                // Delete old lobby from Firebase, but keep format/joining/session state
                if (!ChallengeFormatState.IsJoining && ChallengeFormatState.ActiveChallengeId != Guid.Empty)
                {
                    Firebase.FirebaseClient.Instance.StopHeartbeat();
                    Firebase.FirebaseClient.Instance.DeleteLobby(ChallengeFormatState.ActiveChallengeId.ToString());
                }
                Firebase.FirebaseSseListener.Instance?.Dispose();
                ChallengeFormatState.ActiveChallengeId = Guid.Empty;
                // The Firebase lobby record for the old challenge ID is gone now,
                // so the "is this ID currently registered?" cache must be cleared
                // too — otherwise the next RegisterLobbyIfNeeded for the recycled
                // challenge could short-circuit and the new lobby would never
                // appear in Firebase. (Symptom: web "Request Invite" 404s after
                // any post-match challenge recycle.)
                Patches.ChallengeSettingsPatch._registeredChallengeId = Guid.Empty;
                return;
            }

            // Not in a lobby session — full cleanup
            if (!ChallengeFormatState.IsJoining && ChallengeFormatState.ActiveChallengeId != Guid.Empty)
            {
                Firebase.FirebaseClient.Instance.StopHeartbeat();
                Firebase.FirebaseClient.Instance.DeleteLobby(ChallengeFormatState.ActiveChallengeId.ToString());
            }

            Firebase.FirebaseSseListener.Instance?.Dispose();
            ChallengeFormatState.Reset();
            Patches.ChallengeSettingsPatch._registeredChallengeId = Guid.Empty;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Plugin.Log.LogInfo($"Scene loaded: {scene.name} (mode: {mode})");
        }

        private IEnumerator PollForNavBar()
        {
            Plugin.Log.LogInfo("PollForNavBar coroutine running");

            while (true)
            {
                yield return new WaitForSeconds(2f);

                try
                {
                    var navBar = FindObjectOfType<NavBarController>();
                    if (navBar != null && navBar.MasteryButton != null)
                    {
                        // Check Base_Middle (where tab is injected), not Base
                        var baseMiddle = navBar.MasteryButton.transform.parent;
                        if (baseMiddle.Find(GameRefs.EnhancementSuiteTabName) == null)
                        {
                            Plugin.Log.LogInfo("NavBarController found, injecting tab...");
                            Patches.NavBarPatch.InjectTab(navBar);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"Poll error: {ex}");
                }
            }
        }

        private void OnDestroy()
        {
            Plugin.Log.LogInfo("PluginBehaviour.OnDestroy called (should NOT happen)");
            SceneManager.sceneLoaded -= OnSceneLoaded;
            TcpIpcServer.Stop();
            // Last-ditch failsafe: if the plugin is torn down mid-fast-
            // forward, restore Time.timeScale so the game (or whatever
            // host process is left) doesn't continue at 100x.
            Features.AnimationFastForward.ForceRestore();
        }
    }

    /// <summary>
    /// Simple main thread dispatcher for executing actions from background threads.
    /// </summary>
    internal class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly System.Collections.Generic.Queue<Action> _queue =
            new System.Collections.Generic.Queue<Action>();

        public static void Enqueue(Action action)
        {
            if (_instance == null)
            {
                var go = new GameObject("MainThreadDispatcher");
                go.hideFlags = HideFlags.HideAndDontSave;
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<MainThreadDispatcher>();
            }

            lock (_queue)
            {
                _queue.Enqueue(action);
            }
        }

        private void Update()
        {
            lock (_queue)
            {
                while (_queue.Count > 0)
                {
                    try { _queue.Dequeue()?.Invoke(); }
                    catch (Exception ex) { Plugin.Log.LogError($"MainThreadDispatcher error: {ex}"); }
                }
            }
        }
    }

    internal static class PluginInfo
    {
        public const string GUID = "com.mtgaenhancement.suite";
        public const string NAME = "MTGA Enhancement Suite";
        public const string VERSION = "0.18.2";
    }
}
