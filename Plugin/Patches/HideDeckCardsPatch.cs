using System;
using System.Collections.Generic;
using Core.Code.Decks;
using HarmonyLib;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.State;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Nasconde dalla lista delle carte (il "pool" del deck editor) quelle di
    /// cui il mazzo aperto ha gia' il massimo consentito.
    ///
    /// AGGANCIO — <c>DeckBuilderWidgetUtilities.GetDisplayedPool</c>, la
    /// funzione che decide quali carte far vedere. E' l unico punto di tutto
    /// Core.dll che la chiama (verificato sulla decompilazione completa: il
    /// solo chiamante e' <c>DeckBuilderVisualsUpdater.GetCardPoolViewInfo</c>),
    /// quindi qui filtriamo solo cio' che si vede: nessuna logica di gioco
    /// legge questa lista, non si puo' rovinare un mazzo.
    ///
    /// Da notare che NON ci agganciamo alla catena dei filtri normali
    /// (<c>DeckBuilderCardFilterProvider.ApplyFilters</c>): quella lista di
    /// funzioni viene passata identica a pool, mazzo principale e sideboard,
    /// quindi un filtro li' dentro svuoterebbe anche il pannello del mazzo. In
    /// piu' e' una fotografia ricalcolata solo al cambio di filtro, mentre
    /// <c>GetDisplayedPool</c> viene rieseguita a ogni <c>RefreshPoolView</c>,
    /// che Arena chiama su ogni aggiunta e rimozione di carta: la lista si
    /// aggiorna da sola.
    ///
    /// REGOLA — <c>DeckBuilderModel.CanAddCardToMainDeck</c>, cioe' la stessa
    /// che usa Arena per decidere se il "+" e' attivo. Non riscriviamo il
    /// limite a mano perche' quella funzione conosce gia' i casi particolari:
    ///   - il massimo del formato (4, ma 1 nei formati singleton come il Brawl:
    ///     arriva dal server come CardCountRestriction e diventa
    ///     MaxCardsByTitle in FormatManagerCore);
    ///   - <c>AlternateDeckLimit</c> della singola carta, che SCAVALCA il
    ///     limite di formato. Nel database di Arena vale 250 per tutte le terre
    ///     base (anche innevate e Wastes) e per le carte "quante ne vuoi"
    ///     (Relentless Rats, Persistent Petitioners, Dragon's Approach, Rat
    ///     Colony, Shadowborn Apostle, Hare Apparent, Slime Against Humanity,
    ///     Tempest Hawk, Cid), 7 per Seven Dwarves e 9 per Nazgul. Sono quindi
    ///     le uniche che restano visibili anche in Brawl, ed e' giusto cosi'.
    /// Il conteggio e' per NOME e comprende mazzo + zona comandante + il
    /// maggiore fra sideboard e companion, quindi tutte le stampe e le arti
    /// alternative della stessa carta spariscono insieme.
    ///
    /// In constructed <c>_allowUnownedInConstructed</c> e' sempre vero, quindi
    /// il possesso non entra nel conto: una carta che non hai resta visibile
    /// (la puoi mettere nel mazzo e craftarla) finche' il mazzo non ne ha il
    /// massimo. Sparisce solo al massimo raggiunto.
    ///
    /// DOVE NON SI APPLICA: sola lettura, visualizzatore collezione, draft e
    /// sealed, e sideboard durante la partita. In quei casi il "pool" sono le
    /// carte del mazzo o del pool sigillato e nasconderle impedirebbe di
    /// lavorare. Vedi <see cref="ShouldApply"/>.
    ///
    /// Il toggle e' letto a ogni invocazione: spegnendolo si torna al
    /// comportamento originale senza riavviare.
    /// </summary>
    internal static class HideDeckCardsPatch
    {
        private static bool _loggedFailure;

        /// <summary>
        /// Quando e' vero, la prossima ricostruzione della lista scrive nel log
        /// cosa ha esaminato e cosa ha deciso. Si arma all avvio e a ogni
        /// commutazione dell interruttore, e si disarma dopo una riga: serve a
        /// poter dire perche' il filtro NON ha fatto niente (contesto sbagliato,
        /// mazzo vuoto, patch mai chiamata) invece di doverlo indovinare.
        /// </summary>
        private static bool _diagArmed = true;

        public static void Apply(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(DeckBuilderWidgetUtilities), "GetDisplayedPool");
                if (target == null)
                {
                    Plugin.Log.LogWarning("HideDeckCards: DeckBuilderWidgetUtilities.GetDisplayedPool non trovato, patch saltata.");
                    return;
                }

                harmony.Patch(target, postfix: new HarmonyMethod(
                    typeof(HideDeckCardsPatch), nameof(GetDisplayedPool_Postfix)));
                Plugin.Log.LogInfo("HideDeckCards: patch applicata.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"HideDeckCards: patch fallita: {ex.Message}");
            }
        }

        /// <summary>
        /// Ricostruisce subito la lista delle carte. Serve quando si commuta
        /// l interruttore mentre il deck editor e' gia' aperto: senza, il
        /// cambiamento si vedrebbe solo al primo tocco di un filtro.
        /// Fuori dal deck editor il servizio non esiste e non facciamo nulla.
        /// </summary>
        public static void RefreshPoolNow()
        {
            _diagArmed = true;
            try
            {
                // Il servizio esiste anche quando nessun mazzo e' aperto, ma il
                // suo Model e' null e RefreshPoolView ci va dentro subito
                // (GetCardsNeededToFinishDeck) -> NullReference. Verificato dal
                // log del 2026-09-09: il warning usciva commutando
                // l'interruttore fuori dal deck editor.
                var provider = GameReflection.PantryGet<DeckBuilderModelProvider>();
                if (provider == null || provider.Model == null)
                {
                    Plugin.Log.LogInfo("HideDeckCards: nessun mazzo aperto, niente da ridisegnare.");
                    return;
                }

                var visuals = GameReflection.PantryGet<DeckBuilderVisualsUpdater>();
                if (visuals != null) visuals.RefreshPoolView(false, null);
                else Plugin.Log.LogInfo("HideDeckCards: VisualsUpdater non disponibile, la lista si aggiornera' al prossimo filtro.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"HideDeckCards: refresh del pool non riuscito: {ex.Message}");
            }
        }

        private static void GetDisplayedPool_Postfix(
            DeckBuilderContext context,
            DeckBuilderModel model,
            ref IReadOnlyList<CardPrintingQuantity> __result)
        {
            try
            {
                if (!ModSettings.Instance.HideCardsInDeck) return;
                if (context == null || model == null || __result == null) return;

                if (!ShouldApply(context))
                {
                    LogDiagnosticsIfArmed(context, __result.Count, -1);
                    return;
                }

                if (__result.Count == 0)
                {
                    LogDiagnosticsIfArmed(context, 0, 0);
                    return;
                }

                // Il pool contiene una voce per stampa, e piu' stampe
                // condividono lo stesso nome: la risposta e' per nome, quindi
                // la calcoliamo una volta sola per TitleId.
                Dictionary<uint, bool> answerByTitle = null;

                // Allochiamo la lista nuova solo se qualcosa va davvero tolto:
                // a interruttore acceso ma mazzo vuoto non paghiamo nulla.
                List<CardPrintingQuantity> kept = null;

                for (int i = 0; i < __result.Count; i++)
                {
                    var entry = __result[i];
                    var printing = entry?.Printing;

                    bool keep = true;
                    if (printing != null)
                    {
                        if (answerByTitle == null) answerByTitle = new Dictionary<uint, bool>();
                        if (!answerByTitle.TryGetValue(printing.TitleId, out keep))
                        {
                            keep = model.CanAddCardToMainDeck(printing.GrpId);
                            answerByTitle[printing.TitleId] = keep;
                        }
                    }

                    if (keep)
                    {
                        kept?.Add(entry);
                    }
                    else if (kept == null)
                    {
                        kept = new List<CardPrintingQuantity>(__result.Count);
                        for (int j = 0; j < i; j++) kept.Add(__result[j]);
                    }
                }

                LogDiagnosticsIfArmed(context, __result.Count, (kept == null) ? 0 : (__result.Count - kept.Count));
                if (kept != null) __result = kept;
            }
            catch (Exception ex)
            {
                // Un eccezione qui lascia __result com era: al massimo si vede
                // la lista completa, che e' il comportamento di Arena.
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    Plugin.Log.LogError($"HideDeckCards: filtro non applicato: {ex}");
                }
            }
        }

        /// <summary>
        /// Una riga di log per commutazione, scritta FUORI dai rami: dice cosa
        /// e' stato esaminato, non solo cosa e' stato trovato. Con
        /// <paramref name="removed"/> a -1 il filtro non e' stato applicato, e i
        /// flag del contesto dicono quale guardia lo ha fermato.
        /// </summary>
        private static int _lastRemovedLogged = -1;

        private static void LogDiagnosticsIfArmed(DeckBuilderContext context, int poolSize, int removed)
        {
            // Il primo giro dopo l'apertura del deck editor arriva col pool
            // ancora vuoto. Consumare li' l'unica riga di diagnostica vuol dire
            // non vedere mai il caso interessante: e' successo davvero
            // (2026-09-11, "0 carte nascoste su 0"), e il log non poteva piu'
            // dire se il filtro avesse mai nascosto qualcosa. Restiamo armati.
            if (removed >= 0 && poolSize == 0) return;

            // Oltre alla prima riga, ne scriviamo una ogni volta che il numero
            // di carte nascoste cambia: e' li' che si vede il filtro dire "si".
            // Cambia solo quando cambia il mazzo, quindi non allaga il log.
            bool contatoreCambiato = removed != _lastRemovedLogged;
            if (!_diagArmed && !contatoreCambiato) return;

            _diagArmed = false;
            _lastRemovedLogged = removed;
            try
            {
                string esito = (removed < 0)
                    ? "NON applicato (contesto escluso)"
                    : $"applicato: {removed} carte nascoste su {poolSize}";
                Plugin.Log.LogInfo(
                    $"HideDeckCards: {esito} | formato={context.Format?.FormatName ?? "?"} " +
                    $"maxPerNome={context.MaxCardsByTitle} readOnly={context.IsReadOnly} " +
                    $"mode={context.StartingMode} editing={context.IsEditingDeck} " +
                    $"constructed={context.IsConstructed} limited={context.IsLimited} " +
                    $"sideboarding={context.IsSideboarding}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"HideDeckCards: diagnostica non scritta: {ex.Message}");
            }
        }

        /// <summary>
        /// Vero solo quando <c>GetDisplayedPool</c> restituisce davvero il pool
        /// delle carte possedute e si sta costruendo un mazzo constructed.
        /// Le prime due condizioni ricalcano gli stessi rami della funzione
        /// originale: in sola lettura restituisce il mazzo, in
        /// ReadOnlyCollection l intera collezione.
        /// </summary>
        private static bool ShouldApply(DeckBuilderContext context)
        {
            if (context.IsReadOnly) return false;
            if (context.StartingMode == DeckBuilderMode.ReadOnlyCollection) return false;
            if (!context.IsEditingDeck) return false;
            // Constructed comprende il Brawl (MDNEFormatType.Constructed) ed
            // esclude draft e sealed, dove il pool sono le carte pescate.
            if (!context.IsConstructed) return false;
            if (context.IsSideboarding) return false;
            return true;
        }
    }
}
