using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MTGAEnhancementSuite.Features.CollectionExport
{
    /// <summary>Il file prodotto da un profilo, con il conto di cosa c'e' dentro e cosa no.</summary>
    internal sealed class ProfileOutput
    {
        public string Text;
        public int DataLines;
        public int CopiesWritten;

        /// <summary>Righe che questo formato non sa rappresentare (es. senza set per ManaBox).</summary>
        public readonly List<ExportRow> Rejected = new List<ExportRow>();
        public string RejectReason;

        /// <summary>Righe scritte, ma con un dato che il sito potrebbe non riconoscere.</summary>
        public int CopiesWithCaveat;
        public string CaveatReason;
    }

    /// <summary>
    /// Un formato di file. Riceve il modello comune e fa solo le proprie
    /// trasformazioni: nomi delle colonne, convenzioni dei valori, cosa non
    /// puo' rappresentare.
    ///
    /// L'aggregazione sta qui e non nel modello: due righe del modello sono
    /// due stampe diverse, ma un formato che non ha la colonna che le
    /// distingue le scriverebbe uguali. Il writer le unisce solo se TUTTE le
    /// celle tranne la quantita' coincidono, cioe' se nel file sarebbero
    /// davvero la stessa riga.
    /// </summary>
    internal abstract class ExportProfile
    {
        public abstract string Id { get; }
        public abstract string DisplayName { get; }
        /// <summary>Una riga per l'interfaccia: a cosa serve e come si importa.</summary>
        public abstract string Description { get; }
        public abstract string FileName { get; }
        public virtual string FileFilter => "CSV files (*.csv)\0*.csv\0All files (*.*)\0*.*\0\0";
        public virtual string Extension => "csv";

        protected abstract string[] Header { get; }
        protected abstract int QuantityColumn { get; }
        protected abstract string[] Cells(ExportRow r);

        /// <summary>Non null = la riga non si puo' scrivere in questo formato.</summary>
        protected virtual string Reject(ExportRow r) => null;

        /// <summary>Non null = la riga si scrive, ma con un dato incerto per il sito.</summary>
        protected virtual string Caveat(ExportRow r) => null;

        public virtual ProfileOutput Write(CollectionExportModel model)
        {
            var output = new ProfileOutput();
            var merged = new List<string[]>();
            var quantities = new List<int>();
            var byCells = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var r in model.Rows)
            {
                if (r.Quantity <= 0) continue;
                var reject = Reject(r);
                if (reject != null) { output.Rejected.Add(r); output.RejectReason = reject; continue; }

                var caveat = Caveat(r);
                if (caveat != null) { output.CopiesWithCaveat += r.Quantity; output.CaveatReason = caveat; }

                var cells = Cells(r);
                cells[QuantityColumn] = "";
                var key = string.Join("\u001f", cells);
                if (byCells.TryGetValue(key, out int at)) { quantities[at] += r.Quantity; continue; }
                byCells[key] = merged.Count;
                merged.Add(cells);
                quantities.Add(r.Quantity);
            }

            var sb = new StringBuilder();
            if (Header != null) WriteLine(sb, Header);
            for (int i = 0; i < merged.Count; i++)
            {
                merged[i][QuantityColumn] = quantities[i].ToString(CultureInfo.InvariantCulture);
                WriteLine(sb, merged[i]);
                output.CopiesWritten += quantities[i];
            }
            output.DataLines = merged.Count;
            output.Text = sb.ToString();
            return output;
        }

        // ------------------------------------------------------------------
        // CSV (RFC 4180): virgola, CRLF, doppi apici attorno ai campi che
        // contengono virgole, apici, a capo o spazi ai bordi; l'apice interno
        // si raddoppia. Il file si scrive in UTF-8 SENZA BOM: col BOM la prima
        // intestazione diventa "﻿Count" e un importatore che confronta i
        // nomi esatti non trova la colonna.

        protected static void WriteLine(StringBuilder sb, string[] cells)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Escape(cells[i]));
            }
            sb.Append("\r\n");
        }

        internal static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool quote = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                         || s[0] == ' ' || s[s.Length - 1] == ' ';
            return quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        // ------------------------------------------------------------------

        public static readonly ExportProfile Moxfield = new MoxfieldProfile();
        public static readonly ExportProfile ManaBox = new ManaBoxProfile();
        public static readonly ExportProfile MtgGoldfish = new MtgGoldfishProfile();
        public static readonly ExportProfile Generic = new GenericProfile();
        public static readonly ExportProfile NameList = new NameListProfile();

        /// <summary>In ordine di menu. Il primo e' quello predefinito.</summary>
        public static readonly IReadOnlyList<ExportProfile> All =
            new[] { Moxfield, ManaBox, MtgGoldfish, Generic, NameList };

        internal const string NeedsSetReason = "no identified set (this format requires one)";
    }

    /// <summary>
    /// Moxfield, e Archidekt col suo preset "Moxfield".
    ///
    /// Intestazione: quella che Moxfield scrive nei suoi export, che e' anche
    /// cio' su cui e' costruito il preset di Archidekt. La guida di Moxfield
    /// (aggiornata al 2023) dice che l'ordine non conta, che solo Name e'
    /// obbligatorio, che Edition e' il codice Scryfall, che Foil vuoto =
    /// non foil, e che Alter e Playtest vuoti = falso; la lingua vuota vale
    /// inglese. Per questo tutto cio' che Arena non sa (condizione, lingua,
    /// scambi, prezzo, foil) resta VUOTO: nessun valore inventato.
    /// </summary>
    internal sealed class MoxfieldProfile : ExportProfile
    {
        public override string Id => "moxfield";
        public override string DisplayName => "Moxfield / Archidekt";
        public override string Description =>
            "Moxfield: Collection > Import, format \"Moxfield\", location \"Arena\". Archidekt: Collection > Import, preset \"Moxfield\".";
        public override string FileName => "MTGA-Collection-Moxfield.csv";

        private static readonly string[] H =
        {
            "Count", "Tradelist Count", "Name", "Edition", "Condition", "Language", "Foil",
            "Tags", "Last Modified", "Collector Number", "Alter", "Proxy", "Purchase Price",
        };
        protected override string[] Header => H;
        protected override int QuantityColumn => 0;

        protected override string[] Cells(ExportRow r) => new[]
        {
            "", "", r.Name, r.SetCode ?? "", "", "", "",
            "", "", r.CollectorNumber ?? "", "", "", "",
        };

        protected override string Caveat(ExportRow r) =>
            r.Precision == MatchPrecision.NameOnly ? "name only: the site will pick a printing" : null;
    }

    /// <summary>
    /// ManaBox. Le colonne sono quelle della sua guida all'import; nome e set
    /// sono obbligatori, e lo Scryfall ID puo' sostituirli. Le righe senza set
    /// identificato non si possono scrivere: un codice di Arena non e' un
    /// codice Scryfall, e metterlo li' sarebbe un'informazione inventata.
    /// </summary>
    internal sealed class ManaBoxProfile : ExportProfile
    {
        public override string Id => "manabox";
        public override string DisplayName => "ManaBox";
        public override string Description =>
            "ManaBox app: Collection > Import > CSV. Matched by Scryfall ID, set code and collector number.";
        public override string FileName => "MTGA-Collection-ManaBox.csv";

        private static readonly string[] H =
            { "Name", "Set code", "Set name", "Collector number", "Quantity", "Foil", "Scryfall ID" };
        protected override string[] Header => H;
        protected override int QuantityColumn => 4;

        // "normal" e' il valore che ManaBox scrive per le carte non foil nei
        // propri export. Arena non ha foil fisici: gli stili cosmetici NON
        // sono foil.
        protected override string[] Cells(ExportRow r) => new[]
            { r.Name, r.SetCode, r.SetName ?? "", r.CollectorNumber ?? "", "", "normal", r.ScryfallId ?? "" };

        protected override string Reject(ExportRow r) =>
            r.Precision == MatchPrecision.NameOnly ? NeedsSetReason : null;
    }

    /// <summary>
    /// MTGGoldfish: <c>Card,Set ID,Set Name,Quantity,Foil,Variation</c>.
    ///
    /// Set ID e' il codice **MTGO**, che Scryfall dichiara per set (campo
    /// <c>mtgo_code</c>). Dove manca (set solo cartacei, o solo Arena) si usa
    /// il codice Scryfall in maiuscolo e la riga viene segnalata: il Set Name
    /// accanto e' quello ufficiale, ma non sappiamo se Goldfish lo accetta.
    /// Foil "REGULAR" come da guida. Variation vuota: Arena non dice quale
    /// delle varianti di Goldfish sia una stampa, e non la indoviniamo.
    /// Nome: la sola faccia anteriore per le carte a due facce, intero per le
    /// split [inferenza dai mazzi pubblicati su Goldfish, non dalla guida].
    /// </summary>
    internal sealed class MtgGoldfishProfile : ExportProfile
    {
        public override string Id => "mtggoldfish";
        public override string DisplayName => "MTGGoldfish";
        public override string Description =>
            "MTGGoldfish: Collection > Import, format \"MTGGoldfish\". Set codes are MTGO codes where they exist.";
        public override string FileName => "MTGA-Collection-MTGGoldfish.csv";

        private static readonly string[] H = { "Card", "Set ID", "Set Name", "Quantity", "Foil", "Variation" };
        protected override string[] Header => H;
        protected override int QuantityColumn => 3;

        protected override string[] Cells(ExportRow r) => new[]
        {
            r.FrontFaceName,
            !string.IsNullOrEmpty(r.MtgoSetCode) ? r.MtgoSetCode.ToUpperInvariant() : r.SetCode.ToUpperInvariant(),
            r.SetName ?? "", "", "REGULAR", "",
        };

        protected override string Reject(ExportRow r) =>
            r.Precision == MatchPrecision.NameOnly ? NeedsSetReason : null;

        protected override string Caveat(ExportRow r) =>
            string.IsNullOrEmpty(r.MtgoSetCode) ? "set has no MTGO code; Scryfall code used" : null;
    }

    /// <summary>
    /// Il formato nostro, indipendente dai siti: tutto cio' che sappiamo, con
    /// gli id di Arena accanto a quelli di Scryfall e il grado di certezza
    /// della riga. Nessuna colonna di finitura: Arena non ne ha.
    /// </summary>
    internal sealed class GenericProfile : ExportProfile
    {
        public override string Id => "generic";
        public override string DisplayName => "Generic CSV";
        public override string Description =>
            "Every identifier we have, Arena and Scryfall side by side, plus how each row was matched. Good as a backup.";
        public override string FileName => "MTGA-Collection.csv";

        private static readonly string[] H =
        {
            "Quantity", "Name", "Set Code", "Set Name", "Collector Number", "Scryfall ID", "Oracle ID",
            "Arena ID", "Arena Set Code", "Arena Collector Number", "Match", "Digital Only", "Note",
        };
        protected override string[] Header => H;
        protected override int QuantityColumn => 0;

        protected override string[] Cells(ExportRow r)
        {
            var ids = new List<uint>(r.ArenaIds);
            ids.Sort();
            var idText = new StringBuilder();
            foreach (var id in ids)
            {
                if (idText.Length > 0) idText.Append(' ');
                idText.Append(id.ToString(CultureInfo.InvariantCulture));
            }
            return new[]
            {
                "", r.Name, r.SetCode ?? "", r.SetName ?? "", r.CollectorNumber ?? "",
                r.ScryfallId ?? "", r.OracleId ?? "", idText.ToString(),
                r.ArenaSetCode ?? "", r.ArenaCollectorNumber ?? "",
                MatchLabel(r.Precision), r.DigitalOnly ? "true" : "false", r.Note ?? "",
            };
        }

        internal static string MatchLabel(MatchPrecision p)
        {
            switch (p)
            {
                case MatchPrecision.ArenaId: return "arena_id";
                case MatchPrecision.SetAndNumber: return "set+number";
                default: return "name";
            }
        }
    }

    /// <summary>
    /// Il formato di prima: "copie nome", una riga per nome, tutte le stampe
    /// sommate. Resta perche' ha uno scopo suo: incollare la lista in un
    /// deckbuilder che vuole solo nomi. Per la collezione vera c'e' il resto.
    /// </summary>
    internal sealed class NameListProfile : ExportProfile
    {
        public override string Id => "namelist";
        public override string DisplayName => "Name list (.txt)";
        public override string Description =>
            "One line per card name, all printings added up: \"4 Lightning Strike\". For pasting into deckbuilders.";
        public override string FileName => "MTGA-Collection-Names.txt";
        public override string FileFilter => "Text files (*.txt)\0*.txt\0All files (*.*)\0*.*\0\0";
        public override string Extension => "txt";

        protected override string[] Header => null;
        protected override int QuantityColumn => 0;
        protected override string[] Cells(ExportRow r) => new[] { "", r.Name };

        public override ProfileOutput Write(CollectionExportModel model)
        {
            var output = new ProfileOutput();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var r in model.Rows)
            {
                if (r.Quantity <= 0 || string.IsNullOrEmpty(r.Name)) continue;
                if (!counts.ContainsKey(r.Name)) { counts[r.Name] = 0; order.Add(r.Name); }
                counts[r.Name] += r.Quantity;
            }
            order.Sort((a, b) =>
            {
                int c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a, b, StringComparison.Ordinal);
            });
            var sb = new StringBuilder();
            foreach (var name in order)
            {
                // Un nome su una riga: niente a capo dentro, altrimenti la
                // riga successiva diventerebbe una carta.
                sb.Append(counts[name].ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(name.Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
                output.CopiesWritten += counts[name];
            }
            output.DataLines = order.Count;
            output.Text = sb.ToString();
            return output;
        }
    }
}
