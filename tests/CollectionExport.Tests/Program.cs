using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using MTGAEnhancementSuite.Features.CollectionExport;

namespace MTGAEnhancementSuite.Tests
{
    /// <summary>
    /// Banco di prova dell'export della collezione. Esce con 0 se tutto passa,
    /// 1 altrimenti, e scrive OGNI controllo eseguito (non solo quelli falliti):
    /// un banco che non dice cosa ha guardato non si distingue da uno che non
    /// ha guardato niente (regole 19 e 55 di MISTAKES.md).
    /// </summary>
    internal static class Program
    {
        private static int _passed, _failed;
        private static string _section = "";

        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "-download") return Download(args[1]);

            var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            var index = LoadFixtureIndex(fixtures);

            Run("CSV escaping", TestEscaping);
            Run("Arena names", TestArenaNames);
            Run("Scryfall page parsing", () => TestParsing(fixtures, index));
            Run("Index file round trip", () => TestIndexFile(index));
            Run("Empty collection", () => TestEmpty(index));
            Run("Single card", () => TestSingle(index));
            Run("Fixture collection: mapping", () => TestFixtureMapping(index));
            Run("Fixture collection: every profile", () => TestFixtureProfiles(index));
            Run("No Scryfall index", TestWithoutIndex);
            Run("Determinism", () => TestDeterminism(index));
            Run("Large collection", TestLarge);

            if (args.Length == 3 && args[0] == "-real")
                Run("Real data", () => TestRealData(args[1], args[2]));
            else
                Console.WriteLine("\n(real-data check skipped: pass -real <scryfall pages dir> <arena-cards.tsv>)");

            Console.WriteLine($"\n{_passed} checks passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// Il downloader vero, contro Scryfall vero: lo stesso codice che gira
        /// nel gioco. Ci mette un paio di minuti; non fa parte dei test normali.
        /// </summary>
        private static int Download(string path)
        {
            var d = new ScryfallIndexDownloader(path, "MTGAPlus-tests/1.0 (collection export)");
            var sw = Stopwatch.StartNew();
            d.Start();
            string last = null;
            while (d.Running)
            {
                if (d.Status != last) { last = d.Status; Console.WriteLine($"  {sw.Elapsed.TotalSeconds,4:0}s  {last}"); }
                System.Threading.Thread.Sleep(500);
            }
            Console.WriteLine($"  {sw.Elapsed.TotalSeconds,4:0}s  {d.Status}");
            if (!d.Succeeded) return 1;
            var back = ScryfallIndexStore.Load(path);
            Console.WriteLine($"  file {new FileInfo(path).Length / 1024} KB, {back.Count} prints, {back.All.Count(p => p.ArenaId != 0)} with arena_id");
            return back.Count == d.Result.Count ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // infrastruttura

        private static void Run(string name, Action body)
        {
            _section = name;
            Console.WriteLine($"\n== {name}");
            try { body(); }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine($"  FAIL  threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void Check(bool ok, string what)
        {
            if (ok) { _passed++; Console.WriteLine($"  ok    {what}"); }
            else { _failed++; Console.WriteLine($"  FAIL  {what}"); }
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            bool ok = EqualityComparer<T>.Default.Equals(expected, actual);
            Check(ok, ok ? $"{what} = {actual}" : $"{what}: expected <{expected}> got <{actual}>");
        }

        private static ScryfallArenaIndex LoadFixtureIndex(string dir)
        {
            var mtgo = ScryfallArenaIndex.ParseMtgoCodes(File.ReadAllText(Path.Combine(dir, "scryfall-sets.json")));
            var prints = new List<ScryfallPrint>();
            prints.AddRange(ScryfallArenaIndex.ParseSearchPage(
                File.ReadAllText(Path.Combine(dir, "scryfall-search-page1.json")), mtgo, out _, out _));
            prints.AddRange(ScryfallArenaIndex.ParseSearchPage(
                File.ReadAllText(Path.Combine(dir, "scryfall-search-page2.json")), mtgo, out _, out _));
            return new ScryfallArenaIndex(prints, new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));
        }

        private static ArenaCard Card(uint grpId, int count, string name, string set, string cn,
                                      bool digital = false, bool rebalanced = false, bool token = false, bool inDb = true)
        {
            return new ArenaCard
            {
                GrpId = grpId, Count = count, Name = name, ArenaSetCode = set, CollectorNumber = cn,
                IsDigitalOnly = digital, IsRebalanced = rebalanced, IsToken = token, InArenaDatabase = inDb,
            };
        }

        /// <summary>
        /// Lettore CSV indipendente dal writer (RFC 4180): serve a dire che il
        /// file si rilegge nelle celle che volevamo, non solo che "sembra" giusto.
        /// </summary>
        private static List<string[]> ParseCsv(string text)
        {
            var rows = new List<string[]>();
            var cells = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false, any = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (ch == '"') quoted = false;
                    else cell.Append(ch);
                    continue;
                }
                if (ch == '"' && cell.Length == 0) { quoted = true; any = true; }
                else if (ch == ',') { cells.Add(cell.ToString()); cell.Clear(); any = true; }
                else if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    cells.Add(cell.ToString()); cell.Clear();
                    rows.Add(cells.ToArray()); cells.Clear();
                    i++; any = false;
                }
                else if (ch == '\n' || ch == '\r') throw new FormatException($"bare line break at {i}");
                else { cell.Append(ch); any = true; }
            }
            if (quoted) throw new FormatException("unterminated quote");
            if (any || cell.Length > 0) throw new FormatException("last line has no CRLF");
            return rows;
        }

