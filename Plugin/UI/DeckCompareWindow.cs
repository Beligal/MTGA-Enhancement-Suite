using System;
using System.Collections.Generic;
using MTGAEnhancementSuite.Features;
using MTGAEnhancementSuite.State;
using TMPro;
using UnityEngine;
using static MTGAEnhancementSuite.UI.ListModal;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Il confronto fra due mazzi: per ogni carta, quante copie in A e quante
    /// in B. Si apre dal tasto destro su una tessera ("Compare with..."): il
    /// mazzo cliccato e' A, B si sceglie con lo stesso selettore di "add from
    /// another deck".
    ///
    /// **Differisce dalla colonna di "add from another deck"**, che guarda un
    /// mazzo solo e dice cosa manca a quello aperto: qui le carte che stanno
    /// solo in A compaiono quanto quelle che stanno solo in B.
    ///
    /// Niente riga di intestazione con "A" e "B": l'ordine delle colonne e'
    /// quello dei nomi nel titolo (richiesta dell'utente, 2026-10-10).
    /// Ciascuna pila del mazzo comincia con una riga che porta il suo nome e
    /// i totali; principale sempre, le altre solo se almeno un mazzo ne ha.
    ///
    /// Sola lettura: non tocca nessuno dei due mazzi.
    /// </summary>
    internal static class DeckCompareWindow
    {
        private static readonly Color SectionBackground = new Color(0.22f, 0.24f, 0.36f, 1f);

        // Colonne, in frazioni della riga.
        private const float NameRight = 0.70f;
        private const float ColumnARight = 0.85f;

        private static ModalFrame _frame;
        private static DeckComparer.Result _result;

        /// <summary>Vive per la sessione, come i filtri del selettore.</summary>
        private static bool _hideSame;

        /// <summary>Da chiamare dal menu della tessera: chiede B e poi apre il confronto.</summary>
        public static void PickAndShow(Guid deckA)
        {
            string name, format;
            if (!DeckComparer.TryDescribe(deckA, out name, out format))
            {
                Toast.Warning(LocalDeckStore.IsLocal(deckA)
                    ? "Local decks can't be compared yet"
                    : "Could not read this deck");
                return;
            }

            DeckImportModal.PickDeckToCompare(deckA, name, format, deckB => Show(deckA, deckB));
        }

        public static void Show(Guid deckA, Guid deckB)
        {
            string error;
            var result = DeckComparer.Compare(deckA, deckB, out error);
            if (result == null)
            {
                Toast.Warning(error ?? "Could not compare the two decks");
                return;
            }

            _result = result;
            Render();
        }

        public static void Close()
        {
            _frame?.Destroy();
            _frame = null;
            _result = null;
        }

        // -----------------------------------------------------------------

        private static void Render()
        {
            // **Solo la finestra**, non il risultato: Render() viene richiamato
            // da "Hide identical", e Close() azzererebbe _result prima che lo
            // si legga. E' l'errore di MISTAKES.md #37.
            _frame?.Destroy();

            if (_result == null)
            {
                Plugin.Log.LogError("DeckCompare: Render() senza un confronto da mostrare.");
                Toast.Error("Deck comparison: nothing to show");
                return;
            }

            _frame = ListModal.Open("MTGAES_DeckCompareWindow",
                PlainName(_result.NameA) + "  vs  " + PlainName(_result.NameB),
                richTitle: false,
                onClose: Close);

            foreach (var section in _result.Sections)
                AddSection(_frame.List, section);

            var footer = _frame.Footer;
            Button(footer, "Swap", new Vector2(0.00f, 0f), new Vector2(0.17f, 1f), ButtonIdle,
                () => Show(_result.IdB, _result.IdA));
            Button(footer, "Change...", new Vector2(0.19f, 0f), new Vector2(0.38f, 1f), ButtonIdle,
                ChangeB);
            Button(footer, _hideSame ? "Show identical" : "Hide identical",
                new Vector2(0.40f, 0f), new Vector2(0.62f, 1f),
                _hideSame ? AccentBlue : ButtonIdle,
                () => { _hideSame = !_hideSame; Render(); });
            Button(footer, "Copy", new Vector2(0.64f, 0f), new Vector2(0.81f, 1f), ButtonIdle, Copy);
            Button(footer, "Close", new Vector2(0.83f, 0f), new Vector2(1.00f, 1f),
                new Color(0.25f, 0.25f, 0.32f, 1f), Close);
        }

        private static void AddSection(Transform list, DeckDiff.Section section)
        {
            // Riga d'apertura: nome della pila e totali, nelle stesse colonne
            // delle carte.
            var head = Row(list);
            head.GetComponent<UnityEngine.UI.Image>().color = SectionBackground;
            Cells(head.transform, section.Title, section.TotalA, section.TotalB,
                Color.white, Color.white, Color.white, FontStyles.Bold);

            int hidden = 0;
            foreach (var row in section.Rows)
            {
                if (row.Same && _hideSame) { hidden++; continue; }
                AddCardRow(list, row);
            }

            if (section.IsEmpty)
                Note(list, "Neither deck has cards here.");
            else if (section.DifferentRows == 0 && _hideSame)
                Note(list, "No differences.");

            // Il riassunto in coda: quante copie in comune, quante carte
            // diverse. "In common" conta le COPIE (il minore dei due numeri,
            // riga per riga), le altre due contano le CARTE.
            if (!section.IsEmpty)
            {
                string summary = $"{section.Shared} in common  |  " +
                                 Count(section.DifferentRows, "card differs", "cards differ") + "  |  " +
                                 Count(section.SameRows, "identical", "identical");
                if (hidden > 0) summary += " (hidden)";
                Note(list, summary);
            }

            Spacer(list);
        }

        private static void AddCardRow(Transform list, DeckDiff.Row row)
        {
            var go = Row(list);

            if (row.Same)
            {
                Cells(go.transform, row.Name, row.A, row.B,
                    TextDisabled, TextDisabled, TextDisabled, FontStyles.Normal);
                return;
            }

            // Il numero piu' alto dei due e' quello evidenziato; uno zero si
            // spegne, perche' "non c'e'" deve leggersi diverso da "ce n'e' meno".
            Color colorA = row.A > row.B ? WinGreen : (row.A == 0 ? TextDisabled : TextSecondary);
            Color colorB = row.B > row.A ? WinGreen : (row.B == 0 ? TextDisabled : TextSecondary);
            Cells(go.transform, row.Name, row.A, row.B, TextPrimary, colorA, colorB, FontStyles.Normal);
        }

        private static void Cells(Transform row, string name, uint a, uint b,
                                  Color nameColor, Color colorA, Color colorB, FontStyles style)
        {
            Label(row, name, nameColor, 17, TextAlignmentOptions.MidlineLeft,
                new Vector2(0f, 0f), new Vector2(NameRight, 1f), new Vector4(14, 0, 6, 0), style);
            Label(row, a.ToString(), colorA, 17, TextAlignmentOptions.Center,
                new Vector2(NameRight, 0f), new Vector2(ColumnARight, 1f), Vector4.zero, style);
            Label(row, b.ToString(), colorB, 17, TextAlignmentOptions.Center,
                new Vector2(ColumnARight, 0f), new Vector2(1f, 1f), Vector4.zero, style);
        }

        private static void ChangeB()
        {
            if (_result == null) return;
            var idA = _result.IdA;
            // La finestra resta aperta sotto il selettore (238 contro 240):
            // se si chiude il selettore senza scegliere, il confronto e' ancora li'.
            DeckImportModal.PickDeckToCompare(idA, _result.NameA, _result.FormatA,
                deckB => Show(idA, deckB));
        }

        private static void Copy()
        {
            if (_result == null) return;

            string text = DeckDiff.ToText(PlainName(_result.NameA), PlainName(_result.NameB),
                _result.Sections, includeSame: !_hideSame);
            try
            {
                GUIUtility.systemCopyBuffer = text;
                Toast.Success(_hideSame ? "Differences copied" : "Comparison copied");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DeckCompare: copia negli appunti fallita: {ex.Message}");
                Toast.Error("Could not copy to the clipboard");
            }
        }

        /// <summary>
        /// Il nome senza i tag dell'editor dei nomi. Nel titolo stanno due nomi
        /// di fila, e i tag di uno non vengono mai chiusi: lo stile del primo
        /// colerebbe sul secondo. Negli appunti, poi, i tag sarebbero testo.
        /// </summary>
        private static string PlainName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "(unnamed)";
            string plain;
            List<CharStyle> styles;
            if (StyledName.TryParse(raw, out plain, out styles) && !string.IsNullOrEmpty(plain))
                return plain;
            var stripped = ReplayFiles.StripTags(raw);
            return string.IsNullOrEmpty(stripped) ? raw : stripped;
        }
    }
}
