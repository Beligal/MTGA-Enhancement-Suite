using System;
using System.Collections.Generic;
using System.Text;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Il confronto fra due mazzi, carta per carta: per ogni nome, quante
    /// copie in A e quante in B.
    ///
    /// **Non contiene Unity e non contiene tipi di Arena**, di proposito
    /// (regola 44 di <c>MISTAKES.md</c>): riceve coppie chiave/nome/numero e
    /// restituisce righe, quindi si prova da un programma fuori dal gioco. Chi
    /// legge i mazzi e raggruppa le stampe e' <see cref="DeckComparer"/>.
    ///
    /// La chiave e' quella del raggruppamento per nome di
    /// <c>DeckImportManager.GroupByName</c>: due stampe diverse della stessa
    /// carta sono la stessa riga. Senza, due mazzi con le stesse otto Foreste
    /// ma illustrazioni diverse risulterebbero diversi.
    /// </summary>
    internal static class DeckDiff
    {
        /// <summary>Una carta di un mazzo, gia' raggruppata per nome.</summary>
        internal struct Line
        {
            public ulong Key;
            public string Name;
            public uint Count;

            public Line(ulong key, string name, uint count)
            {
                Key = key; Name = name; Count = count;
            }
        }

        internal sealed class Row
        {
            public string Name;
            public uint A;
            public uint B;
            public bool Same => A == B;
        }

        internal sealed class Section
        {
            public string Title;
            /// <summary>Prima le righe diverse, poi le uguali; ciascun gruppo in ordine alfabetico.</summary>
            public List<Row> Rows = new List<Row>();
            public uint TotalA;
            public uint TotalB;
            /// <summary>Copie presenti in tutti e due: la somma, riga per riga, del minore dei due numeri.</summary>
            public uint Shared;
            public int SameRows;
            public int DifferentRows;
            public bool IsEmpty => TotalA == 0 && TotalB == 0;
        }

        public static Section Compare(string title, IEnumerable<Line> a, IEnumerable<Line> b)
        {
            var section = new Section { Title = title };

            // L'ordine d'arrivo conta solo per il nome: se una carta e' in
            // tutti e due i mazzi, il nome e' quello letto per primo.
            var byKey = new Dictionary<ulong, Row>();
            var order = new List<Row>();

            Accumulate(a, byKey, order, isA: true);
            Accumulate(b, byKey, order, isA: false);

            foreach (var row in order)
            {
                // Una riga a zero da tutte e due le parti non e' una carta del
                // mazzo: puo' arrivare solo da una voce con quantita' 0, che
                // il raggruppamento gia' scarta. Si scarta anche qui, perche'
                // questa funzione non deve dipendere da chi la chiama.
                if (row.A == 0 && row.B == 0) continue;

                section.Rows.Add(row);
                section.TotalA += row.A;
                section.TotalB += row.B;
                section.Shared += Math.Min(row.A, row.B);
                if (row.Same) section.SameRows++;
                else section.DifferentRows++;
            }

            section.Rows.Sort(CompareRows);
            return section;
        }

        private static void Accumulate(IEnumerable<Line> lines, Dictionary<ulong, Row> byKey,
                                       List<Row> order, bool isA)
        {
            if (lines == null) return;
            foreach (var line in lines)
            {
                Row row;
                if (!byKey.TryGetValue(line.Key, out row))
                {
                    row = new Row { Name = string.IsNullOrEmpty(line.Name) ? "?" : line.Name };
                    byKey[line.Key] = row;
                    order.Add(row);
                }

                // Somma e non assegna: se lo stesso nome arrivasse due volte
                // dalla stessa parte, le copie sono comunque tutte nel mazzo.
                if (isA) row.A += line.Count;
                else row.B += line.Count;
            }
        }

        private static int CompareRows(Row x, Row y)
        {
            if (x.Same != y.Same) return x.Same ? 1 : -1;
            int byName = string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
            if (byName != 0) return byName;
            // Due carte diverse con lo stesso nome visualizzato possono
            // esistere (stampe sconosciute al database): l'ordine resta
            // comunque stabile da un'apertura all'altra.
            int byA = y.A.CompareTo(x.A);
            return byA != 0 ? byA : y.B.CompareTo(x.B);
        }

        /// <summary>
        /// Il confronto come testo semplice, da incollare dove si vuole. Le
        /// colonne sono allineate con gli spazi, quindi si leggono bene in un
        /// carattere a larghezza fissa (un blocco di codice su Discord) e
        /// restano comprensibili altrove.
        ///
        /// I nomi dei mazzi devono arrivare gia' senza i tag di stile.
        /// </summary>
        public static string ToText(string nameA, string nameB, IList<Section> sections, bool includeSame)
        {
            var sb = new StringBuilder();
            sb.Append(nameA).Append(" vs ").Append(nameB).Append('\n');

            int nameWidth = 0;
            int numberWidth = 1;
            foreach (var s in sections)
            {
                if (s == null || s.IsEmpty) continue;
                nameWidth = Math.Max(nameWidth, s.Title.Length);
                numberWidth = Math.Max(numberWidth, Math.Max(s.TotalA, s.TotalB).ToString().Length);
                foreach (var r in s.Rows)
                    if (includeSame || !r.Same) nameWidth = Math.Max(nameWidth, r.Name.Length);
            }

            foreach (var s in sections)
            {
                if (s == null || s.IsEmpty) continue;

                // Come nella finestra: la pila si apre con il suo nome e i
                // totali, poi le carte.
                sb.Append('\n');
                AppendLine(sb, s.Title, s.TotalA, s.TotalB, nameWidth, numberWidth);
                bool any = false;
                foreach (var r in s.Rows)
                {
                    if (!includeSame && r.Same) continue;
                    AppendLine(sb, r.Name, r.A, r.B, nameWidth, numberWidth);
                    any = true;
                }
                if (!any) sb.Append("(no differences)\n");
            }

            return sb.ToString();
        }

        private static void AppendLine(StringBuilder sb, string name, uint a, uint b, int nameWidth, int numberWidth)
        {
            sb.Append(name.PadRight(nameWidth))
              .Append("  ").Append(a.ToString().PadLeft(numberWidth))
              .Append("  ").Append(b.ToString().PadLeft(numberWidth))
              .Append('\n');
        }
    }
}
