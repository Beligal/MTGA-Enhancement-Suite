using System;
using System.Collections.Generic;
using System.IO;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.State;
using Newtonsoft.Json;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// L'archivio dei match giocati, su disco.
    ///
    /// **Una riga JSON per match, in aggiunta in coda** (`matches.jsonl`), non
    /// un unico documento riscritto ogni volta. Tre motivi, tutti pratici:
    /// aggiungere costa quanto la riga e non quanto lo storico; una riga
    /// rovinata da un'interruzione si butta da sola senza portarsi dietro le
    /// altre; e dopo mille partite non si riscrive un file da megabyte a ogni
    /// fine match.
    ///
    /// Separato **per account** (`PersonaID`), come le cartelle dei mazzi: chi
    /// ha due account non deve vedere i numeri mescolati. Finche' il login non
    /// e' arrivato al Pantry non si scrive niente — inventare una cartella
    /// "sconosciuto" significherebbe mescolarci dentro account diversi, ed e'
    /// un danno che dopo non si separa piu'.
    /// </summary>
    internal static class MatchStatsStore
    {
        private const string FileName = "matches.jsonl";

        private static string _dir;
        private static string _dirForPersona;

        private static List<MatchRecord> _cache;
        private static string _cacheForPersona;

        /// <summary>
        /// La cartella dei dati di questo account, creandola se serve, oppure
        /// null se l'account non e' ancora noto.
        /// </summary>
        public static string Directory()
        {
            var persona = AccountId.CurrentAsFolderName();
            if (string.IsNullOrEmpty(persona)) return null;

            if (_dir != null && _dirForPersona == persona) return _dir;

            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dir = Path.Combine(appData, "MTGAEnhancementSuite", "stats", persona);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                _dir = dir;
                _dirForPersona = persona;
                return dir;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"MatchStats: cartella non creata ({ex.Message}).");
                return null;
            }
        }

        private static string FilePath()
        {
            var dir = Directory();
            return dir == null ? null : Path.Combine(dir, FileName);
        }

        // -----------------------------------------------------------------
        // Scrittura
        // -----------------------------------------------------------------

        /// <summary>
        /// Aggiunge un match in coda. Restituisce false se non si e' potuto
        /// scrivere — chi chiama deve saperlo, perche' una partita non salvata
        /// non si recupera piu'.
        /// </summary>
        public static bool Append(MatchRecord record)
        {
            if (record == null) return false;

            var path = FilePath();
            if (path == null)
            {
                Plugin.Log.LogWarning("MatchStats: account non ancora noto, match NON salvato.");
                return false;
            }

            try
            {
                // Formatting.None: una riga sola, altrimenti il formato "una
                // riga per match" salta e il file non si rilegge piu'.
                string line = JsonConvert.SerializeObject(record, Formatting.None);
                File.AppendAllText(path, line + Environment.NewLine);

                if (_cache != null && _cacheForPersona == _dirForPersona) _cache.Add(record);

                Plugin.Log.LogInfo($"MatchStats: match {record.MatchId} salvato ({line.Length} byte).");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"MatchStats: scrittura fallita: {ex.Message}");
                return false;
            }
        }

        // -----------------------------------------------------------------
        // Lettura
        // -----------------------------------------------------------------

        /// <summary>
        /// Tutti i match di questo account, dal piu' vecchio al piu' recente.
        /// Le righe illeggibili vengono saltate e contate nel log: una riga
        /// troncata da un arresto improvviso non deve far sparire lo storico.
        /// </summary>
        public static List<MatchRecord> All()
        {
            var persona = _dirForPersona;
            if (_cache != null && persona != null && _cacheForPersona == persona) return _cache;

            var list = new List<MatchRecord>();
            var path = FilePath();
            if (path == null) return list;

            if (!File.Exists(path))
            {
                _cache = list;
                _cacheForPersona = _dirForPersona;
                return list;
            }

            int broken = 0;
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = JsonConvert.DeserializeObject<MatchRecord>(line);
                        if (rec != null) list.Add(rec);
                        else broken++;
                    }
                    catch { broken++; }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"MatchStats: lettura fallita: {ex.Message}");
                return list;
            }

            // Una riga fuori dai rami: dice quanto ha letto, non solo se e'
            // andata male. Un "0 match" senza contesto non distingue "non hai
            // ancora giocato" da "non ho saputo leggere niente".
            Plugin.Log.LogInfo($"MatchStats: letti {list.Count} match, {broken} righe illeggibili saltate.");

            _cache = list;
            _cacheForPersona = _dirForPersona;
            return list;
        }

        /// <summary>Butta la copia in memoria: la prossima lettura ripassa dal disco.</summary>
        public static void Invalidate()
        {
            _cache = null;
            _cacheForPersona = null;
        }

        /// <summary>
        /// I numeri di una lista di match.
        ///
        /// "On play" si conta **sulla prima partita di ogni match**: dalla
        /// seconda in poi chi comincia non lo decide il sorteggio ma chi ha
        /// perso quella prima, quindi mescolarle misurerebbe un'altra cosa.
        /// I match di cui non sappiamo chi ha cominciato non entrano in
        /// nessuna delle due percentuali, invece di finire d'ufficio in una.
        /// </summary>
        public static DeckSummary Summarize(List<MatchRecord> matches)
        {
            var s = new DeckSummary();
            if (matches == null) return s;

            var byEvent = new Dictionary<string, int>();

            foreach (var m in matches)
            {
                if (m == null) continue;

                s.Matches++;
                if (m.Won == null) s.MatchDraws++;
                else if (m.Won.Value) s.MatchWins++;

                if (m.Games != null)
                {
                    foreach (var g in m.Games)
                    {
                        if (g == null) continue;
                        s.Games++;
                        if (g.Won == null) s.GameDraws++;
                        else if (g.Won.Value) s.GameWins++;
                    }
                }

                var onPlay = m.OnPlayGameOne;
                if (onPlay != null && m.Won != null)
                {
                    if (onPlay.Value)
                    {
                        s.OnPlayMatches++;
                        if (m.Won.Value) s.OnPlayWins++;
                    }
                    else
                    {
                        s.OnDrawMatches++;
                        if (m.Won.Value) s.OnDrawWins++;
                    }
                }

                string evt = string.IsNullOrEmpty(m.EventName) ? "(unknown)" : m.EventName;
                int n;
                byEvent[evt] = byEvent.TryGetValue(evt, out n) ? n + 1 : 1;

                if (s.LastPlayedUtc == null || m.EndedUtc > s.LastPlayedUtc.Value)
                    s.LastPlayedUtc = m.EndedUtc;
            }

            foreach (var pair in byEvent) s.ByEvent.Add(pair);
            s.ByEvent.Sort((a, b) => b.Value.CompareTo(a.Value));
            return s;
        }

        /// <summary>I match giocati con un mazzo, dal piu' recente.</summary>
        public static List<MatchRecord> ForDeck(Guid deckId)
        {
            string key = deckId.ToString();
            var result = new List<MatchRecord>();
            foreach (var m in All())
                if (m != null && string.Equals(m.DeckId, key, StringComparison.OrdinalIgnoreCase))
                    result.Add(m);
            result.Sort((a, b) => b.EndedUtc.CompareTo(a.EndedUtc));
            return result;
        }
    }
}