        /// <summary>Le verifiche strutturali che valgono per OGNI file CSV prodotto.</summary>
        private static List<string[]> CheckCsv(ExportProfile profile, ProfileOutput output, string[] header, string label)
        {
            var rows = ParseCsv(output.Text);
            Check(rows.Count >= 1, $"{label}: has a header line");
            Check(rows.Count > 0 && rows[0].SequenceEqual(header), $"{label}: header is exactly [{string.Join(",", header)}]");
            // Ordinal: con il confronto culturale il BOM (carattere ignorabile)
            // "inizia" qualunque stringa, e il controllo fallirebbe sempre.
            Check(output.Text.Length == 0 || output.Text[0] != '﻿', $"{label}: no BOM");
            Check(rows.All(r => r.Length == header.Length), $"{label}: every line has {header.Length} cells");
            Equal(output.DataLines, rows.Count - 1, $"{label}: data lines");

            int qcol = Array.IndexOf(header, header.First(h => h == "Count" || h == "Quantity"));
            var qty = rows.Skip(1).Select(r => r[qcol]).ToList();
            Check(qty.All(q => int.TryParse(q, out int n) && n > 0 && n.ToString() == q),
                $"{label}: every quantity is a positive integer without separators");
            Equal(output.CopiesWritten, qty.Sum(q => int.Parse(q)), $"{label}: copies in file");

            var keys = rows.Skip(1).Select(r => string.Join("\u001f", r.Where((_, i) => i != qcol))).ToList();
            Equal(keys.Count, keys.Distinct().Count(), $"{label}: no two lines identical except quantity");
            return rows;
        }

        private static readonly string[] MoxfieldHeader =
            { "Count", "Tradelist Count", "Name", "Edition", "Condition", "Language", "Foil", "Tags", "Last Modified", "Collector Number", "Alter", "Proxy", "Purchase Price" };
        private static readonly string[] ManaBoxHeader =
            { "Name", "Set code", "Set name", "Collector number", "Quantity", "Foil", "Scryfall ID" };
        private static readonly string[] GoldfishHeader =
            { "Card", "Set ID", "Set Name", "Quantity", "Foil", "Variation" };
        private static readonly string[] GenericHeader =
            { "Quantity", "Name", "Set Code", "Set Name", "Collector Number", "Scryfall ID", "Oracle ID", "Arena ID", "Arena Set Code", "Arena Collector Number", "Match", "Digital Only", "Note" };

        private static string[] HeaderOf(ExportProfile p)
        {
            if (p == ExportProfile.Moxfield) return MoxfieldHeader;
            if (p == ExportProfile.ManaBox) return ManaBoxHeader;
            if (p == ExportProfile.MtgGoldfish) return GoldfishHeader;
            if (p == ExportProfile.Generic) return GenericHeader;
            return null;
        }

