using System;
using System.Collections.Generic;
using MTGAEnhancementSuite.Helpers;
using Wizards.Mtga.Decks;
using Wotc.Mtga.Cards.Database;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Legge due mazzi salvati e li passa a <see cref="DeckDiff"/>, una pila
    /// alla volta.
    ///
    /// **Solo lettura e solo mazzi salvati.** Si apre dalla lista dei mazzi,
    /// non dal deck builder, quindi legge sempre la copia di
    /// <c>DecksManager</c>: e' quella che c'e' anche sul server, e nessuno dei
    /// due mazzi puo' essere "a meta' di una modifica".
    ///
    /// **I mazzi locali restano fuori.** Arena non li conosce, quindi
    /// <c>GetDeck</c> non li trova; l'unico modo di leggerli sarebbe
    /// <c>LocalDeckBridge.BuildClientDeck</c>, che passa dall'importatore di
    /// Arena e **riduce le copie a quelle possedute** — un confronto fatto
    /// cosi' mostrerebbe differenze che nel mazzo non ci sono.
    /// </summary>
    internal static class DeckComparer
    {
        internal sealed class Result
        {
            public Guid IdA;
            public Guid IdB;
            public string NameA;
            public string NameB;
            public string FormatA;
            /// <summary>Sempre il mazzo principale, poi le altre pile solo se almeno un mazzo ne ha.</summary>
            public List<DeckDiff.Section> Sections = new List<DeckDiff.Section>();

            public int DifferentRows
            {
                get
                {
                    int n = 0;
                    foreach (var s in Sections) n += s.DifferentRows;
                    return n;
                }
            }
        }

        private static readonly EDeckPile[] PileOrder =
        {
            EDeckPile.Main, EDeckPile.Sideboard, EDeckPile.CommandZone, EDeckPile.Companions,
        };

        private static string PileTitle(EDeckPile pile)
        {
            switch (pile)
            {
                case EDeckPile.Main: return "Main deck";
                case EDeckPile.Sideboard: return "Sideboard";
                case EDeckPile.CommandZone: return "Commander";
                case EDeckPile.Companions: return "Companion";
                default: return pile.ToString();
            }
        }

        /// <summary>Nome e formato di un mazzo salvato; false se Arena non lo conosce.</summary>
        public static bool TryDescribe(Guid deckId, out string name, out string format)
        {
            name = null;
            format = null;
            var deck = DeckImportManager.SavedDeck(deckId);
            if (deck?.Summary == null) return false;
            name = string.IsNullOrEmpty(deck.Summary.Name) ? "(unnamed)" : deck.Summary.Name;
            format = deck.Summary.Format;
            return true;
        }

        /// <summary>
        /// Il confronto, o null con <paramref name="error"/> gia' pronto per
        /// l'utente.
        /// </summary>
        public static Result Compare(Guid idA, Guid idB, out string error)
        {
            error = null;

            if (idA == idB)
            {
                error = "Pick a different deck to compare with";
                return null;
            }

            var deckA = DeckImportManager.SavedDeck(idA);
            var deckB = DeckImportManager.SavedDeck(idB);
            if (deckA?.Summary == null || deckB?.Summary == null)
            {
                bool local = LocalDeckStore.IsLocal(idA) || LocalDeckStore.IsLocal(idB);
                error = local ? "Local decks can't be compared yet"
                              : "Could not read one of the two decks";
                Plugin.Log.LogWarning(
                    $"DeckCompare: lettura fallita (A {idA}: {(deckA != null ? "ok" : "null")}, " +
                    $"B {idB}: {(deckB != null ? "ok" : "null")}, locale: {local}).");
                return null;
            }

            var db = GameReflection.PantryGet<CardDatabase>();

            var result = new Result
            {
                IdA = idA,
                IdB = idB,
                NameA = string.IsNullOrEmpty(deckA.Summary.Name) ? "(unnamed)" : deckA.Summary.Name,
                NameB = string.IsNullOrEmpty(deckB.Summary.Name) ? "(unnamed)" : deckB.Summary.Name,
                FormatA = deckA.Summary.Format,
            };

            int unknownTotal = 0;
            foreach (var kind in PileOrder)
            {
                int unknownA, unknownB;
                var linesA = Lines(DeckImportManager.Pile(deckA, kind), db, out unknownA);
                var linesB = Lines(DeckImportManager.Pile(deckB, kind), db, out unknownB);
                unknownTotal += unknownA + unknownB;

                var section = DeckDiff.Compare(PileTitle(kind), linesA, linesB);

                // Il principale si mostra sempre, anche vuoto: un mazzo senza
                // carte nel principale e' un fatto da vedere, non da nascondere.
                if (kind == EDeckPile.Main || !section.IsEmpty)
                    result.Sections.Add(section);
            }

            var main = result.Sections[0];
            Plugin.Log.LogInfo(
                $"DeckCompare: '{result.NameA}' ({main.TotalA}) vs '{result.NameB}' ({main.TotalB}): " +
                $"{result.Sections.Count} sezioni, {result.DifferentRows} righe diverse, " +
                $"{main.Shared} copie in comune nel principale, {unknownTotal} stampe sconosciute al database.");

            return result;
        }

        private static List<DeckDiff.Line> Lines(List<Client_DeckCard> pile, CardDatabase db, out int unknown)
        {
            var lines = new List<DeckDiff.Line>();
            foreach (var g in DeckImportManager.GroupByName(pile, db, out unknown))
                lines.Add(new DeckDiff.Line(g.Key, g.Name, g.Quantity));
            return lines;
        }
    }
}
