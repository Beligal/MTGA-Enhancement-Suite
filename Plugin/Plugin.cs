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

            // Se l'avvio e' andato bene si conferma l'aggiornamento in fondo a
            // questo metodo. Questi due blocchi intercettano i propri errori —
            // di proposito, perche' una patch che non attacca non deve impedire
            // al resto di funzionare — quindi senza tenerne traccia si
            // arriverebbe in fondo identici sia che abbiano funzionato sia che
            // non abbiano fatto nulla.
            bool startupOk = true;

            try
            {
                _harmony = new Harmony(PluginInfo.GUID);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.LogInfo("Attribute-based Harmony patches applied");
            }
            catch (Exception ex)
            {
                startupOk = false;
                Log.LogError($"Failed to apply attribute-based Harmony patches: {ex}");
            }

            try
            {
                Patches.CompanionDisablePatch.Apply(_harmony);
                Patches.CardVFXPatch.Apply(_harmony);
                Patches.ForceSimplifiedPatch.Apply(_harmony);
                Patches.HideDeckCardsPatch.Apply(_harmony);
                Patches.SparkyDeckPatch.Apply(_harmony);
                Patches.SparkyBehaviourPatch.Apply(_harmony);
                Log.LogInfo("Manual Harmony patches applied");
            }
            catch (Exception ex)
            {
                startupOk = false;
                Log.LogError($"Failed to apply manual Harmony patches: {ex}");
            }

            var persistentObj = new GameObject("MTGAEnhancementSuite_Persistent");
            persistentObj.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(persistentObj);
            persistentObj.AddComponent<PluginBehaviour>();

            Log.LogInfo("Persistent behaviour created");

            // Adesso si': le patch hanno attaccato e il comportamento persistente
            // esiste. Se l'avvio precedente aveva applicato un aggiornamento, e'
            // qui che lo si conferma e i backup vengono rimossi.
            //
            // Non confermare significa che al prossimo avvio il bootstrapper
            // rimette la versione di prima. Se quella nuova continua a fallire,
            // l'aggiornamento verra' rimesso in coda e ritirato via a ogni
            // avvio: e' rumoroso nel log, e resta preferibile a lasciare in
            // esecuzione una versione che non attacca le sue patch.
            if (startupOk)
            {
                AutoUpdater.ConfirmUpdateApplied();
            }
            else
            {
                Log.LogError("Startup incomplete — NOT confirming the update. "
                             + "The previous version will be restored on the next launch.");
            }
        }

        private void OnDestroy()
        {
            Log.LogInfo("Plugin OnDestroy called (this is expected)");
        }
    }

    internal class PluginBehaviour : MonoBehaviour
    {
        /// <summary>
        /// Il MonoBehaviour persistente del plugin. Vive per tutta la sessione:
        /// sta su un GameObject DontDestroyOnLoad creato in Plugin.Awake(), e il
        /// suo OnDestroy scatta solo alla chiusura del gioco.
        ///
        /// Serve a chi deve avviare una coroutine senza avere un MonoBehaviour
        /// proprio. Prima quel ruolo lo faceva FirebaseClient.Instance, che pero'
        /// e' un componente del backend: usarlo come semplice ospite legava a
        /// Firebase codice che con Firebase non c'entra niente.
        /// </summary>
        internal static PluginBehaviour Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            Plugin.Log.LogInfo("PluginBehaviour.Awake - starting coroutines");

            // Load local decks from disk so they're ready when the deck
            // manager first opens. Cheap file scan; safe if the folder is empty.
            try { Features.LocalDeckStore.Load(); }
            catch (Exception ex) { Plugin.Log.LogWarning($"LocalDeckStore.Load on startup: {ex.Message}"); }

            // Tracce lasciate dalle funzioni server su chi aggiorna da una
            // versione precedente: l'aggiornatore sostituisce i file, non li
            // cancella. Non solleva mai: si arrangia da sola.
            Features.LegacyCleanup.Run();

            SceneManager.sceneLoaded += OnSceneLoaded;
            StartCoroutine(PollForNavBar());
            // Auto-update dalle release del fork (Beligal/MTGA-Enhancement-Suite),
            // firmate con la nostra chiave. Era disattivato finche' puntava a
            // upstream, perche' avrebbe sovrascritto questa build con quella
            // ufficiale a ogni avvio.
            //
            // Finche' il repo non ha release, GitHub risponde 404: la richiesta
            // fallisce, viene loggata una riga di warning e non succede altro.
            StartCoroutine(AutoUpdater.CheckForUpdate());
            StartCoroutine(Features.IconBootstrapper.EnsureIcons());
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
                if (KeyPressed(Key.F9))
                    UI.SparkyMenu.Toggle();
            });

            RunFeature("AnimationFastForward", Features.AnimationFastForward.Tick);

            // Pulsante preset a destra della barra filtri del deck editor.
            RunFeature("DeckPresetButton", Features.DeckPresetButton.Tick);

            // Registrazione degli esiti delle partite. Sonda il MatchManager
            // ogni 0,25 s e si aggancia quando compare: fuori dal duello non
            // esiste, perche' non e' nella tabella dei servizi del Pantry.
            RunFeature("MatchRecorder", Features.MatchRecorder.Tick);
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
        public const string VERSION = "0.25.0";
    }
}