        /// <summary>Nessuna copia persa: ogni copia letta sta in una riga, fra le scartate o fra le rifiutate dal formato.</summary>
        private static void CheckConservation(CollectionExportModel model, ExportProfile p, ProfileOutput o, string label)
        {
            int rejected = o.Rejected.Sum(r => r.Quantity);
            Equal(model.CopiesIn, model.CopiesExported + model.CopiesSkipped, $"{label}: model keeps every copy (in = rows + skipped)");
            if (p != ExportProfile.NameList)
                Equal(model.CopiesExported, o.CopiesWritten + rejected, $"{label}: file + rejected = model rows");
        }

        // ------------------------------------------------------------------
        // test

        private static void TestEscaping()
        {
            Equal("plain", ExportProfile.Escape("plain"), "plain cell");
            Equal("\"a, b\"", ExportProfile.Escape("a, b"), "comma is quoted");
            Equal("\"say \"\"hi\"\"\"", ExportProfile.Escape("say \"hi\""), "quotes are doubled");
            Equal("\"two\r\nlines\"", ExportProfile.Escape("two\r\nlines"), "line break is quoted");
            Equal("\" lead\"", ExportProfile.Escape(" lead"), "leading space is quoted");
            Equal("", ExportProfile.Escape(null), "null is empty");
            Equal("Æther Vial ★", ExportProfile.Escape("Æther Vial ★"), "non-ASCII passes through");
        }

        private static void TestArenaNames()
        {
            Equal("Dusk // Dawn", CollectionMapper.CleanArenaName("Dusk /// Dawn", false), "Arena split separator");
            Equal("Pharaoh Rama-Tut", CollectionMapper.CleanArenaName("Pharaoh <nobr>Rama-Tut</nobr>", false), "nobr tags removed");
            Equal("A-Demilich", CollectionMapper.CleanArenaName("<sprite=\"SpriteSheet_MiscIcons\" name=\"arena_a\">Demilich", true),
                "Alchemy sprite removed and A- prefix added");
            Equal("A-Demilich", CollectionMapper.CleanArenaName("A-Demilich", true), "A- prefix not doubled");
            Equal(null, CollectionMapper.CleanArenaName("  <b></b> ", false), "only tags = no name");
            Check(CollectionMapper.NamesMatch("Fable of the Mirror-Breaker", "Fable of the Mirror-Breaker // Reflection of Kiki-Jiki"),
                "front face matches a double-faced name");
            Check(CollectionMapper.NamesMatch("Sol'kanar the Tainted", "Sol'Kanar the Tainted"), "case difference tolerated");
            Check(!CollectionMapper.NamesMatch("Goblin Bruiser", "Rise from the Grave"), "different cards do not match");
            Check(!CollectionMapper.NamesMatch("A-Demilich", "Demilich"), "Alchemy card does not match its paper original");
        }

        private static void TestParsing(string dir, ScryfallArenaIndex index)
        {
            var mtgo = ScryfallArenaIndex.ParseMtgoCodes(File.ReadAllText(Path.Combine(dir, "scryfall-sets.json")));
            Equal("dar", mtgo["dom"], "Dominaria MTGO code from /sets");
            Check(!mtgo.ContainsKey("akr"), "Arena-only set has no MTGO code");

            ScryfallArenaIndex.ParseSearchPage(File.ReadAllText(Path.Combine(dir, "scryfall-search-page1.json")), mtgo, out var next1, out int total);
            ScryfallArenaIndex.ParseSearchPage(File.ReadAllText(Path.Combine(dir, "scryfall-search-page2.json")), mtgo, out var next2, out _);
            Check(next1 != null, "page 1 points to a next page");
            Check(next2 == null, "last page has no next page");
            Equal(15, total, "declared total");
            Equal(15, index.Count, "prints in index");

            bool threw = false;
            try { ScryfallArenaIndex.ParseSearchPage("{\"object\":\"error\",\"details\":\"rate limited\"}", mtgo, out _, out _); }
            catch (FormatException) { threw = true; }
            Check(threw, "an error response is rejected, not read as an empty page");

            var fable = index.ByArenaId(79570).Single();
            Equal("transform", fable.Layout, "layout read");
            Equal("c0957e5e-c71b-439c-931c-9f55d2f76ace", fable.OracleId, "oracle id read");
            Check(fable.Id != fable.OracleId, "Scryfall ID and Oracle ID are different fields");
            Equal(2, index.ByArenaId(64319).Count, "arena id shared by two printings is kept as two");
        }

