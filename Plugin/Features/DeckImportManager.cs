using System;
using System.Collections.Generic;
using System.Diagnostics;
using Core.Code.Decks;
using GreClient.CardData;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.UI;
using Wizards.Mtga.Decks;
using Wotc.Mtga.Cards.Database;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Dati per "aggiungi da un altro mazzo": elenco dei mazzi da cui pescare,
    /// lettura del loro contenuto, confronto con il mazzo aperto nel deck
    /// editor, e inserimento delle carte scelte.
    ///
    /// Nessuna chiamata di rete, in nessun punto. Verificato decompilando
    /// SharedClientCore.dll il 2026-09-19:
    /// <c>DeckDataProvider.GetFullDeck(id)</c> restituisce l'oggetto che ha gia'
    /// in memoria (<c>_allDecks</c>) dentro una promise gia' risolta — non
    /// interroga il server. I contenuti di tutti i mazzi vengono scaricati una
    /// volta sola al login (<c>ProcessSummaries</c> -> <c>LoadDeckContents</c>).
    /// Per questo qui usiamo la via sincrona, <c>DecksManager.GetDeck(id)</c>,
    /// che legge lo stesso dizionario senza passare dalla promise.
    ///
    /// <c>DecksManager</c> vive nello scope <c>Environment</c> del Pantry
    /// (PantryServices, dentro il dizionario Environment): sopravvive al cambio
    /// di scena, quindi e' raggiungibile anche dal deck builder, dove
    /// <c>DeckManagerController</c> non e' piu' trovabile.
    /// </summary>
    internal static class DeckImportManager
    {
        /// <summary>Un mazzo dell'elenco di partenza.</summary>
        internal sealed class SourceDeck
        {
            public Guid Id;
            public string Name;
            public string Format;
            public uint MainDeckSize;
            public DateTime LastUpdated;

            /// <summary>Identita' di colore del mazzo: l'OR di quella di tutte le sue carte.</summary>
            public CardColorFlags Colors;
        }

        /// <summary>
        /// Una carta del mazzo di origine, raggruppata per nome.
        ///
        /// Il raggruppamento e' per <c>TitleId</c> e non per <c>GrpId</c> perche'
        /// un mazzo puo' contenere piu' stampe della stessa carta — succede
        /// sempre con le terre base, che hanno un GrpId per illustrazione. Per
        /// chi guarda sono "8 Foresta", non due righe da 4. Le stampe vere
        /// restano in <see cref="Printings"/> cosi' l'inserimento conserva le
        /// illustrazioni del mazzo di origine.
        /// </summary>
        internal sealed class SourceCard
        {
            public uint TitleId;
            public string Name;
            public uint InSource;
            public uint InOpenDeck;
            public List<DeckCardInserter.Entry> Printings = new List<DeckCardInserter.Entry>();
            public bool Selected;

            /// <summary>
            /// Quante copie mancano rispetto al mazzo di origine — cioe' quante
            /// ne aggiunge davvero la spunta.
            ///
            /// Aggiungere <see cref="InSource"/> invece di questa era il difetto
            /// segnalato il 2026-09-19: con 1 copia nel mazzo aperto e 2
            /// nell'origine, se ne ritrovavano 3. L'operazione giusta e'
            /// "portami a pari con l'altro mazzo", non "somma".
            /// </summary>
            public uint Missing => (InOpenDeck >= InSource) ? 0u : (InSource - InOpenDeck);
        }

        // Colori e dimensione di un mazzo, per id. Ricalcolare l'identita' di
        // colore vuol dire una GetCardPrintingById per ogni carta di ogni mazzo:
        // con molti mazzi si sente all'apertura della finestra. La chiave
        // comprende LastUpdated, cosi' un mazzo modificato altrove non resta
        // con i colori vecchi.
        private struct DeckScan
        {
            public DateTime Stamp;
            public uint Size;
            public CardColorFlags Colors;
        }

        private static readonly Dictionary<Guid, DeckScan> _scanCache = new Dictionary<Guid, DeckScan>();

        // -----------------------------------------------------------------
        // Elenco dei mazzi
        // -----------------------------------------------------------------

        /// <summary>Formato del mazzo aperto, o null se non c'e' nessun mazzo aperto.</summary>
        public static string OpenDeckFormat()
        {
            var model = GetOpenModel();
            return model == null ? null : model._deckFormat;
        }

        /// <summary>
        /// I mazzi da cui si puo' pescare, dal piu' recente al piu' vecchio.
        ///
        /// Esclusi: i mazzi precostruiti del gioco (<c>IsNetDeck</c>), quelli
        /// vuoti, e il mazzo che si sta modificando — di quest'ultimo la copia
        /// in memoria e' quella di *prima* delle modifiche in corso, quindi
        /// comparirebbe come "un altro mazzo" con dentro una versione vecchia
        /// di se' stesso.
        ///
        /// Il filtro sul formato NON si applica qui: la lista torna completa e
        /// la UI decide cosa mostrare con <see cref="MatchesFormat"/>. Cosi' i
        /// mazzi si leggono una volta sola e l'interruttore "mostra tutti i
        /// formati" puo' dire quanti ne sta nascondendo.
        /// </summary>
        public static List<SourceDeck> ListDecks()
        {
            var model = GetOpenModel();
            return ListDecks((model != null) ? model._deckId : Guid.Empty,
                             (model != null) ? model._deckFormat : null);
        }

        /// <summary>
        /// Come <see cref="ListDecks()"/>, ma il mazzo da escludere e il
        /// formato di riferimento li dice il chiamante invece di prenderli dal
        /// deck builder.
        ///
        /// Serve al confronto, che si apre dalla **lista** dei mazzi: li' il
        /// deck builder non e' aperto, e non e' verificato che
        /// <c>DeckBuilderModelProvider</c> si svuoti quando lo si chiude — se
        /// restasse il modello dell'ultima sessione, si escluderebbe un mazzo a
        /// caso. Il formato serve solo al conteggio nel log.
        /// </summary>
        public static List<SourceDeck> ListDecks(Guid excludeId, string referenceFormat)
        {
            var result = new List<SourceDeck>();

            var dm = GameReflection.PantryGet<DecksManager>();
            if (dm == null)
            {
                Plugin.Log.LogWarning("DeckImport: DecksManager non disponibile nel Pantry.");
                return result;
            }

            List<Client_Deck> cached;
            try { cached = dm.GetAllCachedDecks(); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckImport: GetAllCachedDecks fallita: {ex.Message}");
                return result;
            }
            if (cached == null) return result;

            Guid openId = excludeId;
            string openFormat = referenceFormat;

            var db = GameReflection.PantryGet<CardDatabase>();
            var watch = Stopwatch.StartNew();
            int skippedNet = 0, otherFormat = 0, skippedEmpty = 0, scanned = 0;

            foreach (var deck in cached)
            {
                var summary = deck?.Summary;
                if (summary == null) continue;
                if (summary.IsNetDeck) { skippedNet++; continue; }
                if (summary.DeckId == openId) continue;

                DeckScan scan;
                if (!_scanCache.TryGetValue(summary.DeckId, out scan) || scan.Stamp != summary.LastUpdated)
                {
                    scan = ScanDeck(deck, db, summary.LastUpdated);
                    _scanCache[summary.DeckId] = scan;
                    scanned++;
                }

                if (scan.Size == 0) { skippedEmpty++; continue; }

                // Contato DOPO gli scarti, altrimenti il numero comprende mazzi
                // che comunque non finiscono nella lista e il log mente.
                if (!SameFormat(openFormat, summary.Format)) otherFormat++;

                result.Add(new SourceDeck
                {
                    Id = summary.DeckId,
                    Name = string.IsNullOrEmpty(summary.Name) ? "(unnamed)" : summary.Name,
                    Format = summary.Format,
                    MainDeckSize = scan.Size,
                    LastUpdated = summary.LastUpdated,
                    Colors = scan.Colors,
                });
            }

            result.Sort((a, b) => b.LastUpdated.CompareTo(a.LastUpdated));
            watch.Stop();

            // Una riga fuori dai rami: dice cosa e' stato esaminato, non solo
            // cosa e' rimasto. Senza il totale di partenza una lista vuota non
            // distingue "nessun mazzo compatibile" da "non ho letto niente".
            // Il tempo c'e' perche' la scansione dei colori e' l'unica parte
            // che puo' costare: se un giorno si sente, il numero e' gia' qui.
            Plugin.Log.LogInfo(
                $"DeckImport: {cached.Count} mazzi in cache -> {result.Count} utilizzabili " +
                $"(precostruiti {skippedNet}, vuoti {skippedEmpty}; di quelli utilizzabili " +
                $"{otherFormat} sono in un formato diverso da '{openFormat ?? "?"}'). " +
                $"Colori calcolati per {scanned} mazzi, {watch.ElapsedMilliseconds} ms in tutto.");

            return result;
        }

        /// <summary>
        /// Somma le copie del mazzo principale e mette insieme l'identita' di
        /// colore di tutte le sue carte. Un solo giro sulla pila: e' l'unico
        /// punto in cui serve toccare ogni carta di ogni mazzo.
        /// </summary>
        private static DeckScan ScanDeck(Client_Deck deck, CardDatabase db, DateTime stamp)
        {
            var scan = new DeckScan { Stamp = stamp, Size = 0, Colors = CardColorFlags.None };

            var pile = Pile(deck, EDeckPile.Main);
            if (pile == null) return scan;

            var provider = db?.CardDataProvider;

            foreach (var card in pile)
            {
                scan.Size += card.Quantity;
                if (provider == null) continue;

                try
                {
                    var printing = provider.GetCardPrintingById(card.Id);
                    if (printing != null) scan.Colors |= printing.ColorIdentityFlags;
                }
                catch
                {
                    // Una stampa che il database non conosce non deve far
                    // sparire il mazzo dalla lista: perde solo il suo colore.
                }
            }

            return scan;
        }

        /// <summary>
        /// Vero se il mazzo e' nello stesso formato di quello aperto.
        ///
        /// Il formato aperto lo passa il chiamante, che lo legge una volta
        /// sola: chiederlo qui dentro vorrebbe dire una lookup nel Pantry per
        /// ogni riga della lista.
        /// </summary>
        public static bool MatchesFormat(SourceDeck deck, string openFormat)
        {
            if (deck == null) return false;
            return SameFormat(openFormat, deck.Format);
        }

        // -----------------------------------------------------------------
        // Contenuto di un mazzo
        // -----------------------------------------------------------------

        /// <summary>
        /// Le carte del mazzo principale di <paramref name="deckId"/>, in ordine
        /// alfabetico, con accanto quante copie ce ne sono gia' nel mazzo
        /// aperto. Lista vuota se il mazzo non e' in cache.
        ///
        /// Nessuna carta e' spuntata di partenza: la scelta e' dell'utente, e
        /// "Select missing" nella finestra fa in un colpo il caso frequente.
        /// </summary>
        public static List<SourceCard> ReadDeck(Guid deckId)
        {
            var result = new List<SourceCard>();

            // Un mazzo che non si legge finisce nell'avviso qui sotto, come
            // prima dell'estrazione: Pile(null) e' null.
            var deck = SavedDeck(deckId);
            var pile = Pile(deck, EDeckPile.Main);
            if (pile == null || pile.Count == 0)
            {
                Plugin.Log.LogWarning($"DeckImport: il mazzo {deckId} non ha carte nel pile principale.");
                return result;
            }

            var db = GameReflection.PantryGet<CardDatabase>();
            var model = GetOpenModel();

            int unknown;
            foreach (var group in GroupByName(pile, db, out unknown))
            {
                var entry = new SourceCard
                {
                    TitleId = group.TitleId,
                    Name = group.Name,
                    InSource = group.Quantity,
                    // Con la stampa della PRIMA voce del gruppo, come prima
                    // dell'estrazione: per nome il conteggio non cambia.
                    InOpenDeck = QuantityInOpenDeck(model, group.FirstPrinting),
                };
                entry.Printings.AddRange(group.Printings);
                result.Add(entry);
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

            Plugin.Log.LogInfo(
                $"DeckImport: mazzo {deckId} letto, {pile.Count} voci -> {result.Count} carte per nome, " +
                $"{unknown} stampe sconosciute al database.");

            return result;
        }

        /// <summary>Una carta di una pila, con tutte le sue stampe sommate.</summary>
        internal sealed class NamedCount
        {
            /// <summary>
            /// Il TitleId quando il database conosce la carta, altrimenti il
            /// GrpId in uno spazio di chiavi separato, cosi' due carte
            /// sconosciute diverse non finiscono nella stessa riga.
            /// </summary>
            public ulong Key;
            public uint TitleId;
            public string Name;
            public uint Quantity;
            /// <summary>La stampa della prima voce incontrata; null se sconosciuta.</summary>
            public CardPrintingData FirstPrinting;
            public List<DeckCardInserter.Entry> Printings = new List<DeckCardInserter.Entry>();
        }

        /// <summary>
        /// Raggruppa una pila per nome (<c>TitleId</c>), nell'ordine in cui le
        /// carte compaiono. Estratto il 2026-10-10 da <see cref="ReadDeck"/>,
        /// che lo faceva in linea, quando e' servito anche al confronto fra
        /// mazzi: due copie dello stesso raggruppamento avrebbero finito per
        /// contare in due modi diversi.
        /// </summary>
        internal static List<NamedCount> GroupByName(List<Client_DeckCard> pile, CardDatabase db, out int unknown)
        {
            var result = new List<NamedCount>();
            unknown = 0;
            if (pile == null) return result;

            var byKey = new Dictionary<ulong, NamedCount>();

            foreach (var card in pile)
            {
                if (card.Quantity == 0) continue;

                CardPrintingData printing = null;
                try { printing = db?.CardDataProvider?.GetCardPrintingById(card.Id); }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"DeckImport: GetCardPrintingById({card.Id}) ha sollevato: {ex.Message}");
                }

                ulong key;
                uint titleId;
                if (printing != null)
                {
                    titleId = printing.TitleId;
                    key = titleId;
                }
                else
                {
                    unknown++;
                    titleId = 0u;
                    key = 0x1_0000_0000UL | card.Id;
                }

                NamedCount entry;
                if (!byKey.TryGetValue(key, out entry))
                {
                    entry = new NamedCount
                    {
                        Key = key,
                        TitleId = titleId,
                        Name = CardName(db, card.Id),
                        FirstPrinting = printing,
                    };
                    byKey[key] = entry;
                    result.Add(entry);
                }

                entry.Quantity += card.Quantity;
                entry.Printings.Add(new DeckCardInserter.Entry(card.Id, card.Quantity));
            }

            return result;
        }

        /// <summary>
        /// Il mazzo salvato, dalla copia in memoria di <c>DecksManager</c>.
        /// Null se non c'e' — succede con i mazzi locali, che Arena non
        /// conosce.
        /// </summary>
        internal static Client_Deck SavedDeck(Guid deckId)
        {
            var dm = GameReflection.PantryGet<DecksManager>();
            if (dm == null) return null;

            try { return dm.GetDeck(deckId); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckImport: GetDeck({deckId}) fallita: {ex.Message}");
                return null;
            }
        }

        // -----------------------------------------------------------------
        // Inserimento
        // -----------------------------------------------------------------

        /// <summary>
        /// Quante copie aggiungerebbe davvero la selezione corrente: la somma
        /// di <see cref="SourceCard.Missing"/>, non di <c>InSource</c>. E' il
        /// numero che la finestra scrive sul pulsante, e deve essere lo stesso
        /// che finisce nel mazzo.
        /// </summary>
        public static uint SelectedCopies(IEnumerable<SourceCard> cards)
        {
            uint total = 0;
            if (cards == null) return 0;
            foreach (var c in cards)
                if (c != null && c.Selected) total += c.Missing;
            return total;
        }

        /// <summary>
        /// Porta il mazzo aperto a pari con quello di origine per le carte
        /// spuntate: aggiunge solo le copie mancanti.
        ///
        /// Le copie si prendono dalle stampe dell'origine nell'ordine in cui
        /// stanno nel mazzo, cosi' l'illustrazione scelta li' viene conservata
        /// finche' basta a coprire quello che manca.
        /// </summary>
        public static void AddSelected(IEnumerable<SourceCard> cards, string sourceDeckName)
        {
            if (cards == null) return;

            var entries = new List<DeckCardInserter.Entry>();
            int picked = 0, alreadyCovered = 0;

            foreach (var card in cards)
            {
                if (card == null || !card.Selected) continue;
                picked++;

                uint toAdd = card.Missing;
                if (toAdd == 0) { alreadyCovered++; continue; }

                foreach (var printing in card.Printings)
                {
                    if (toAdd == 0) break;
                    uint take = Math.Min(toAdd, printing.Count);
                    entries.Add(new DeckCardInserter.Entry(printing.GrpId, take));
                    toAdd -= take;
                }
            }

            if (picked == 0)
            {
                Toast.Warning("No cards selected");
                return;
            }

            if (entries.Count == 0)
            {
                // Selezione fatta solo di carte gia' coperte: non e' un errore,
                // ma dirlo e' meglio che non far succedere niente.
                Toast.Info($"Nothing to add: you already have all {picked} selected card(s)");
                return;
            }

            var outcome = DeckCardInserter.Insert(entries, "Deck import");
            if (!outcome.DeckReady)
            {
                Toast.Warning("Import: no deck open");
                return;
            }

            string label = string.IsNullOrEmpty(sourceDeckName) ? "another deck" : $"'{sourceDeckName}'";

            Plugin.Log.LogInfo(
                $"DeckImport: da {label} selezionate {picked} carte ({alreadyCovered} gia' coperte), " +
                $"{outcome.Added} copie aggiunte, {outcome.Rejected} rifiutate.");
            PerPlayerLog.Info($"Deck import from {label}: +{outcome.Added} cards, {outcome.Rejected} rejected");

            // Stessa forma del messaggio dei preset: la nota sulle terre va in
            // coda a quello di esito, non in un avviso separato, altrimenti i
            // due riquadri si accavallano.
            string landsNote = outcome.AutoLandsTurnedOff
                ? "\nAuto-lands off: the imported cards set the mana base. Reopen the deck to restore it."
                : "";
            float duration = outcome.AutoLandsTurnedOff ? 6f : 4f;

            if (outcome.Added == 0)
                Toast.Warning($"{label}: no cards added ({outcome.Rejected} were already at the copy limit)");
            else if (outcome.Rejected > 0)
                Toast.Info($"{label}: +{outcome.Added} cards, {outcome.Rejected} over the copy limit{landsNote}", duration);
            else
                Toast.Success($"{label}: +{outcome.Added} cards{landsNote}", duration);
        }

        // -----------------------------------------------------------------
        // Utilita'
        // -----------------------------------------------------------------

        private static DeckBuilderModel GetOpenModel()
        {
            var provider = GameReflection.PantryGet<DeckBuilderModelProvider>();
            if (provider == null || provider.Model == null || !provider.Model.HasLoadedDeck) return null;
            return provider.Model;
        }

        /// <summary>
        /// Vero se siamo in un contesto in cui ha senso importare: constructed,
        /// in modifica, non in sola lettura e non durante il sideboarding.
        /// Stesse guardie di <c>HideDeckCardsPatch.ShouldApply</c>, e per lo
        /// stesso motivo: fuori di li' il pool non sono le carte possedute e
        /// <c>CanAddCardToMainDeck</c> rifiuterebbe comunque tutto.
        /// </summary>
        public static bool ContextAllowsImport()
        {
            try
            {
                var provider = GameReflection.PantryGet<DeckBuilderContextProvider>();
                var context = provider?.Context;
                if (context == null) return false;
                if (context.IsReadOnly) return false;
                if (context.StartingMode == DeckBuilderMode.ReadOnlyCollection) return false;
                if (!context.IsEditingDeck) return false;
                if (!context.IsConstructed) return false;
                if (context.IsSideboarding) return false;
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckImport: contesto non leggibile ({ex.Message}), importazione disattivata.");
                return false;
            }
        }

        /// <summary>
        /// Una delle pile del mazzo. Le pile sono quattro — <c>Main</c>,
        /// <c>Sideboard</c>, <c>CommandZone</c>, <c>Companions</c> — e
        /// <c>Client_DeckContents</c> le crea tutte nel costruttore
        /// (SharedClientCore.dll, decompilato il 2026-10-10); null solo se il
        /// mazzo non ha contenuti.
        /// </summary>
        internal static List<Client_DeckCard> Pile(Client_Deck deck, EDeckPile kind)
        {
            var contents = deck?.Contents;
            if (contents?.Piles == null) return null;
            List<Client_DeckCard> pile;
            return contents.Piles.TryGetValue(kind, out pile) ? pile : null;
        }

        /// <summary>
        /// Quante copie della stessa carta (per nome, quindi tutte le stampe
        /// insieme) ci sono gia' nel mazzo aperto. Se il mazzo aperto non c'e'
        /// o la stampa e' sconosciuta la risposta e' 0, che nella UI si legge
        /// come "non ce l'hai": e' il verso giusto in cui sbagliare, perche'
        /// l'inserimento poi applica comunque il limite vero.
        /// </summary>
        private static uint QuantityInOpenDeck(DeckBuilderModel model, CardPrintingData printing)
        {
            if (model == null || printing == null) return 0;
            try { return model.GetQuantityInMainDeckByTitle(printing); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckImport: GetQuantityInMainDeckByTitle fallita: {ex.Message}");
                return 0;
            }
        }

        private static string CardName(CardDatabase db, uint grpId)
        {
            try
            {
                var titles = db?.CardTitleProvider;
                if (titles != null)
                {
                    string t = titles.GetCardTitle(grpId, null, false);
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            catch { /* il nome e' cosmetico: se non si ricava, resta il numero */ }
            return grpId.ToString();
        }

        private static bool SameFormat(string openFormat, string deckFormat)
        {
            // Senza un formato di riferimento non filtriamo: meglio mostrare
            // tutto che mostrare una lista vuota senza spiegazione.
            if (string.IsNullOrEmpty(openFormat)) return true;
            if (string.IsNullOrEmpty(deckFormat)) return false;
            return string.Equals(openFormat, deckFormat, StringComparison.OrdinalIgnoreCase);
        }
    }
}
