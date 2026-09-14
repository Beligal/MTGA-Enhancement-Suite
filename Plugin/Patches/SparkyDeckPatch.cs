using System;
using System.Collections.Generic;
using AssetLookupTree;
using Core.Code.Familiar;
using HarmonyLib;
using MTGAEnhancementSuite.UI;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Sceglie quale mazzo giochera' Sparky, invece di lasciarlo al sorteggio.
    ///
    /// COME FUNZIONA IL GIOCO — quando avvii una partita contro il bot,
    /// <c>HomePageContentController.Coroutine_PlayBotGame</c> fa due cose in
    /// quest'ordine:
    ///
    ///   1. <c>BotControlManager.SetUpBotTool(botTool, assetLookupSystem)</c>,
    ///      che al suo interno chiama
    ///      <c>botTool.SetDeckHeuristic(config._deckHeuristics.SelectRandom())</c>;
    ///   2. <c>botMatchServiceWrapper.AIBotMatch(mioMazzo, botTool.DeckHeuristic.GUID,
    ///      tipoPartita)</c>.
    ///
    /// Fra le due c'e' la preparazione del NOSTRO mazzo, con attese asincrone.
    /// Un postfix sul punto 1 cade quindi esattamente fra il sorteggio e la
    /// lettura: riassegniamo l'euristica e il resto del gioco prosegue come se
    /// il sorteggio fosse uscito cosi'.
    ///
    /// NON E' UN AGGIRAMENTO. Il GUID dell'euristica e' un parametro che il
    /// client manda al server per progetto: e' il server a costruire il mazzo
    /// di Sparky partendo da quel GUID. Noi scegliamo fra gli stessi valori fra
    /// cui sceglie il client, con la sua stessa funzione pubblica. Sul filo la
    /// richiesta e' identica a quella di un sorteggio fortunato: non esiste un
    /// seed ne' una firma sulla casualita'. Quello che invece si farebbe notare
    /// — e che qui non facciamo mai — sarebbe inventare un GUID fuori dalla
    /// lista che il gioco stesso ci consegna.
    ///
    /// PERCHE' LEGGIAMO LA LISTA VIVA e non i cinque GUID trovati nella
    /// ricerca: quei nomi stanno in un asset Unity, non nel binario, e sono
    /// stati letti una volta sola (2026-08-29). Se WOTC ne aggiunge, ne toglie
    /// o ne rinomina uno, una lista scritta a mano sceglierebbe in silenzio la
    /// cosa sbagliata. Cosi' invece il selettore segue il gioco, e quando un
    /// nome salvato non esiste piu' lo dice e lascia il sorteggio.
    ///
    /// Vale solo per le partite contro il bot (Familiar / NPE). Non tocca il
    /// PvP, che non passa di qui.
    /// Dettagli in research/arena-internals/sparky-decision-logic.md.
    /// </summary>
    internal static class SparkyDeckPatch
    {
        /// <summary>Valore che vuol dire "lascia il sorteggio al gioco".</summary>
        public const string RandomChoice = "random";

        /// <summary>
        /// La scelta corrente. **Vive solo per questa sessione** e non finisce
        /// in settings.json: a ogni riavvio si riparte dal sorteggio. E' una
        /// decisione voluta, non una dimenticanza — e' una funzione da banco di
        /// prova, e ritrovarsi mesi dopo tutte le partite contro lo stesso
        /// Sparky senza ricordare di averlo deciso sarebbe un bug silenzioso.
        /// </summary>
        public static string Choice { get; private set; } = RandomChoice;

        /// <summary>
        /// Vero quando la lista dei mazzi arriva dal gioco e non dal ripiego,
        /// cioe' dopo la prima partita contro il bot di questa sessione.
        /// </summary>
        public static bool HasLiveList => _liveDecks.Count > 0;

        /// <summary>
        /// I nomi visti nell'ultima partita contro il bot. Vuota finche' non ne
        /// avviamo una: prima di allora il selettore gira sulla lista di
        /// ripiego qui sotto.
        /// </summary>
        private static readonly List<string> _liveDecks = new List<string>();

        /// <summary>
        /// Ripiego per far girare il tasto prima che il gioco ci abbia mostrato
        /// la lista vera. Sono i cinque mazzi del tutorial letti il 2026-08-29.
        /// **Non e' una fonte di verita':** serve solo a dare qualcosa da
        /// scorrere. La scelta viene comunque applicata confrontandola con la
        /// lista viva, quindi un nome qui dentro ormai sbagliato puo' al
        /// massimo far ricadere sul sorteggio, mai far scegliere il mazzo
        /// sbagliato.
        /// </summary>
        private static readonly string[] _fallbackDecks =
        {
            "ANB_NPE_White", "ANB_NPE_Blue", "ANB_NPE_Black", "ANB_NPE_Red", "ANB_NPE_Green"
        };

        public static void Apply(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(BotControlManager), "SetUpBotTool");
                if (target == null)
                {
                    Plugin.Log.LogWarning(
                        "SparkyDeck: BotControlManager.SetUpBotTool non trovato, patch saltata.");
                    return;
                }

                harmony.Patch(target, postfix: new HarmonyMethod(
                    typeof(SparkyDeckPatch), nameof(SetUpBotTool_Postfix)));
                Plugin.Log.LogInfo("SparkyDeck: patch applicata.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"SparkyDeck: patch fallita: {ex.Message}");
            }
        }

        /// <summary>
        /// I nomi dei parametri devono restare <c>botTool</c> e
        /// <c>assetLookupSystem</c>: Harmony li inietta per nome, non per
        /// posizione, e rinominarli li farebbe arrivare nulli.
        /// </summary>
        private static void SetUpBotTool_Postfix(BotTool botTool, AssetLookupSystem assetLookupSystem)
        {
            try
            {
                if (botTool == null) return;

                var config = BotControlManager.FetchBotConfig(assetLookupSystem);
                var heuristics = config != null ? config._deckHeuristics : null;
                if (heuristics == null || heuristics.Count == 0)
                {
                    Plugin.Log.LogWarning("SparkyDeck: il gioco non ha dato nessun mazzo, lascio com'e'.");
                    return;
                }

                RememberLiveDecks(heuristics);

                var wanted = Choice;
                var drawn = NameOf(botTool.DeckHeuristic);

                // La riga di log esce SEMPRE, anche quando non cambiamo niente:
                // deve poter dire "ho guardato, e ho scelto di non fare nulla".
                if (string.IsNullOrEmpty(wanted) || wanted == RandomChoice)
                {
                    Plugin.Log.LogInfo(
                        $"SparkyDeck: nessuna scelta impostata, tengo il sorteggio ({drawn}). " +
                        $"Disponibili: {string.Join(", ", _liveDecks.ToArray())}");
                    return;
                }

                for (int i = 0; i < heuristics.Count; i++)
                {
                    var candidate = heuristics[i];
                    if (candidate == null) continue;
                    if (!string.Equals(NameOf(candidate), wanted, StringComparison.OrdinalIgnoreCase)) continue;

                    botTool.SetDeckHeuristic(candidate);
                    Plugin.Log.LogInfo(
                        $"SparkyDeck: sorteggiato {drawn}, sostituito con {wanted} " +
                        $"(GUID {candidate.GUID}).");
                    return;
                }

                Plugin.Log.LogWarning(
                    $"SparkyDeck: il mazzo salvato \"{wanted}\" non esiste piu' fra quelli del " +
                    $"gioco ({string.Join(", ", _liveDecks.ToArray())}): tengo il sorteggio ({drawn}).");
            }
            catch (Exception ex)
            {
                // Non far mai fallire l'avvio di una partita per colpa nostra:
                // senza questo catch un'eccezione qui risalirebbe dentro la
                // coroutine del gioco.
                Plugin.Log.LogError($"SparkyDeck: scelta non applicata: {ex.Message}");
            }
        }

        /// <summary>
        /// I mazzi da mostrare nel menu: quelli veri se li abbiamo gia' visti,
        /// altrimenti il ripiego. Non comprende la voce "sorteggio", che il
        /// menu aggiunge da se'.
        /// </summary>
        public static List<string> AvailableDecks()
        {
            return _liveDecks.Count > 0
                ? new List<string>(_liveDecks)
                : new List<string>(_fallbackDecks);
        }

        /// <summary>Chiamata dal menu quando l'utente clicca una riga.</summary>
        public static void Choose(string deck)
        {
            Choice = string.IsNullOrEmpty(deck) ? RandomChoice : deck;

            var provisional = HasLiveList ? "" : " (lista provvisoria)";
            Toast.Info(Choice == RandomChoice
                ? "Sparky: random deck"
                : "Sparky: " + Label(Choice) + provisional);
            Plugin.Log.LogInfo($"SparkyDeck: scelta impostata su {Choice}.");
        }

        private static void RememberLiveDecks(List<DeckHeuristic> heuristics)
        {
            _liveDecks.Clear();
            for (int i = 0; i < heuristics.Count; i++)
            {
                var name = NameOf(heuristics[i]);
                if (!string.IsNullOrEmpty(name)) _liveDecks.Add(name);
            }
        }

        private static string NameOf(DeckHeuristic heuristic)
        {
            return heuristic == null ? "(nessuno)" : heuristic.name;
        }

        /// <summary>
        /// "ANB_NPE_Green" -> "Green". Serve solo per quello che si legge a
        /// video: la scelta viene sempre tenuta e confrontata col nome intero.
        /// </summary>
        public static string Label(string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return assetName;
            var cut = assetName.LastIndexOf('_');
            return cut >= 0 && cut < assetName.Length - 1 ? assetName.Substring(cut + 1) : assetName;
        }
    }
}
