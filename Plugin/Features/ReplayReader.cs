using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Google.Protobuf;
using Wotc.Mtgo.Gre.External.Messaging;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Rilegge un file scritto da <see cref="ReplayRecorder"/>.
    ///
    /// Il formato e' quello che scrive il registratore e niente di piu':
    /// lunghezza a 4 byte in little-endian, poi quel numero di byte di
    /// `GREToClientMessage` in protobuf, ripetuto fino alla fine; il tutto
    /// dentro un gzip.
    ///
    /// **Esiste prima dell'esportatore, di proposito.** Quel formato non era
    /// mai stato riletto da nessuno: scritto una volta e mai provato. Costruirci
    /// sopra un visualizzatore senza prima avere qualcosa che dica "ho trovato
    /// N fotogrammi in un file da K byte" vorrebbe dire scoprire un difetto del
    /// formato attraverso i sintomi del visualizzatore, cioe' nel punto piu'
    /// lontano possibile dalla causa.
    ///
    /// Un file troncato — il gioco chiuso a meta' partita — non e' un errore:
    /// si restituisce quello che si e' letto e si dice quanto manca. Un replay
    /// parziale e' meglio di nessun replay.
    /// </summary>
    internal static class ReplayReader
    {
        /// <summary>Cosa e' stato trovato in un file.</summary>
        internal sealed class Result
        {
            public List<GREToClientMessage> Messages = new List<GREToClientMessage>();
            public long CompressedBytes;
            public long RawBytes;
            public bool Truncated;
            public string Error;

            public bool Ok => Error == null && Messages.Count > 0;
        }

        /// <summary>
        /// Legge tutto il file. <paramref name="maxMessages"/> a un valore
        /// positivo si ferma prima: serve a dare un'occhiata a un file senza
        /// tirarsi in memoria una partita intera.
        /// </summary>
        public static Result Read(string path, int maxMessages = 0)
        {
            var result = new Result();

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                result.Error = "file non trovato";
                return result;
            }

            try { result.CompressedBytes = new FileInfo(path).Length; } catch { }

            try
            {
                using (var file = File.OpenRead(path))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                {
                    var header = new byte[4];
                    while (true)
                    {
                        if (!ReadExactly(gzip, header, 4))
                        {
                            // Fine pulita: zero byte letti. Altrimenti il file
                            // si e' interrotto a meta' intestazione.
                            break;
                        }

                        int length = BitConverter.ToInt32(header, 0);
                        if (length <= 0 || length > 64 * 1024 * 1024)
                        {
                            result.Truncated = true;
                            result.Error = $"lunghezza fotogramma non plausibile ({length}) dopo {result.Messages.Count} messaggi";
                            break;
                        }

                        var payload = new byte[length];
                        if (!ReadExactly(gzip, payload, length))
                        {
                            result.Truncated = true;
                            break;
                        }

                        result.RawBytes += 4 + length;

                        try
                        {
                            result.Messages.Add(GREToClientMessage.Parser.ParseFrom(payload));
                        }
                        catch (Exception ex)
                        {
                            result.Truncated = true;
                            result.Error = $"messaggio {result.Messages.Count} illeggibile: {ex.Message}";
                            break;
                        }

                        if (maxMessages > 0 && result.Messages.Count >= maxMessages) break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Un gzip tagliato a meta' solleva proprio qui. Se qualcosa e'
                // stato letto lo teniamo.
                result.Truncated = true;
                if (result.Messages.Count == 0) result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Legge un file e scrive nel log cosa ha trovato — **la misura vera del
        /// formato**, che finora era solo una stima.
        /// </summary>
        public static Result Inspect(string path)
        {
            var r = Read(path);
            string name = Path.GetFileName(path ?? "?");

            if (r.Error != null && r.Messages.Count == 0)
            {
                Plugin.Log.LogWarning($"Replay '{name}': NON leggibile ({r.Error}).");
                return r;
            }

            int states = 0;
            foreach (var m in r.Messages)
                if (m != null && m.Type == GREMessageType.GameStateMessage) states++;

            double ratio = r.RawBytes > 0 ? (double)r.CompressedBytes / r.RawBytes : 0;

            Plugin.Log.LogInfo(
                $"Replay '{name}': {r.Messages.Count} messaggi ({states} di stato), " +
                $"{r.RawBytes / 1024} KB non compressi -> {r.CompressedBytes / 1024} KB su disco " +
                $"(compressione {ratio:P0})" +
                (r.Truncated ? $"  ATTENZIONE: file troncato{(r.Error != null ? " — " + r.Error : "")}" : ""));

            return r;
        }

        /// <summary>
        /// I replay presenti per questo account, dal piu' recente.
        /// </summary>
        public static List<string> ListFiles()
        {
            var list = new List<string>();
            var dir = ReplayRecorder.Directory();
            if (dir == null) return list;

            try
            {
                var files = Directory.GetFiles(dir, "*.mtgarep.gz");
                Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                list.AddRange(files);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Replay: elenco non leggibile ({ex.Message}).");
            }
            return list;
        }

        /// <summary>
        /// <c>GZipStream</c> puo' restituire meno byte di quanti chiesti anche
        /// quando il file continua: va richiamato finche' non ha finito. Un solo
        /// <c>Read</c> sembra funzionare sui file piccoli e comincia a perdere
        /// pezzi su quelli grandi — cioe' proprio sulle partite lunghe.
        /// </summary>
        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int done = 0;
            while (done < count)
            {
                int n = stream.Read(buffer, done, count - done);
                if (n <= 0) return false;
                done += n;
            }
            return true;
        }
    }
}
