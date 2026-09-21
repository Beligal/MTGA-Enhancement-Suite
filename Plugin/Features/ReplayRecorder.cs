using System;
using System.IO;
using System.IO.Compression;
using Google.Protobuf;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using Wotc.Mtgo.Gre.External.Messaging;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Salva su disco il flusso grezzo dei messaggi di una partita, perche' un
    /// giorno un visualizzatore possa ripercorrerla.
    ///
    /// **Spenta di default.** Il costo non e' trascurabile e non e' ancora
    /// misurato sul nostro formato: sul `Player.log` del 2026-09-15 i messaggi
    /// di stato erano 427 per 8 MB di testo, media ~18,8 KB l'uno, e da
    /// `combat-state.md` una partita ne produce circa 380. In forma testuale
    /// sarebbero ~7 MB a partita. Qui si salva la forma **binaria** originale
    /// (protobuf) compressa, che e' molto meno — ma di quanto lo dira' il log
    /// alla prima partita registrata, non una stima.
    ///
    /// **Formato: nessuna trasformazione.** Lunghezza a 4 byte piu' i byte del
    /// messaggio, ripetuto, il tutto in gzip. Convertire adesso in una forma
    /// piu' comoda vorrebbe dire buttare via informazione che un visualizzatore
    /// futuro potrebbe volere, e quella informazione non si recupera piu'.
    /// </summary>
    internal static class ReplayRecorder
    {
        // L'estensione sta in ReplayFiles, insieme alle guardie che la usano
        // per decidere cosa e' lecito cancellare: una seconda copia qui e' la
        // deriva che prima o poi fa divergere le due.
        private static FileStream _file;
        private static GZipStream _gzip;
        private static string _path;
        private static int _messages;
        private static long _rawBytes;
        private static bool _failed;
        private static bool _loggedFirstSize;

        /// <summary>Vero se in questo momento stiamo scrivendo una partita.</summary>
        public static bool IsRecording => _gzip != null && !_failed;

        /// <summary>Vero se l'utente ha acceso la registrazione.</summary>
        public static bool Enabled => ModSettings.Instance.RecordReplays;

        public static string Directory()
        {
            var statsDir = MatchStatsStore.Directory();
            if (statsDir == null) return null;
            try
            {
                var dir = Path.Combine(statsDir, "replays");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Replay: cartella non creata ({ex.Message}).");
                return null;
            }
        }

        // -----------------------------------------------------------------

        public static void BeginMatch(string matchId, string deckName)
        {
            Abort();   // una partita precedente lasciata aperta da un'uscita brusca

            if (!Enabled) return;

            var dir = Directory();
            if (dir == null) return;

            try
            {
                string name = ReplayFiles.BuildName(DateTime.Now, deckName, matchId);
                _path = Path.Combine(dir, name);
                _file = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
                _gzip = new GZipStream(_file, CompressionMode.Compress);
                _messages = 0;
                _rawBytes = 0;
                _failed = false;
                Plugin.Log.LogInfo($"Replay: registrazione avviata -> {name}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Replay: apertura fallita ({ex.Message}), registrazione saltata.");
                Abort();
                _failed = true;
            }
        }

        /// <summary>
        /// Scrive un messaggio. Gira **dentro** il gestore del flusso GRE,
        /// quindi non puo' permettersi di sollevare: al primo errore chiude e
        /// smette per questa partita, e lo dice una volta sola.
        /// </summary>
        public static void Capture(GREToClientMessage msg)
        {
            if (_gzip == null || _failed || msg == null) return;

            try
            {
                byte[] payload = msg.ToByteArray();
                _gzip.Write(BitConverter.GetBytes(payload.Length), 0, 4);
                _gzip.Write(payload, 0, payload.Length);
                _messages++;
                _rawBytes += payload.Length + 4;
            }
            catch (Exception ex)
            {
                _failed = true;
                Plugin.Log.LogError($"Replay: scrittura interrotta ({ex.Message}), registrazione di questa partita annullata.");
                Abort();
            }
        }

        /// <summary>
        /// Chiude il file e restituisce il **nome** da mettere nel record del
        /// match, o null se non si e' registrato niente.
        /// </summary>
        public static string FinishMatch(string matchId)
        {
            if (_gzip == null) { Abort(); return null; }

            string path = _path;
            int messages = _messages;
            long raw = _rawBytes;

            try
            {
                _gzip.Dispose();     // il Dispose del gzip chiude anche il FileStream sottostante
                _gzip = null;
                _file = null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Replay: chiusura fallita ({ex.Message}).");
                Abort();
                return null;
            }

            if (_failed || path == null) { Reset(); return null; }

            long onDisk = -1;
            try { onDisk = new FileInfo(path).Length; } catch { }

            // La promessa fatta nello studio: la dimensione VERA, non una stima.
            string sizeLine =
                $"Replay: {messages} messaggi, {raw / 1024} KB non compressi -> " +
                $"{(onDisk < 0 ? "?" : (onDisk / 1024).ToString())} KB su disco";

            if (!_loggedFirstSize)
            {
                _loggedFirstSize = true;
                Plugin.Log.LogInfo(sizeLine + "  <-- prima registrazione della sessione");

                // E adesso lo rileggiamo. Scrivere un file e dichiararlo fatto
                // senza averlo mai riaperto e' come dire che un controllo
                // funziona senza avergli mai visto dire di no: il formato
                // andava provato da qualche parte, e il posto meno costoso e'
                // qui, subito, mentre il file e' appena stato chiuso.
                //
                // Solo per il primo di ogni sessione: rileggere un file da
                // megabyte a ogni fine partita si sentirebbe.
                try
                {
                    var check = ReplayReader.Inspect(path);
                    if (!check.Ok)
                        Toast.Warning("Replay saved but could not be read back - see the log");
                    else if (check.Messages.Count != messages)
                        Plugin.Log.LogWarning(
                            $"Replay: scritti {messages} messaggi ma riletti {check.Messages.Count}. " +
                            "Il formato non torna.");
                    else
                        Plugin.Log.LogInfo("Replay: riletto correttamente, il formato regge.");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"Replay: verifica di rilettura fallita ({ex.Message}).");
                }
            }
            else
            {
                Plugin.Log.LogInfo(sizeLine);
            }

            string fileName = Path.GetFileName(path);
            Reset();
            return fileName;
        }

        // -----------------------------------------------------------------

        private static void Abort()
        {
            try { _gzip?.Dispose(); } catch { }
            try { _file?.Dispose(); } catch { }
            Reset();
        }

        private static void Reset()
        {
            _gzip = null;
            _file = null;
            _path = null;
            _messages = 0;
            _rawBytes = 0;
        }
    }
}
