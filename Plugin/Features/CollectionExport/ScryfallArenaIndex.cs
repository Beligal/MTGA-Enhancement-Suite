using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace MTGAEnhancementSuite.Features.CollectionExport
{
    /// <summary>Una stampa di Scryfall, ridotta ai campi che servono all'export.</summary>
    internal sealed class ScryfallPrint
    {
        public uint ArenaId;            // 0 = Scryfall non lo dichiara
        public string Id;               // Scryfall ID: identifica la STAMPA
        public string OracleId;         // identifica la carta astratta, uguale per tutte le ristampe
        public string Name;             // multifaccia: "Fronte // Retro"
        public string Layout;
        public string Set;              // codice Scryfall, minuscolo
        public string SetName;
        public string MtgoSetCode;      // dal elenco dei set, puo' mancare
        public string CollectorNumber;
        public bool Digital;
    }

    /// <summary>
    /// Il ponte fra gli id di Arena e le stampe di Scryfall.
    ///
    /// Si costruisce da una ricerca Scryfall <c>game:arena</c> (tutte le
    /// stampe giocabili in Arena, con il loro <c>arena_id</c>) e si salva su
    /// disco come TSV: l'export lo legge senza rete. E' **facoltativo**: senza
    /// indice l'export funziona lo stesso, ma solo per nome.
    ///
    /// Due chiavi:
    ///   - per <c>arena_id</c>: l'identificazione esatta;
    ///   - per (set, numero): il ripiego quando Scryfall non ha l'id, accettato
    ///     dal mapper solo se il risultato e' unico e il nome coincide.
    /// </summary>
    internal sealed class ScryfallArenaIndex
    {
        public const string SearchUrl =
            "https://api.scryfall.com/cards/search?q=game%3Aarena&unique=prints&include_extras=true&order=set";
        public const string SetsUrl = "https://api.scryfall.com/sets";

        private const string Magic = "#MTGAPLUS-SCRYFALL-ARENA-INDEX";
        private const int FormatVersion = 1;

        private readonly Dictionary<uint, List<ScryfallPrint>> _byArenaId = new Dictionary<uint, List<ScryfallPrint>>();
        private readonly Dictionary<string, List<ScryfallPrint>> _bySetNumber =
            new Dictionary<string, List<ScryfallPrint>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ScryfallPrint> _all = new List<ScryfallPrint>();

        public DateTime BuiltUtc { get; private set; }
        public int Count => _all.Count;
        public IReadOnlyList<ScryfallPrint> All => _all;

        public ScryfallArenaIndex(IEnumerable<ScryfallPrint> prints, DateTime builtUtc)
        {
            BuiltUtc = builtUtc;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in prints)
            {
                if (p == null || string.IsNullOrEmpty(p.Id)) continue;
                // La stessa stampa due volte (pagine che si sovrappongono se
                // Scryfall si aggiorna durante il download): una sola.
                if (!seen.Add(p.Id)) continue;
                _all.Add(p);
                if (p.ArenaId != 0) Add(_byArenaId, p.ArenaId, p);
                if (!string.IsNullOrEmpty(p.Set) && !string.IsNullOrEmpty(p.CollectorNumber))
                    Add(_bySetNumber, SetNumberKey(p.Set, p.CollectorNumber), p);
            }
        }

        private static void Add<TKey>(Dictionary<TKey, List<ScryfallPrint>> map, TKey key, ScryfallPrint p)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<ScryfallPrint>(1);
            list.Add(p);
        }

        private static string SetNumberKey(string set, string number) => set.Trim() + "|" + number.Trim();

        /// <summary>Le stampe con questo arena_id. Vuoto se nessuna; piu' di una = ambiguo.</summary>
        public IReadOnlyList<ScryfallPrint> ByArenaId(uint arenaId)
        {
            return _byArenaId.TryGetValue(arenaId, out var list) ? (IReadOnlyList<ScryfallPrint>)list : Array.Empty<ScryfallPrint>();
        }

        public IReadOnlyList<ScryfallPrint> BySetNumber(string set, string number)
        {
            if (string.IsNullOrEmpty(set) || string.IsNullOrEmpty(number)) return Array.Empty<ScryfallPrint>();
            return _bySetNumber.TryGetValue(SetNumberKey(set, number), out var list)
                ? (IReadOnlyList<ScryfallPrint>)list : Array.Empty<ScryfallPrint>();
        }

        // ------------------------------------------------------------------
        // Lettura delle risposte di Scryfall

        /// <summary>
        /// Una pagina di <c>/cards/search</c>. <paramref name="nextPage"/> e'
        /// null sull'ultima. Lancia se il JSON non e' una lista di carte: una
        /// pagina d'errore non deve diventare un indice vuoto.
        /// </summary>
        public static List<ScryfallPrint> ParseSearchPage(string json, IDictionary<string, string> mtgoCodes,
                                                          out string nextPage, out int totalCards)
        {
            var root = JObject.Parse(json);
            if ((string)root["object"] != "list")
                throw new FormatException("Scryfall response is not a list: " + (string)root["object"] + " " + (string)root["details"]);

            totalCards = (int?)root["total_cards"] ?? -1;
            nextPage = (bool?)root["has_more"] == true ? (string)root["next_page"] : null;

            var result = new List<ScryfallPrint>();
            if (!(root["data"] is JArray data)) return result;
            foreach (var t in data)
            {
                if (!(t is JObject c)) continue;
                var set = (string)c["set"];
                string mtgo = null;
                if (set != null && mtgoCodes != null) mtgoCodes.TryGetValue(set, out mtgo);

                // oracle_id manca al primo livello per le reversible_card:
                // sta nelle facce. Se le facce non concordano resta null.
                var oracle = (string)c["oracle_id"];
                if (oracle == null && c["card_faces"] is JArray faces && faces.Count > 0)
                {
                    var first = (string)faces[0]["oracle_id"];
                    bool same = first != null;
                    foreach (var f in faces) if ((string)f["oracle_id"] != first) same = false;
                    if (same) oracle = first;
                }

                result.Add(new ScryfallPrint
                {
                    ArenaId = (uint?)c["arena_id"] ?? 0,
                    Id = (string)c["id"],
                    OracleId = oracle,
                    Name = (string)c["name"],
                    Layout = (string)c["layout"],
                    Set = set,
                    SetName = (string)c["set_name"],
                    MtgoSetCode = mtgo,
                    CollectorNumber = (string)c["collector_number"],
                    Digital = (bool?)c["digital"] ?? false,
                });
            }
            return result;
        }

        /// <summary>Da <c>/sets</c>: codice Scryfall -> codice MTGO, solo dove Scryfall lo dichiara.</summary>
        public static Dictionary<string, string> ParseMtgoCodes(string json)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var root = JObject.Parse(json);
            if (!(root["data"] is JArray data)) return map;
            foreach (var t in data)
            {
                var code = (string)t["code"];
                var mtgo = (string)t["mtgo_code"];
                if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(mtgo)) map[code] = mtgo;
            }
            return map;
        }

        // ------------------------------------------------------------------
        // Cache su disco: TSV, una stampa per riga.
        //
        // La prima riga dichiara quante stampe seguono: un file troncato (gioco
        // chiuso a meta' scrittura) non passa per un indice piu' piccolo, viene
        // rifiutato. Lo scrive comunque il chiamante con temporaneo + rinomina.

        public void Save(TextWriter w)
        {
            w.Write(Magic); w.Write('\t');
            w.Write(FormatVersion.ToString(CultureInfo.InvariantCulture)); w.Write('\t');
            w.Write(BuiltUtc.ToString("o", CultureInfo.InvariantCulture)); w.Write('\t');
            w.Write(_all.Count.ToString(CultureInfo.InvariantCulture)); w.Write('\n');
            foreach (var p in _all)
            {
                w.Write(p.ArenaId == 0 ? "" : p.ArenaId.ToString(CultureInfo.InvariantCulture)); w.Write('\t');
                w.Write(Tsv(p.Id)); w.Write('\t');
                w.Write(Tsv(p.OracleId)); w.Write('\t');
                w.Write(Tsv(p.Name)); w.Write('\t');
                w.Write(Tsv(p.Layout)); w.Write('\t');
                w.Write(Tsv(p.Set)); w.Write('\t');
                w.Write(Tsv(p.SetName)); w.Write('\t');
                w.Write(Tsv(p.MtgoSetCode)); w.Write('\t');
                w.Write(Tsv(p.CollectorNumber)); w.Write('\t');
                w.Write(p.Digital ? "1" : "0"); w.Write('\n');
            }
        }

        /// <summary>Rilegge un indice salvato. Lancia <see cref="FormatException"/> se il file non e' integro.</summary>
        public static ScryfallArenaIndex Load(TextReader r)
        {
            var header = r.ReadLine();
            if (header == null) throw new FormatException("empty index file");
            var h = header.Split('\t');
            if (h.Length != 4 || h[0] != Magic) throw new FormatException("not an MTGA+ Scryfall index");
            if (h[1] != FormatVersion.ToString(CultureInfo.InvariantCulture)) throw new FormatException("index version " + h[1]);
            var built = DateTime.Parse(h[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            int declared = int.Parse(h[3], CultureInfo.InvariantCulture);

            var prints = new List<ScryfallPrint>(declared);
            string line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var f = line.Split('\t');
                if (f.Length != 10) throw new FormatException("bad index line " + (prints.Count + 2));
                prints.Add(new ScryfallPrint
                {
                    ArenaId = f[0].Length == 0 ? 0 : uint.Parse(f[0], CultureInfo.InvariantCulture),
                    Id = Null(f[1]), OracleId = Null(f[2]), Name = Null(f[3]), Layout = Null(f[4]),
                    Set = Null(f[5]), SetName = Null(f[6]), MtgoSetCode = Null(f[7]),
                    CollectorNumber = Null(f[8]), Digital = f[9] == "1",
                });
            }
            if (prints.Count != declared)
                throw new FormatException($"index truncated: {prints.Count} of {declared} prints");
            return new ScryfallArenaIndex(prints, built);
        }

        private static string Tsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s) sb.Append(ch == '\t' || ch == '\r' || ch == '\n' ? ' ' : ch);
            return sb.ToString();
        }

        private static string Null(string s) => s.Length == 0 ? null : s;
    }
}