        private static void TestIndexFile(ScryfallArenaIndex index)
        {
            var sw = new StringWriter();
            index.Save(sw);
            var text = sw.ToString();
            var back = ScryfallArenaIndex.Load(new StringReader(text));
            Equal(index.Count, back.Count, "round trip keeps every print");
            Equal(index.BuiltUtc, back.BuiltUtc, "round trip keeps the build date");
            var a = index.ByArenaId(67330).Single();
            var b = back.ByArenaId(67330).Single();
            Check(a.Id == b.Id && a.Name == b.Name && a.Set == b.Set && a.CollectorNumber == b.CollectorNumber
                  && a.MtgoSetCode == b.MtgoSetCode && a.OracleId == b.OracleId && a.Digital == b.Digital && a.Layout == b.Layout,
                  "round trip keeps every field");

            var lines = text.Split('\n').Where(l => l.Length > 0).ToArray();
            var truncated = string.Join("\n", lines.Take(lines.Length - 3)) + "\n";
            bool threw = false;
            try { ScryfallArenaIndex.Load(new StringReader(truncated)); }
            catch (FormatException) { threw = true; }
            Check(threw, "a truncated index file is rejected");

            var path = Path.Combine(Path.GetTempPath(), "mtgaplus-index-test-" + Guid.NewGuid().ToString("N") + ".tsv");
            try
            {
                ScryfallIndexStore.Save(path, index);
                Equal(index.Count, ScryfallIndexStore.Load(path).Count, "saved through the store and read back from disk");
                Check(!File.Exists(path + ".tmp"), "no temporary file left behind");
                File.WriteAllText(path, "garbage");
                Check(ScryfallIndexStore.TryLoad(path, out var err) == null && err != null, "TryLoad on a corrupt file gives null and a reason");
                Check(ScryfallIndexStore.TryLoad(path + ".missing", out var err2) == null && err2 == null, "TryLoad on a missing file gives null and no error");
            }
            finally { File.Delete(path); }
        }

        private static void TestEmpty(ScryfallArenaIndex index)
        {
            var model = CollectionMapper.Build(new ArenaCard[0], index);
            Equal(0, model.Rows.Count, "no rows");
            foreach (var p in ExportProfile.All)
            {
                var o = p.Write(model);
                var h = HeaderOf(p);
                if (h != null)
                {
                    var rows = CheckCsv(p, o, h, p.Id);
                    Equal(1, rows.Count, $"{p.Id}: header only");
                }
                else Equal("", o.Text, $"{p.Id}: empty text");
            }
        }

        private static void TestSingle(ScryfallArenaIndex index)
        {
            var model = CollectionMapper.Build(new[] { Card(67330, 1, "Yargle, Glutton of Urborg", "DAR", "113") }, index);
            var o = ExportProfile.Moxfield.Write(model);
            var rows = CheckCsv(ExportProfile.Moxfield, o, MoxfieldHeader, "moxfield");
            Equal(2, rows.Count, "one data line");
            var r = rows[1];
            Equal("1", r[0], "Count");
            Equal("Yargle, Glutton of Urborg", r[2], "Name (with comma, read back intact)");
            Equal("dom", r[3], "Edition is the Scryfall code, not Arena's DAR");
            Equal("113", r[9], "Collector Number");
            Check(r[1] == "" && r[4] == "" && r[5] == "" && r[6] == "" && r[10] == "" && r[11] == "" && r[12] == "",
                "trade, condition, language, foil, alter, proxy and price are left empty");
        }

