using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using UnityEngine;
using Wotc.Mtga.Cards.Database;
using Wotc.Mtgo.Gre.External.Messaging;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Scrive un replay come **pagina HTML autosufficiente** e la apre nel
    /// browser predefinito.
    ///
    /// Perche' un file e non un server locale: un server vorrebbe dire una
    /// porta in ascolto dentro il processo del gioco, il permesso del firewall
    /// di Windows alla prima esecuzione e un servizio di rete da mantenere. Un
    /// file non costa niente di tutto questo, e in piu' si archivia, si rinomina
    /// e si manda a qualcuno: un replay diventa una cosa che possiedi, non un
    /// indirizzo che funziona solo mentre Arena e' aperta.
    ///
    /// La pagina **non ricostruisce niente**: riceve i passi gia' pronti da
    /// <see cref="ReplayReconstructor"/> e li disegna.
    /// </summary>
    internal static class ReplayExporter
    {
        /// <summary>
        /// Apre il replay di questo match. Restituisce false se non c'e' niente
        /// da aprire; in quel caso ha gia' avvisato l'utente.
        /// </summary>
        public static bool OpenFor(MatchRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.ReplayFile))
            {
                Toast.Warning("No replay recorded for this match");
                return false;
            }

            string dir = ReplayRecorder.Directory();
            if (dir == null)
            {
                Toast.Error("Replay folder not available");
                return false;
            }

            string source = Path.Combine(dir, record.ReplayFile);
            if (!File.Exists(source))
            {
                Toast.Warning("That replay is no longer on disk");
                Plugin.Log.LogWarning($"Replay: '{source}' non c'e' piu'.");
                return false;
            }

            try
            {
                var read = ReplayReader.Read(source);
                if (read.Messages.Count == 0)
                {
                    Toast.Error("The replay could not be read");
                    Plugin.Log.LogWarning($"Replay: '{record.ReplayFile}' illeggibile ({read.Error ?? "vuoto"}).");
                    return false;
                }

                if (read.Truncated)
                    Plugin.Log.LogWarning(
                        $"Replay: '{record.ReplayFile}' e' troncato, si apre quello che c'e' " +
                        $"({read.Messages.Count} messaggi).");

                var doc = Build(read.Messages, record);

                string template = Template();
                if (template == null)
                {
                    Toast.Error("The replay viewer is missing from this build");
                    return false;
                }

                string html = ReplayHtml.Inject(template, ReplayHtml.Json(doc, MetaOf(record, read.Truncated)));

                string page = Path.Combine(dir, Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(record.ReplayFile)) + ".html");

                // UTF-8 senza BOM: un browser che apre un file:// legge l'HTML
                // e il meta charset, e un BOM davanti al doctype lo confonde.
                File.WriteAllText(page, html, new UTF8Encoding(false));

                Plugin.Log.LogInfo(
                    $"Replay: pagina scritta ({doc.Steps.Count} passi, {doc.Cards.Count} carte, " +
                    $"{new FileInfo(page).Length / 1024} KB) -> {Path.GetFileName(page)}");

                foreach (var w in doc.Warnings) Plugin.Log.LogWarning("Replay: " + w);

                if (doc.Steps.Count == 0)
                {
                    Toast.Warning("Nothing to show in this replay");
                    return false;
                }

                // **Non `Application.OpenURL`**: quello lascia il browser
                // figlio di MTGA, e Steam poi non considera chiuso il gioco
                // finche' il browser resta aperto. Vedi `ExternalOpen`.
                return ExternalOpen.Detached(page);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Replay: esportazione fallita ({ex}).");
                Toast.Error("Could not open the replay - see the log");
                return false;
            }
        }

        // -----------------------------------------------------------------

        /// <summary>
        /// Il visualizzatore, cioe' la pagina React costruita da <c>viewer/</c>
        /// e incorporata nella DLL.
        ///
        /// **Perche' una risorsa incorporata e non un file accanto alla DLL:**
        /// un file si perde, si aggiorna a meta', e un utente che copia solo la
        /// DLL si ritrova un pulsante che non fa niente. Dentro l'assembly non
        /// puo' separarsi da chi la usa.
        ///
        /// Si legge una volta sola: sono qualche centinaio di KB, e un replay
        /// si apre piu' volte di seguito.
        /// </summary>
        private static string _template;

        private static string Template()
        {
            if (_template != null) return _template;

            try
            {
                var asm = typeof(ReplayExporter).Assembly;
                using (var stream = asm.GetManifestResourceStream(TemplateResource))
                {
                    if (stream == null)
                    {
                        Plugin.Log.LogError(
                            $"Replay: risorsa '{TemplateResource}' assente dall'assembly. " +
                            "Il visualizzatore va ricostruito: cd viewer && npm run build.");
                        return null;
                    }
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                        _template = reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Replay: modello non leggibile ({ex.Message}).");
                return null;
            }

            return _template;
        }

        private const string TemplateResource = "MTGAEnhancementSuite.Resources.replay-viewer.html";

        private static ReplayHtml.Meta MetaOf(MatchRecord record, bool truncated)
        {
            return new ReplayHtml.Meta
            {
                Deck = PlainName(record.DeckName),
                Opponent = record.OpponentName,
                Event = record.EventName,
                Format = record.Format,
                Ranked = record.Ranked,
                Date = record.EndedLocal.ToString("yyyy-MM-dd HH:mm"),
                Won = record.Won,
                Truncated = truncated,
            };
        }

        private static ReplayReconstructor.ReplayDocument Build(
            IList<GREToClientMessage> messages, MatchRecord record)
        {
            // Il posto locale si impara dal file, ma serve gia' mentre si
            // ricostruisce, per dare un nome ai giocatori: si guarda prima.
            uint local = 0;
            foreach (var m in messages)
            {
                if (m == null || m.SystemSeatIds == null || m.SystemSeatIds.Count != 1) continue;
                local = m.SystemSeatIds[0];
                break;
            }

            string mine = PlainName(record.DeckName);
            if (string.IsNullOrEmpty(mine)) mine = "You";
            string theirs = string.IsNullOrEmpty(record.OpponentName) ? "Opponent" : record.OpponentName;

            ReplayReconstructor.SeatNamer seatName = seat =>
            {
                if (local == 0) return null;
                return seat == local ? mine : theirs;
            };

            return ReplayReconstructor.Build(messages, seatName, CardName);
        }

        /// <summary>
        /// Il nome inglese della carta, dal database di Arena.
        ///
        /// Si chiede **solo per i GrpId che arrivano da oggetti carta**: il
        /// GrpId di un'abilita' vive nello stesso spazio numerico e qui
        /// troverebbe una carta a caso (MISTAKES.md #33). Il filtro sta in
        /// <c>ReplayReconstructor.Remember</c>, che e' l'unico a chiamare
        /// questo metodo.
        /// </summary>
        private static string CardName(uint grpId)
        {
            try
            {
                var db = GameReflection.PantryGet<CardDatabase>();
                var titles = db?.CardTitleProvider;
                if (titles == null) return null;
                return titles.GetCardTitle(grpId, null, false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Il nome del mazzo senza i tag di stile. Un nome come
        /// <c>&lt;color=#F00&gt;Rosso</c> in una pagina HTML si vedrebbe cosi'
        /// com'e': i tag di TMP non sono HTML.
        /// </summary>
        private static string PlainName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            string plain;
            List<CharStyle> styles;
            if (StyledName.TryParse(raw, out plain, out styles) && !string.IsNullOrEmpty(plain))
                return plain;
            return raw;
        }


    }
}
