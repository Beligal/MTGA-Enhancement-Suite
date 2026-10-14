using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MTGAEnhancementSuite.Features.CollectionExport
{
    /// <summary>
    /// Dall'inventario di Arena al modello intermedio.
    ///
    /// Le regole, in ordine:
    ///   1. i token non sono carte collezionabili: fuori, ma contati;
    ///   2. <c>arena_id</c> trovato in Scryfall con UNA stampa: identificazione esatta;
    ///   3. altrimenti, (set Arena in minuscolo, numero) con UNA stampa arena
    ///      di Scryfall e lo stesso nome: identificazione per set e numero.
    ///      Il nome deve coincidere perche' i codici di Arena non sono quelli
    ///      di Scryfall (DAR e' "dom", CONF e' "con"): un codice che per caso
    ///      esiste in Scryfall con un altro significato non deve passare;
    ///   4. altrimenti solo il nome, e la riga lo dichiara;
    ///   5. senza nome: fuori, e contata.
    ///
    /// Nessuna carta sparisce in silenzio: ogni copia letta finisce in una
    /// riga o in <see cref="CollectionExportModel.Skipped"/>.
    /// </summary>
    internal static class CollectionMapper
    {
        public static CollectionExportModel Build(IEnumerable<ArenaCard> inventory, ScryfallArenaIndex index)
        {
            var model = new CollectionExportModel { HadScryfallIndex = index != null };
            var byKey = new Dictionary<string, ExportRow>(StringComparer.Ordinal);

            // Ordine per GrpId: quando due voci finiscono nella stessa riga,
            // i campi "del primo" sono sempre quelli dello stesso.
            foreach (var c in inventory.Where(x => x != null).OrderBy(x => x.GrpId))
            {
                if (c.Count <= 0) { model.NonPositiveEntries++; continue; }
                model.CopiesIn += c.Count;

                var arenaName = CleanArenaName(c.Name, c.IsRebalanced);

                if (c.IsToken)
                {
                    model.Skipped.Add(Skip(c, arenaName, "token, not a collectible card"));
                    continue;
                }

                string why;
                var print = Identify(c, arenaName, index, out var precision, out why);

                if (print == null && string.IsNullOrEmpty(arenaName))
                {
                    model.Skipped.Add(Skip(c, null, c.InArenaDatabase
                        ? "no English name in the Arena card database"
                        : "not in the Arena card database"));
                    continue;
                }

                string key = print != null
                    ? "S|" + print.Id
                    : "N|" + arenaName.ToLowerInvariant() + "|" + (c.ArenaSetCode ?? "") + "|" + (c.CollectorNumber ?? "");

                if (!byKey.TryGetValue(key, out var row))
                {
                    row = new ExportRow
                    {
                        Precision = precision,
                        ArenaSetCode = c.ArenaSetCode,
                        ArenaCollectorNumber = c.CollectorNumber,
                        Note = why,
                    };
                    if (print != null)
                    {
                        row.Name = print.Name ?? arenaName;
                        row.Layout = print.Layout;
                        row.SetCode = print.Set;
                        row.SetName = print.SetName;
                        row.MtgoSetCode = print.MtgoSetCode;
                        row.CollectorNumber = print.CollectorNumber;
                        row.ScryfallId = print.Id;
                        row.OracleId = print.OracleId;
                        row.DigitalOnly = print.Digital;
                    }
                    else
                    {
                        row.Name = arenaName;
                    }
                    byKey[key] = row;
                    model.Rows.Add(row);
                }

                row.Quantity += c.Count;
                if (!row.ArenaIds.Contains(c.GrpId)) row.ArenaIds.Add(c.GrpId);
                row.DigitalOnly |= c.IsDigitalOnly;
                row.Rebalanced |= c.IsRebalanced || (row.Name != null && row.Name.StartsWith("A-", StringComparison.Ordinal));
            }

            model.Rows.Sort(CompareRows);
            model.Skipped.Sort((a, b) => a.GrpId.CompareTo(b.GrpId));
            return model;
        }

        private static ScryfallPrint Identify(ArenaCard c, string arenaName, ScryfallArenaIndex index,
                                              out MatchPrecision precision, out string why)
        {
            precision = MatchPrecision.NameOnly;
            if (index == null) { why = "no Scryfall index"; return null; }

            string idProblem = null;
            var byId = Distinct(index.ByArenaId(c.GrpId));
            if (byId.Count > 0)
            {
                // Anche l'arena_id di Scryfall va controllato col nome: sul
                // database di Arena del 2026-10-13 dieci id puntavano alla
                // carta sbagliata (terre base sfalsate di una, "Goblin
                // Bruiser" -> "Rise from the Grave"). Senza nome da Arena non
                // si puo' controllare, e si tiene cio' che dice Scryfall.
                var named = string.IsNullOrEmpty(arenaName)
                    ? byId
                    : byId.Where(p => NamesMatch(arenaName, p.Name)).ToList();
                if (named.Count == 1)
                {
                    precision = MatchPrecision.ArenaId;
                    why = null;
                    return named[0];
                }
                if (named.Count > 1)
                {
                    // Piu' stampe dichiarano lo stesso arena_id e lo stesso
                    // nome. Si sceglie solo se set e numero di Arena ne
                    // indicano una e una sola.
                    var narrowed = named.Where(p => SameSetNumber(p, c)).ToList();
                    if (narrowed.Count == 1)
                    {
                        precision = MatchPrecision.ArenaId;
                        why = null;
                        return narrowed[0];
                    }
                    why = "ambiguous: " + named.Count + " Scryfall printings share this Arena id";
                    return null;
                }
                idProblem = "Scryfall's record for this Arena id is a different card (" + byId[0].Name + ")";
            }

            var bySet = Distinct(index.BySetNumber(c.ArenaSetCode?.ToLowerInvariant(), c.CollectorNumber));
            if (bySet.Count == 1 && !string.IsNullOrEmpty(arenaName) && NamesMatch(arenaName, bySet[0].Name))
            {
                precision = MatchPrecision.SetAndNumber;
                why = null;
                return bySet[0];
            }

            why = idProblem
                ?? (bySet.Count > 1
                    ? "ambiguous: several Scryfall printings with this set and number"
                    : "Arena id not found in the Scryfall index");
            return null;
        }

        private static List<ScryfallPrint> Distinct(IReadOnlyList<ScryfallPrint> prints)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<ScryfallPrint>(prints.Count);
            foreach (var p in prints) if (seen.Add(p.Id)) list.Add(p);
            return list;
        }

        private static bool SameSetNumber(ScryfallPrint p, ArenaCard c)
        {
            return string.Equals(p.Set, c.ArenaSetCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.CollectorNumber, c.CollectorNumber, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Il nome di Arena coincide con quello di Scryfall, o con una delle
        /// sue facce (Arena da' alle doppia faccia il nome del fronte).
        /// </summary>
        internal static bool NamesMatch(string arenaName, string scryfallName)
        {
            if (string.IsNullOrEmpty(arenaName) || string.IsNullOrEmpty(scryfallName)) return false;
            if (string.Equals(arenaName, scryfallName, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var face in scryfallName.Split(new[] { " // " }, StringSplitOptions.None))
                if (string.Equals(arenaName, face, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ------------------------------------------------------------------
        // Nomi

        private static readonly Regex TagRx = new Regex(@"<[^>]*>", RegexOptions.Compiled);
        private static readonly Regex SpaceRx = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex ArenaSplitRx = new Regex(@"\s*///\s*", RegexOptions.Compiled);

        /// <summary>
        /// Il nome di Arena nella forma che usano Scryfall e i siti:
        ///   - via i tag di TextMeshPro (<c>&lt;nobr&gt;</c>, lo sprite "A" di Alchemy, colori);
        ///   - " /// " (separatore delle split card in Arena) diventa " // ";
        ///   - le carte ribilanciate prendono il prefisso "A-", che e' come
        ///     Scryfall le chiama. Senza, "A-Demilich" e "Demilich" finivano
        ///     nella stessa riga, ed e' la carta sbagliata.
        /// </summary>
        internal static string CleanArenaName(string raw, bool rebalanced)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var s = TagRx.Replace(raw, "");
            s = SpaceRx.Replace(s, " ").Trim();
            s = ArenaSplitRx.Replace(s, " // ");
            if (s.Length == 0) return null;
            if (rebalanced && !s.StartsWith("A-", StringComparison.Ordinal)) s = "A-" + s;
            return s;
        }

        private static SkippedCard Skip(ArenaCard c, string name, string reason)
        {
            return new SkippedCard { GrpId = c.GrpId, Count = c.Count, Name = name, Reason = reason };
        }

        // ------------------------------------------------------------------
        // Ordinamento: nome, set, numero (naturale), poi gli id. Totale,
        // quindi due export della stessa collezione sono identici byte per byte.

        internal static int CompareRows(ExportRow a, ExportRow b)
        {
            int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            if (c != 0) return c;
            c = string.Compare(a.SetCode ?? "", b.SetCode ?? "", StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = CompareCollectorNumbers(a.CollectorNumber, b.CollectorNumber);
            if (c != 0) return c;
            c = string.Compare(a.ScryfallId ?? "", b.ScryfallId ?? "", StringComparison.Ordinal);
            if (c != 0) return c;
            c = string.Compare(a.ArenaSetCode ?? "", b.ArenaSetCode ?? "", StringComparison.Ordinal);
            if (c != 0) return c;
            c = CompareCollectorNumbers(a.ArenaCollectorNumber, b.ArenaCollectorNumber);
            if (c != 0) return c;
            uint ia = a.ArenaIds.Count > 0 ? a.ArenaIds.Min() : 0;
            uint ib = b.ArenaIds.Count > 0 ? b.ArenaIds.Min() : 0;
            return ia.CompareTo(ib);
        }

        /// <summary>"9" prima di "10", "10" prima di "10a"; il resto in ordine di testo.</summary>
        internal static int CompareCollectorNumbers(string a, string b)
        {
            a = a ?? ""; b = b ?? "";
            int na = LeadingNumber(a, out int la), nb = LeadingNumber(b, out int lb);
            if (la > 0 && lb > 0 && na != nb) return na.CompareTo(nb);
            if (la > 0 && lb == 0) return -1;
            if (la == 0 && lb > 0) return 1;
            return string.Compare(a, b, StringComparison.Ordinal);
        }

        private static int LeadingNumber(string s, out int digits)
        {
            int n = 0; digits = 0;
            while (digits < s.Length && digits < 9 && char.IsDigit(s[digits]))
            {
                n = n * 10 + (s[digits] - '0');
                digits++;
            }
            return n;
        }
    }
}