        /// <summary>
        /// La collezione di prova. Valori di Arena presi dal suo database
        /// (2026-10-13), stampe Scryfall dalla ricerca del 2026-10-14.
        /// </summary>
        private static List<ArenaCard> FixtureCollection() => new List<ArenaCard>
        {
            Card(67330, 1, "Yargle, Glutton of Urborg", "DAR", "113"),          // codice Arena != Scryfall
            Card(67984, 3, "Lightning Strike", "M19", "152"),                    // stessa carta, set diversi
            Card(66263, 2, "Lightning Strike", "XLN", "149"),
            Card(82189, 1, "Lightning Strike", "DMU", "137"),
            Card(82271, 4, "Sol'kanar the Tainted", "DMU", "219"),               // maiuscola diversa da Scryfall
            Card(73788, 2, "Dusk /// Dawn", "AKR", "16", digital: true),         // split, solo digitale
            Card(79570, 1, "Fable of the Mirror-Breaker", "NEO", "141"),         // doppia faccia
            Card(79848, 2, "Fable of the Mirror-Breaker", "NEO", "357"),         // stesso set, altro numero
            Card(70262, 4, "Bonecrusher Giant", "ELD", "115"),                   // avventura
            Card(68773, 1, "Goblin Bruiser", "ANA", "38", digital: true),        // l'id di Scryfall e' di un'altra carta
            Card(64319, 1, "Battle at the Bridge", "AER", "53"),                 // id condiviso da due stampe
            Card(81072, 1, "<sprite=\"SpriteSheet_MiscIcons\" name=\"arena_a\">Demilich", "AFR", "53",
                 digital: true, rebalanced: true),                               // Alchemy: Scryfall non la ha
            Card(91882, 1, "Jace, the Mind Sculptor", "BLC", "75"),
            Card(74995, 2, "Lightning Strike", "AJMP", "152", digital: true),    // ristampa solo digitale
            Card(999001, 1, "Rise from the Grave", "M19", "117"),                // id ignoto a Scryfall: set+numero
            Card(999002, 3, "Kongming, \"Sleeping Dragon\"", "PTK", "1"),         // virgola e apici, solo nome
            Card(999100, 5, "Goblin", "M19", "T1", token: true),                 // token
            Card(999200, 1, null, null, null, inDb: false),                      // ignota a tutti
            Card(70262, 1, "Bonecrusher Giant", "ELD", "115"),                   // stessa voce due volte
            Card(999300, 0, "Zero", "M19", "1"),                                 // quantita' nulla
            Card(999301, -2, "Negative", "M19", "2"),                            // quantita' negativa
        };

        private static ExportRow RowOf(CollectionExportModel m, uint grpId) => m.Rows.Single(r => r.ArenaIds.Contains(grpId));

