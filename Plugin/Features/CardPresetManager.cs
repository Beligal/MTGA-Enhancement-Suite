using System;
using System.Collections.Generic;
using Core.Code.Decks;
using GreClient.CardData;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using Wotc.Mtga.Cards.Database;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Salva e applica i preset di carte nel deck editor.
    ///
    /// Percorso usato, ricalcato su quello che Arena stessa usa per il pulsante
    /// "terre automatiche" (<c>AutoLandsToggle</c>):
    ///
    ///     Model.AddCardToMainDeck(grpId, count)   per ogni carta
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
    internal static class CardPresetManager
    {
        /// <summary>true se siamo nel deck editor con un modello caricato.</summary>
        public static bool IsDeckBuilderReady()
        {
            var provider = GameReflection.PantryGet<DeckBuilderModelProvider>();
            return provider != null && provider.Model != null && provider.Model.HasLoadedDeck;
        }

        public static List<CardPreset> All => ModSettings.Instance.CardPresets
                                              ?? (ModSettings.Instance.CardPresets = new List<CardPreset>());

        // ---------- salvataggio ----------

        /// <summary>
        /// Crea un preset dal main deck attualmente aperto. Il nome viene
        /// derivato da quello del mazzo; in caso di collisione aggiunge un
        /// progressivo, cosi salvataggi ripetuti non si sovrascrivono a vicenda.
        /// Restituisce null se il deck editor non e' pronto o il mazzo e' vuoto.
        /// </summary>
        public static CardPreset SaveCurrentDeck()
        {
            var provider = GameReflection.PantryGet<DeckBuilderModelProvider>();
            if (provider == null || provider.Model == null || !provider.Model.HasLoadedDeck)
            {
                Toast.Warning("Presets: no deck open");
                return null;
            }

            var model = provider.Model;
            var cards = new List<PresetCard>();

            // GetFilteredMainDeck() restituisce il mazzo COMPLETO, malgrado il
            // nome. Verificato decompilando Core.dll il 2026-08-28:
            // DeckBuilderModel.ApplyFilterMainDeck applica i filtri, copia il
            // risultato in _appliedFilteredMainDeck, e subito dopo rimette
            // SetFilters(Array.Empty). Quindi _mainDeck resta senza filtri e
            // GetFilteredMainDeck() ignora la barra di ricerca.
            //
            // La lista filtrata, se mai servisse, e' l'altra: AppliedFilteredMainDeck.
            // Non usarla qui: un preset salvato con una ricerca attiva
            // conterrebbe solo le carte visibili in quel momento.
            var main = model.GetFilteredMainDeck();
            if (main != null)
            {
                foreach (var cq in main)
                {
                    if (cq == null || cq.Printing == null || cq.Quantity == 0) continue;
                    cards.Add(new PresetCard(cq.Printing.GrpId, cq.Quantity, SafeName(cq.Printing)));
                }
            }

            if (cards.Count == 0)
            {
                Toast.Warning("Presets: the deck is empty");
                return null;
            }

            string baseName = string.IsNullOrEmpty(model._deckName) ? "Preset" : model._deckName;
            var preset = new CardPreset(UniqueName(baseName)) { Cards = cards };

            All.Add(preset);
            ModSettings.Instance.Save();

            Plugin.Log.LogInfo($"Preset salvato '{preset.Name}': {cards.Count} voci, {preset.TotalCards} copie.");
            PerPlayerLog.Info($"Preset saved '{preset.Name}' ({preset.TotalCards} cards)");
            Toast.Success($"Preset '{preset.Name}' saved ({preset.TotalCards} cards)");
            return preset;
        }

        // ---------- applicazione ----------

        /// <summary>
        /// Inserisce tutte le carte del preset nel main deck aperto. Ogni carta
        /// viene prima validata con <c>CanAddCardToMainDeck</c>, che applica le
        /// stesse regole di possesso e legalita' che valgono per l inserimento
        /// manuale: quelle rifiutate vengono contate e riportate.
        /// </summary>
        public static void Apply(CardPreset preset)
        {
            if (preset == null) return;

            var provider = GameReflection.PantryGet<DeckBuilderModelProvider>();
            if (provider == null || provider.Model == null || !provider.Model.HasLoadedDeck)
            {
                Toast.Warning("Presets: no deck open");
                return;
            }

            var model = provider.Model;
            uint added = 0, rejected = 0;

            // Lo spegnimento del suggerimento terre non merita un avviso suo:
            // un'azione dell'utente, un messaggio. Lo segnamo qui e lo
            // aggiungiamo in coda all'avviso di esito, in fondo al metodo.
            bool autoLandsTurnedOff = false;

            foreach (var card in preset.Cards)
            {
                if (card == null || card.Count == 0) continue;
                for (uint i = 0; i < card.Count; i++)
                {
                    bool ok;
                    try { ok = model.CanAddCardToMainDeck(card.GrpId); }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"Preset: CanAddCardToMainDeck({card.GrpId}) ha sollevato: {ex.Message}");
                        ok = false;
                    }

                    if (!ok) { rejected++; continue; }

                    try { model.AddCardToMainDeck(card.GrpId); added++; }
                    catch (Exception ex)
                    {
                        rejected++;
                        Plugin.Log.LogWarning($"Preset: AddCardToMainDeck({card.GrpId}) fallita: {ex.Message}");
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
                Plugin.Log.LogWarning($"Preset: UpdatePile(MainDeck) fallita: {ex.Message}");
                try { model.UpdateMainDeck(); } catch { }
            }

            // Se il preset ha portato delle terre base, spegniamo il
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
            // *prima* del preset — su un mazzo nuovo, vuoto — e non si trova mai
            // niente. Sbagliato una volta, il 2026-08-29, senza che nulla lo
            // segnalasse: nessun errore, nessun warning, solo l'avviso che non
            // compariva.
            //
            // Vale solo se il preset contiene davvero terre base: uno di sole
            // magie lascia il suggerimento com'era.
            try
            {
                var presetGrpIds = new HashSet<uint>();
                foreach (var c in preset.Cards)
                    if (c != null) presetGrpIds.Add(c.GrpId);

                bool broughtBasicLands = false;
                var mainDeck = model.GetFilteredMainDeck();
                if (mainDeck != null)
                {
                    foreach (var cq in mainDeck)
                    {
                        if (cq?.Printing == null) continue;
                        if (!presetGrpIds.Contains(cq.Printing.GrpId)) continue;
                        if (!cq.Printing.IsBasicLand) continue;
                        broughtBasicLands = true;
                        break;
                    }
                }

                Plugin.Log.LogInfo($"Preset: {mainDeck?.Count ?? 0} voci nel mazzo dopo il refresh, terre base dal preset: {broughtBasicLands}");

                if (broughtBasicLands)
                {
                    var filters = GameReflection.PantryGet<DeckBuilderCardFilterProvider>();
                    if (filters == null)
                    {
                        Plugin.Log.LogWarning("Preset: DeckBuilderCardFilterProvider non disponibile, suggerimento terre non spento.");
                    }
                    else if (!filters.IsAutoSuggestLandsToggleOn)
                    {
                        Plugin.Log.LogInfo("Preset: suggerimento terre gia' spento, niente da fare.");
                    }
                    else
                    {
                        filters.IsAutoSuggestLandsToggleOn = false;
                        autoLandsTurnedOff = true;
                        Plugin.Log.LogInfo("Preset: conteneva terre base, suggerimento automatico delle terre spento per questa sessione.");
                    }
                }
            }
            catch (Exception ex)
            {
                // Non blocchiamo l'applicazione del preset per questo: al
                // massimo Arena ridistribuisce le terre come faceva prima.
                Plugin.Log.LogWarning($"Preset: non ho potuto spegnere il suggerimento terre ({ex.Message})");
            }

            try
            {
                var visuals = GameReflection.PantryGet<DeckBuilderVisualsUpdater>();
                if (visuals != null) visuals.UpdateAllDeckVisuals();
                else Plugin.Log.LogWarning("Preset: DeckBuilderVisualsUpdater non disponibile, la UI potrebbe non aggiornarsi.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Preset: UpdateAllDeckVisuals fallita: {ex.Message}");
            }

            Plugin.Log.LogInfo($"Preset '{preset.Name}' applicato: {added} aggiunte, {rejected} rifiutate.");
            PerPlayerLog.Info($"Preset '{preset.Name}': +{added} cards, {rejected} rejected");

            // Il suffisso sulle terre va in coda al messaggio di esito, non in un
            // avviso separato: due riquadri per un solo click si accavallano e
            // il secondo finisce sotto il primo.
            string landsNote = autoLandsTurnedOff
                ? "\nAuto-lands off: the preset sets the mana base. Reopen the deck to restore it."
                : "";

            // Con la nota sulle terre il messaggio va su due righe e chiede di
            // fare qualcosa: i 4 secondi di default non bastano per leggerlo.
            float duration = autoLandsTurnedOff ? 6f : 4f;

            if (added == 0)
                Toast.Warning($"'{preset.Name}': no cards added ({rejected} rejected)");
            else if (rejected > 0)
                Toast.Info($"'{preset.Name}': +{added} cards, {rejected} rejected{landsNote}", duration);
            else
                Toast.Success($"'{preset.Name}': +{added} cards{landsNote}", duration);
        }

        // ---------- gestione ----------

        public static void Delete(Guid id)
        {
            var list = All;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].Id == id)
                {
                    string name = list[i].Name;
                    list.RemoveAt(i);
                    ModSettings.Instance.Save();
                    Plugin.Log.LogInfo($"Preset eliminato '{name}'.");
                    Toast.Info($"Preset '{name}' deleted");
                    return;
                }
            }
        }

        // ---------- utilita' ----------

        private static string UniqueName(string baseName)
        {
            var list = All;
            bool Taken(string n)
            {
                foreach (var p in list)
                    if (p != null && string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            if (!Taken(baseName)) return baseName;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{baseName} ({i})";
                if (!Taken(candidate)) return candidate;
            }
            return $"{baseName} ({Guid.NewGuid().ToString().Substring(0, 4)})";
        }

        private static string SafeName(CardPrintingData printing)
        {
            try
            {
                var db = GameReflection.PantryGet<CardDatabase>();
                var titles = db?.CardTitleProvider;
                if (titles != null)
                {
                    string t = titles.GetCardTitle(printing.GrpId, null, false);
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            catch { /* il nome e' solo cosmetico: se non si ricava, pazienza */ }
            return printing.GrpId.ToString();
        }
    }
}
