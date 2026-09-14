using System;
using System.Collections.Generic;
using GreClient.Rules;
using HarmonyLib;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.UI;
using UnityEngine;
using Wotc.Mtga.DuelScene;
using Wotc.Mtgo.Gre.External.Messaging;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Impone a Sparky un comportamento fisso invece di lasciarlo decidere.
    ///
    /// COME CI INFILIAMO — tutto il gestore del bot e' questo, ed e' l'intera
    /// classe <c>FamiliarRequestHandler</c>:
    ///
    /// <code>
    /// public void OnUpdate(float time)
    /// {
    ///     if (_familiarClient.PendingInteraction != null
    ///         &amp;&amp; _familiarClient.StrategyEnabled
    ///         &amp;&amp; !_eventQueue.IsRunning)
    ///     {
    ///         _familiarClient.HandleRequest();
    ///     }
    /// }
    /// </code>
    ///
    /// Un prefix su <c>OnUpdate</c> cade nello stesso frame, un attimo prima:
    /// se la richiesta e' di un tipo che la modalita' attiva sa gestire,
    /// rispondiamo noi e azzeriamo <c>PendingInteraction</c>, cosi' l'originale
    /// trova null e non fa niente. Tutte le altre richieste passano intatte
    /// alla strategia del gioco.
    ///
    /// PERCHE' COSI' E NON SPEGNENDO <c>StrategyEnabled</c>: perche' la partita
    /// non si mette in pausa mentre pensiamo. Scorrono i timer veri
    /// (`Inactivity` 150 s, e gli `ActivePlayer`/`NonActivePlayer` con
    /// `TakeControl`), quindi una modalita' che spegne il bot e aspetta finisce
    /// col server che prende il controllo. La regola qui e': rispondiamo
    /// **sempre** a cio' che dichiariamo di gestire, e non tocchiamo il resto.
    ///
    /// Vale solo nelle partite contro il bot: fuori di li' <c>Familiars</c> e'
    /// vuoto. Vedi research/arena-internals/sparky-control.md.
    /// </summary>
    internal static class SparkyBehaviourPatch
    {
        internal enum Behaviour
        {
            /// <summary>Il gioco decide, noi non tocchiamo niente.</summary>
            Default,
            /// <summary>Non dichiara mai attaccanti.</summary>
            NoAttacks,
            /// <summary>Attacca sempre con tutto quello che puo'.</summary>
            AlwaysAttack,
            /// <summary>Passa sempre la priorita': niente magie, niente abilita'.</summary>
            DrawAndPass
        }

        /// <summary>
        /// Come per il mazzo, vale solo per questa sessione: a ogni riavvio si
        /// torna al comportamento del gioco.
        /// </summary>
        public static Behaviour Mode { get; private set; } = Behaviour.Default;

        public static void Choose(Behaviour mode)
        {
            Mode = mode;
            Toast.Info("Sparky: " + Describe(mode));
            Plugin.Log.LogInfo($"SparkyBehaviour: modalita' impostata su {mode}.");
        }

        public static string Describe(Behaviour mode)
        {
            switch (mode)
            {
                case Behaviour.NoAttacks: return "no attacks";
                case Behaviour.AlwaysAttack: return "always attack";
                case Behaviour.DrawAndPass: return "draw and pass";
                default: return "normal AI";
            }
        }

        public static void Apply(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(FamiliarRequestHandler), "OnUpdate");
                if (target == null)
                {
                    Plugin.Log.LogWarning(
                        "SparkyBehaviour: FamiliarRequestHandler.OnUpdate non trovato, patch saltata.");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(
                    typeof(SparkyBehaviourPatch), nameof(OnUpdate_Prefix)));
                Plugin.Log.LogInfo("SparkyBehaviour: patch applicata.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"SparkyBehaviour: patch fallita: {ex.Message}");
            }
        }

        private static void OnUpdate_Prefix()
        {
            try
            {
                if (Mode == Behaviour.Default) return;

                // Stesso cancello dell'originale: mentre le animazioni girano
                // il bot non agisce, e non deve agire nemmeno la modalita'.
                var gm = UnityEngine.Object.FindObjectOfType<GameManager>();
                var queue = gm != null ? gm.UXEventQueue : null;
                if (queue != null && queue.IsRunning) return;

                var manager = GameReflection.PantryGet<MatchManager>();
                var familiars = manager != null ? manager.Familiars : null;
                if (familiars == null) return;

                foreach (var familiar in familiars)
                {
                    if (familiar == null || !familiar.StrategyEnabled) continue;

                    var request = familiar.PendingInteraction;
                    if (request == null) continue;

                    if (Handle(request)) familiar.PendingInteraction = null;
                }
            }
            catch (Exception ex)
            {
                // Gira a ogni frame di ogni partita contro il bot: un errore
                // qui non deve ne' ripetersi all'infinito nel log ne' bloccare
                // il gestore del gioco.
                LogOnce($"SparkyBehaviour: {ex.Message}");
            }
        }

        /// <summary>
        /// Vero se abbiamo risposto noi. Falso lascia la richiesta alla
        /// strategia del gioco — ed e' la via d'uscita da usare ogni volta che
        /// non siamo sicuri di poter rispondere legalmente.
        /// </summary>
        private static bool Handle(BaseUserRequest request)
        {
            var attack = request as DeclareAttackerRequest;
            if (attack != null)
            {
                switch (Mode)
                {
                    case Behaviour.NoAttacks: return NoAttacks(attack);
                    case Behaviour.AlwaysAttack: return AlwaysAttack(attack);
                    default: return false;
                }
            }

            if (Mode == Behaviour.DrawAndPass)
            {
                var actions = request as ActionsAvailableRequest;
                // Passiamo solo la priorita'. Mulligan, bersagli, blocchi e
                // tutto il resto restano alla strategia: "pesca e passa" vuol
                // dire non lanciare niente, non smettere di giocare.
                if (actions != null && actions.CanPass)
                {
                    actions.SubmitPass();
                    return true;
                }
            }

            return false;
        }

        private static bool NoAttacks(DeclareAttackerRequest request)
        {
            // CanSubmit e' il permesso del server a chiudere cosi' com'e'. Se
            // e' falso c'e' una creatura che DEVE attaccare (mustAttack): la
            // dichiarazione vuota verrebbe rifiutata, e allora lasciamo fare
            // all'AI invece di insistere e far scadere il timer.
            if (!request.CanSubmit)
            {
                LogOnce("SparkyBehaviour: attacco obbligato, lascio decidere l'AI.");
                return false;
            }

            request.SubmitAttackers();
            return true;
        }

        private static bool AlwaysAttack(DeclareAttackerRequest request)
        {
            // Due tempi, come la UI: prima si dichiara e il server risponde con
            // una richiesta nuova che ha i bersagli valorizzati, poi su QUELLA
            // si conferma. Non si possono incatenare sullo stesso oggetto.
            if (request.DeclaredAttackers != null && request.DeclaredAttackers.Count > 0)
            {
                if (!request.CanSubmit) return false;
                request.SubmitAttackers();
                return true;
            }

            var recipient = FirstLegalRecipient(request);
            if (recipient == null)
            {
                // Nessun attaccante possibile: non e' un errore, e' un turno in
                // cui non c'e' niente da mandare avanti. Chiudiamo noi, se il
                // server ce lo concede.
                if (!request.CanSubmit) return false;
                request.SubmitAttackers();
                return true;
            }

            request.DeclareAllAttackers(recipient);
            return true;
        }

        /// <summary>
        /// Il bersaglio verso cui mandare tutti: si preferisce il giocatore, e
        /// si ripiega sul primo legale (un planeswalker, o una squadra nei
        /// formati a piu' giocatori).
        /// </summary>
        private static DamageRecipient FirstLegalRecipient(DeclareAttackerRequest request)
        {
            var attackers = request.Attackers;
            if (attackers == null) return null;

            DamageRecipient fallback = null;
            foreach (var attacker in attackers)
            {
                var legal = attacker != null ? attacker.LegalDamageRecipients : null;
                if (legal == null) continue;

                foreach (var recipient in legal)
                {
                    if (recipient == null) continue;
                    if (recipient.Type == DamageRecType.Player) return recipient;
                    if (fallback == null) fallback = recipient;
                }
            }
            return fallback;
        }

        // Un messaggio identico non si ripete piu' di una volta al minuto: il
        // prefix gira a ogni frame, e senza questo una singola condizione
        // ricorrente riempirebbe il log.
        private static readonly Dictionary<string, float> _lastLogged = new Dictionary<string, float>();

        private static void LogOnce(string message)
        {
            float now = Time.unscaledTime;
            float previous;
            if (_lastLogged.TryGetValue(message, out previous) && now - previous < 60f) return;
            _lastLogged[message] = now;
            Plugin.Log.LogInfo(message);
        }
    }
}