        private static void TestFixtureMapping(ScryfallArenaIndex index)
        {
            var m = CollectionMapper.Build(FixtureCollection(), index);

            Equal(37, m.CopiesIn, "copies read (positive only)");
            Equal(2, m.NonPositiveEntries, "zero/negative entries counted, not exported");
            Equal(2, m.Skipped.Count, "skipped entries (token + unknown)");
            Check(m.Skipped.Any(s => s.GrpId == 999100 && s.Count == 5 && s.Reason.Contains("token")), "token skipped with reason");
            Check(m.Skipped.Any(s => s.GrpId == 999200 && s.Reason.Contains("not in the Arena card database")), "unknown card skipped with reason");

            var yargle = RowOf(m, 67330);
            Equal(MatchPrecision.ArenaId, yargle.Precision, "Yargle matched by arena_id");
            Equal("dom", yargle.SetCode, "Yargle Scryfall set");
            Equal("dar", yargle.MtgoSetCode, "Yargle MTGO set");
            Equal("DAR", yargle.ArenaSetCode, "Arena set kept alongside");

            Equal(4, m.Rows.Count(r => r.Name == "Lightning Strike"), "Lightning Strike: four printings, four rows");
            Equal(3, RowOf(m, 67984).Quantity, "M19 Lightning Strike quantity");

            Equal(MatchPrecision.ArenaId, RowOf(m, 82271).Precision, "Sol'kanar matched despite case");
            Equal("Sol'Kanar the Tainted", RowOf(m, 82271).Name, "Sol'kanar takes Scryfall's spelling");

            var dusk = RowOf(m, 73788);
            Equal("Dusk // Dawn", dusk.Name, "split name");
            Equal("Dusk // Dawn", dusk.FrontFaceName, "split keeps both halves as front face");
            Check(dusk.DigitalOnly, "Amonkhet Remastered is digital only");

            var fable = RowOf(m, 79570);
            Equal("Fable of the Mirror-Breaker // Reflection of Kiki-Jiki", fable.Name, "double-faced full name");
            Equal("Fable of the Mirror-Breaker", fable.FrontFaceName, "double-faced front face");
            Check(RowOf(m, 79848) != fable, "same card, different collector number: separate rows");

            var giant = RowOf(m, 70262);
            Equal(5, giant.Quantity, "duplicate inventory entries summed (4 + 1)");
            Equal("Bonecrusher Giant", giant.FrontFaceName, "adventure front face");

            var bruiser = RowOf(m, 68773);
            Equal(MatchPrecision.NameOnly, bruiser.Precision, "wrong Scryfall arena_id rejected");
            Equal("Goblin Bruiser", bruiser.Name, "keeps Arena's name, not Scryfall's wrong one");
            Check(bruiser.Note != null && bruiser.Note.Contains("different card"), "note explains the rejection");

            var battle = RowOf(m, 64319);
            Equal(MatchPrecision.ArenaId, battle.Precision, "shared arena_id narrowed by set and number");
            Equal("aer", battle.SetCode, "the right one of the two");

            var demilich = RowOf(m, 81072);
            Equal("A-Demilich", demilich.Name, "Alchemy card keeps its own name");
            Equal(MatchPrecision.NameOnly, demilich.Precision, "Alchemy card is not mapped to the paper Demilich");
            Check(demilich.Rebalanced && demilich.DigitalOnly, "flagged rebalanced and digital");

            var rise = RowOf(m, 999001);
            Equal(MatchPrecision.SetAndNumber, rise.Precision, "unknown arena_id matched by set+number+name");
            Equal("m19", rise.SetCode, "set from Scryfall");

            Equal(MatchPrecision.NameOnly, RowOf(m, 999002).Precision, "card absent from Scryfall: name only");
            Check(RowOf(m, 74995).DigitalOnly, "digital reprint flagged");
        }

        private static void TestFixtureProfiles(ScryfallArenaIndex index)
        {
            var m = CollectionMapper.Build(FixtureCollection(), index);
            foreach (var p in ExportProfile.All)
            {
                var o = p.Write(m);
                CheckConservation(m, p, o, p.Id);
                var h = HeaderOf(p);
                if (h != null) CheckCsv(p, o, h, p.Id);
            }

            // Moxfield
            var mox = ParseCsv(ExportProfile.Moxfield.Write(m).Text);
            Check(mox.Any(r => r[2] == "Kongming, \"Sleeping Dragon\"" && r[0] == "3" && r[3] == ""),
                "moxfield: name with comma and quotes read back intact, no edition invented");
            Check(mox.Any(r => r[2] == "Dusk // Dawn" && r[3] == "akr" && r[9] == "16"), "moxfield: split card");
            Check(mox.Skip(1).All(r => r[6] == ""), "moxfield: Foil always empty (Arena styles are not foil)");
            Equal(0, ExportProfile.Moxfield.Write(m).Rejected.Count, "moxfield: nothing rejected");

            // ManaBox
            var mbOut = ExportProfile.ManaBox.Write(m);
            var mb = ParseCsv(mbOut.Text);
            Check(mb.Skip(1).All(r => r[1] != "" && r[6] != ""), "manabox: every line has set code and Scryfall ID");
            Check(mb.Skip(1).All(r => r[5] == "normal"), "manabox: Foil is 'normal'");
            Equal(3, mbOut.Rejected.Count, "manabox: name-only rows rejected (Goblin Bruiser, A-Demilich, Kongming)");
            Check(mb.Any(r => r[0] == "Yargle, Glutton of Urborg" && r[1] == "dom" && r[6] == "645cfc1b-76f2-4823-9fb0-03cb009f8b32"),
                "manabox: Yargle with its Scryfall print id");

            // MTGGoldfish
            var gfOut = ExportProfile.MtgGoldfish.Write(m);
            var gf = ParseCsv(gfOut.Text);
            Check(gf.Any(r => r[0] == "Yargle, Glutton of Urborg" && r[1] == "DAR"), "goldfish: MTGO set code DAR");
            Check(gf.Any(r => r[0] == "Fable of the Mirror-Breaker" && r[1] == "NEO"), "goldfish: front face only for DFC");
            Check(gf.Any(r => r[0] == "Dusk // Dawn" && r[1] == "AKR"), "goldfish: split card whole, Scryfall code upper-cased");
            Check(gf.Skip(1).All(r => r[4] == "REGULAR" && r[5] == ""), "goldfish: Foil REGULAR, Variation empty");
            Check(gfOut.CopiesWithCaveat > 0 && gfOut.CaveatReason.Contains("MTGO"), "goldfish: rows without MTGO code are flagged");

            // Generic
            var gen = ParseCsv(ExportProfile.Generic.Write(m).Text);
            var g = gen.Single(r => r[1] == "Bonecrusher Giant // Stomp");
            Equal("70262", g[7], "generic: Arena id");
            Equal("arena_id", g[10], "generic: match label");
            var k = gen.Single(r => r[1] == "Kongming, \"Sleeping Dragon\"");
            Check(k[2] == "" && k[5] == "" && k[8] == "PTK" && k[9] == "1" && k[10] == "name",
                "generic: name-only row keeps Arena set/number, no Scryfall data invented");
            Equal("true", gen.Single(r => r[1] == "A-Demilich")[11], "generic: digital only flag");

            // Name list
            var txt = ExportProfile.NameList.Write(m).Text;
            Check(txt.Contains("8 Lightning Strike\r\n"), "name list: printings summed (3+2+1+2)");
            Check(!txt.Contains("///"), "name list: no Arena split separator");
        }

