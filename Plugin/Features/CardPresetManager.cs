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

            var entries = new List<DeckCardInserter.Entry>();
            foreach (var card in preset.Cards)
            {
                if (card == null || card.Count == 0) continue;
                entries.Add(new DeckCardInserter.Entry(card.GrpId, card.Count));
            }

            // La sequenza vera (i tre passi di refresh e lo spegnimento del
            // suggerimento terre) vive in DeckCardInserter: la condividiamo con
            // "aggiungi da un altro mazzo", che ha bisogno della stessa identica
            // cosa. I commenti che spiegano il perche' di ogni passo sono li'.
            var outcome = DeckCardInserter.Insert(entries, "Preset");
            if (!outcome.DeckReady)
            {
                Toast.Warning("Presets: no deck open");
                return;
            }

            uint added = outcome.Added, rejected = outcome.Rejected;
            bool autoLandsTurnedOff = outcome.AutoLandsTurnedOff;

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
