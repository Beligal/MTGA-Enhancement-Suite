using System;
using System.Collections.Generic;
using Core.Code.Decks;
using MTGAEnhancementSuite.Helpers;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// L'inserimento di un blocco di carte nel mazzo aperto nel deck editor.
    ///
    /// Estratto da <see cref="CardPresetManager"/> il 2026-09-19, quando la
    /// funzione "aggiungi da un altro mazzo" ha avuto bisogno esattamente
    /// della stessa sequenza. Il codice e' delicato (vedi i commenti sui tre
    /// passi e sul suggerimento terre) e averne due copie da tenere allineate
    /// era il modo piu' rapido per farle divergere: qui c'e' l'unica copia, e
    /// i chiamanti si occupano solo dei propri messaggi.
    ///
    /// Percorso usato, ricalcato su quello che Arena stessa usa per il pulsante
    /// "terre automatiche" (<c>AutoLandsToggle</c>):
    ///
    ///     Model.AddCardToMainDeck(grpId, count)   per ogni carta
    ///     Model.UpdatePile(MainDeck)              una volta sola
    ///     VisualsUpdater.UpdateAllDeckVisuals()   una volta sola alla fine
    ///
    /// Un solo refresh a fine inserimento invece che uno per carta. Non serve
    /// <c>ICardRolloverZoom</c>: quello lo richiede <c>AddCardToDeckPile</c>,
    /// che qui non usiamo.
    ///
    /// Nulla di tutto cio' e' visibile al server: stiamo guidando esattamente
    /// lo stesso codice che percorrono i click dell utente. Al salvataggio del
    /// mazzo parte la normale richiesta di Arena.
    /// </summary>
    internal static class DeckCardInserter
    {
        /// <summary>Una carta da inserire, con quante copie tentarne.</summary>
        internal struct Entry
        {
            public uint GrpId;
            public uint Count;

            public Entry(uint grpId, uint count)
            {
                GrpId = grpId;
                Count = count;
            }
        }

        /// <summary>
        /// Esito dell'inserimento. <see cref="DeckReady"/> falso significa che
        /// non c'era nessun mazzo aperto e non e' stato tentato niente: in quel
        /// caso gli altri campi sono a zero.
        /// </summary>
        internal struct Outcome
        {
            public bool DeckReady;
            public uint Added;
            public uint Rejected;
            public bool AutoLandsTurnedOff;
        }

        /// <summary>true se siamo nel deck editor con un modello caricato.</summary>
        public static bool IsDeckBuilderReady()
        {
            var provider = GameReflection.PantryGet<DeckBuilderModelProvider>();
            return provider != null && provider.Model != null && provider.Model.HasLoadedDeck;
        }

        /// <summary>
        /// Inserisce le carte nel main deck aperto. Ogni copia viene prima
        /// validata con <c>CanAddCardToMainDeck</c>, che applica le stesse
        /// regole di possesso e numero di copie che valgono per l inserimento
        /// manuale: quelle rifiutate vengono contate e riportate.
        ///
        /// <paramref name="source"/> e' solo il prefisso delle righe di log.
        /// </summary>
        public static Outcome Insert(IEnumerable<Entry> entries, string source)
        {
            var outcome = new Outcome();
            if (entries == null) return outcome;
            if (string.IsNullOrEmpty(source)) source = "Insert";

            var provider = GameReflection.PantryGet<DeckBuilderModelProvider>();
            if (provider == null || provider.Model == null || !provider.Model.HasLoadedDeck)
                return outcome;

            var model = provider.Model;
            outcome.DeckReady = true;

            // I grpId inseriti servono ancora dopo il refresh, per decidere se
            // spegnere il suggerimento terre: li raccogliamo qui perche'
            // entries puo' essere un enumerabile consumabile una volta sola.
            var attempted = new HashSet<uint>();

            foreach (var card in entries)
            {
                if (card.Count == 0) continue;
                attempted.Add(card.GrpId);

                for (uint i = 0; i < card.Count; i++)
                {
                    bool ok;
                    try { ok = model.CanAddCardToMainDeck(card.GrpId); }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"{source}: CanAddCardToMainDeck({card.GrpId}) ha sollevato: {ex.Message}");
                        ok = false;
                    }

                    if (!ok) { outcome.Rejected++; continue; }

                    try { model.AddCardToMainDeck(card.GrpId); outcome.Added++; }
                    catch (Exception ex)
                    {
                        outcome.Rejected++;
                        Plugin.Log.LogWarning($"{source}: AddCardToMainDeck({card.GrpId}) fallita: {ex.Message}");
                    }
                }
            }

            // Refresh in DUE passi, entrambi necessari:
            //
            //  1. Model.UpdateMainDeck() -> _mainDeck.ApplyFilters()
            //     AddCardToMainDeck aggiunge solo alla pila grezza; la lista
            //     filtrata che la UI legge resta ferma finche' non si
            //     riapplicano i filtri. Senza questo le carte entrano nel
            //     mazzo ma non si vedono fino al cambio di modalita'.
            //
            //  2. VisualsUpdater.UpdateAllDeckVisuals() -> ridisegna le viste.
            //
            // BasicLandSuggester fa esattamente cosi' (UpdateMainDeck alla fine
            // del suo ciclo), poi AutoLandsToggle aggiunge il passo 2.
            try
            {
                model.UpdatePile(DeckBuilderPile.MainDeck);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"{source}: UpdatePile(MainDeck) fallita: {ex.Message}");
                try { model.UpdateMainDeck(); } catch { }
            }

            // Se l'inserimento ha portato delle terre base, spegniamo il
            // suggerimento automatico delle terre.
            //
            // Perche' serve: Arena lo fa gia' da sola quando l'utente cambia a
            // mano la quantita' di una terra base
            // (DeckBuilderCardFilterProvider.OnTitleInPileChangedQuantity:
            // "PrintingIsBasicLandOrWaste -> IsAutoSuggestLandsToggleOn = false").
            // Il ragionamento e' che chi ha messo le terre di suo pugno sa cosa
            // vuole. Ma quell'evento scatta sui cambi passati dalla UI, e
            // AddCardToMainDeck scrive dritto nella pila grezza
            // (_mainDeck.AddByGrpId): Arena non si accorge di niente, il
            // suggerimento resta acceso e alla prima occasione ricalcola le
            // basi. Bastava aggiungere una carta qualunque, o il comandante —
            // UpdatePile(Commander) chiama SuggestLand() proprio cosi'.
            //
            // DEVE STARE QUI, DOPO UpdatePile. Prima non funziona: la lista che
            // GetFilteredMainDeck() restituisce e' quella filtrata, e resta
            // ferma a com'era finche' UpdatePile non riapplica i filtri (vedi il
            // commento qui sopra). Messo prima, si legge il mazzo com'era
            // *prima* dell'inserimento — su un mazzo nuovo, vuoto — e non si
            // trova mai niente. Sbagliato una volta, il 2026-08-29, senza che
            // nulla lo segnalasse: nessun errore, nessun warning, solo l'avviso
            // che non compariva.
            //
            // Vale solo se sono entrate davvero terre base: un blocco di sole
            // magie lascia il suggerimento com'era.
            try
            {
                bool broughtBasicLands = false;
                var mainDeck = model.GetFilteredMainDeck();
                if (mainDeck != null)
                {
                    foreach (var cq in mainDeck)
                    {
                        if (cq?.Printing == null) continue;
                        if (!attempted.Contains(cq.Printing.GrpId)) continue;
                        if (!cq.Printing.IsBasicLand) continue;
                        broughtBasicLands = true;
                        break;
                    }
                }

                Plugin.Log.LogInfo($"{source}: {mainDeck?.Count ?? 0} voci nel mazzo dopo il refresh, terre base inserite: {broughtBasicLands}");

                if (broughtBasicLands)
                {
                    var filters = GameReflection.PantryGet<DeckBuilderCardFilterProvider>();
                    if (filters == null)
                    {
                        Plugin.Log.LogWarning($"{source}: DeckBuilderCardFilterProvider non disponibile, suggerimento terre non spento.");
                    }
                    else if (!filters.IsAutoSuggestLandsToggleOn)
                    {
                        Plugin.Log.LogInfo($"{source}: suggerimento terre gia' spento, niente da fare.");
                    }
                    else
                    {
                        filters.IsAutoSuggestLandsToggleOn = false;
                        outcome.AutoLandsTurnedOff = true;
                        Plugin.Log.LogInfo($"{source}: contenuto terre base, suggerimento automatico delle terre spento per questa sessione.");
                    }
                }
            }
            catch (Exception ex)
            {
                // Non blocchiamo l'inserimento per questo: al massimo Arena
                // ridistribuisce le terre come faceva prima.
                Plugin.Log.LogWarning($"{source}: non ho potuto spegnere il suggerimento terre ({ex.Message})");
            }

            try
            {
                var visuals = GameReflection.PantryGet<DeckBuilderVisualsUpdater>();
                if (visuals != null) visuals.UpdateAllDeckVisuals();
                else Plugin.Log.LogWarning($"{source}: DeckBuilderVisualsUpdater non disponibile, la UI potrebbe non aggiornarsi.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"{source}: UpdateAllDeckVisuals fallita: {ex.Message}");
            }

            return outcome;
        }
    }
}