        private static void TestWithoutIndex()
        {
            var m = CollectionMapper.Build(FixtureCollection(), null);
            Check(!m.HadScryfallIndex, "model knows it had no index");
            Check(m.Rows.All(r => r.Precision == MatchPrecision.NameOnly && r.SetCode == null && r.ScryfallId == null),
                "without index every row is name only, nothing invented");
            Check(m.Rows.All(r => r.Note == "no Scryfall index"), "and says why");
            Equal(4, m.Rows.Count(r => r.Name == "Lightning Strike"), "printings still kept apart (by Arena set/number)");
            foreach (var p in ExportProfile.All) CheckConservation(m, p, p.Write(m), p.Id + " (no index)");
            var mox = ParseCsv(ExportProfile.Moxfield.Write(m).Text);
            Equal(1, mox.Count(r => r[2] == "Lightning Strike"), "moxfield without index: identical lines merged");
            Equal("8", mox.Single(r => r[2] == "Lightning Strike")[0], "moxfield without index: merged quantity");
            Equal(0, ParseCsv(ExportProfile.ManaBox.Write(m).Text).Count - 1, "manabox without index: no lines (all rejected)");
        }

        private static void TestDeterminism(ScryfallArenaIndex index)
        {
            var cards = FixtureCollection();
            var a = CollectionMapper.Build(cards, index);
            var rnd = new Random(1234);
            for (int i = 0; i < 5; i++)
            {
                var shuffled = cards.OrderBy(_ => rnd.Next()).ToList();
                var b = CollectionMapper.Build(shuffled, index);
                foreach (var p in ExportProfile.All)
                    if (p.Write(a).Text != p.Write(b).Text) { Check(false, $"{p.Id}: output depends on input order (round {i})"); return; }
            }
            Check(true, "5 shuffles x 5 profiles: identical bytes");
        }

        private static void TestLarge()
        {
            var prints = new List<ScryfallPrint>();
            var cards = new List<ArenaCard>();
            var rnd = new Random(42);
            int copies = 0;
            for (uint i = 0; i < 20000; i++)
            {
                uint id = 100000 + i;
                string name = "Card " + (i % 7000);                  // ristampe: stesso nome in piu' set
                string set = "s" + (i / 250);
                prints.Add(new ScryfallPrint
                {
                    ArenaId = id, Id = Guid.NewGuid().ToString(), OracleId = "o" + (i % 7000), Name = name,
                    Layout = "normal", Set = set, SetName = "Set " + set, CollectorNumber = (i % 250 + 1).ToString(),
                });
                int n = rnd.Next(1, 5);
                copies += n;
                cards.Add(Card(id, n, name, set.ToUpperInvariant(), (i % 250 + 1).ToString()));
            }
            for (uint i = 0; i < 1000; i++)
            {
                cards.Add(Card(500000 + i, 1, "Unmapped " + i, "ZZZ", i.ToString()));
                copies++;
            }
            var index = new ScryfallArenaIndex(prints, DateTime.UtcNow);
            var sw = Stopwatch.StartNew();
            var m = CollectionMapper.Build(cards, index);
            var outputs = ExportProfile.All.Select(p => (p, o: p.Write(m))).ToList();
            sw.Stop();

            Equal(copies, m.CopiesIn, "copies read");
            Equal(21000, m.Rows.Count, "one row per printing");
            Equal(20000, m.Rows.Count(r => r.Precision == MatchPrecision.ArenaId), "mapped rows");
            foreach (var (p, o) in outputs)
            {
                CheckConservation(m, p, o, p.Id);
                var h = HeaderOf(p);
                if (h != null) CheckCsv(p, o, h, p.Id);
            }
            Check(sw.ElapsedMilliseconds < 5000, $"map + 5 files in {sw.ElapsedMilliseconds} ms");
        }

        /// <summary>
        /// Tutto il database di Arena contro tutto l'indice Scryfall. Non e'
        /// la collezione di nessuno: e' il caso peggiore, ogni stampa una volta.
        /// </summary>
        private static void TestRealData(string scryDir, string arenaTsv)
        {
            var mtgo = ScryfallArenaIndex.ParseMtgoCodes(File.ReadAllText(Path.Combine(scryDir, "sets.json")));
            var prints = new List<ScryfallPrint>();
            int declared = -1;
            foreach (var f in Directory.GetFiles(scryDir, "page*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                prints.AddRange(ScryfallArenaIndex.ParseSearchPage(File.ReadAllText(f), mtgo, out _, out int t));
                declared = t;
            }
            var index = new ScryfallArenaIndex(prints, DateTime.UtcNow);
            Equal(declared, index.Count, "every declared Scryfall print parsed");
            var sw = new StringWriter(); index.Save(sw);
            Console.WriteLine($"  info  index file size {Encoding.UTF8.GetByteCount(sw.ToString()) / 1024} KB");

            var primary = new List<ArenaCard>();
            var allNonToken = new List<ArenaCard>();
            foreach (var line in File.ReadLines(arenaTsv))
            {
                var f = line.Split('\t');
                var c = Card(uint.Parse(f[0]), 1, f[1].Length == 0 ? null : f[1], f[2], f[3],
                    digital: f[5] == "1", rebalanced: f[6] == "1", token: f[4] == "1");
                if (c.IsToken) continue;
                allNonToken.Add(c);
                if (f[7] == "1") primary.Add(c);
            }

            foreach (var (label, cards) in new[] { ("primary printings", primary), ("all non-token printings", allNonToken) })
            {
                var m = CollectionMapper.Build(cards, index);
                int total = m.CopiesIn;
                Console.WriteLine($"  info  {label}: {total} | arena_id {m.CopiesWith(MatchPrecision.ArenaId)} " +
                                  $"| set+number {m.CopiesWith(MatchPrecision.SetAndNumber)} | name only {m.CopiesWith(MatchPrecision.NameOnly)} " +
                                  $"| skipped {m.CopiesSkipped} | digital {m.CopiesDigitalOnly}");
                foreach (var g in m.Rows.Where(r => r.Note != null).GroupBy(r => r.Note.Split('(')[0].Trim()).OrderByDescending(g => g.Count()))
                    Console.WriteLine($"  info    {g.Count(),5}  {g.Key}");
                foreach (var p in ExportProfile.All)
                {
                    var o = p.Write(m);
                    CheckConservation(m, p, o, $"{label} / {p.Id}");
                    var h = HeaderOf(p);
                    if (h != null) CheckCsv(p, o, h, $"{label} / {p.Id}");
                    if (o.Rejected.Count > 0 || o.CopiesWithCaveat > 0)
                        Console.WriteLine($"  info  {p.Id}: rejected {o.Rejected.Sum(r => r.Quantity)} ({o.RejectReason}), caveat {o.CopiesWithCaveat} ({o.CaveatReason})");
                }
            }
        }
    }
}
