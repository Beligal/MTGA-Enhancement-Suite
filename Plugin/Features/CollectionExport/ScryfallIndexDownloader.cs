using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace MTGAEnhancementSuite.Features.CollectionExport
{
    /// <summary>
    /// Scarica l'indice Scryfall delle stampe Arena e lo salva su disco.
    ///
    /// Gira su un thread suo: l'interfaccia legge <see cref="Status"/> e non
    /// aspetta mai la rete. Sono ~125 pagine da 175 carte (misurato il
    /// 2026-10-14: 21.399 stampe, 123 pagine, ~110 MB di JSON non compresso,
    /// che con gzip viaggiano molto piu' leggeri).
    ///
    /// **Il ritmo e' lento apposta.** A ~8 richieste al secondo Scryfall ha
    /// risposto 429 dopo 20 pagine, con <c>Retry-After</c> di quasi un minuto.
    /// Qui si aspetta <see cref="PageDelayMs"/> fra una pagina e l'altra e, se
    /// arriva comunque un 429, si rispetta il Retry-After. Un download dura
    /// quindi un paio di minuti, e si fa una volta ogni tanto.
    ///
    /// Il file su disco si sostituisce solo a download COMPLETO e riletto:
    /// un'interruzione lascia l'indice vecchio com'era.
    /// </summary>
    internal sealed class ScryfallIndexDownloader
    {
        public const int PageDelayMs = 600;
        private const int MaxPages = 400;          // guardia contro un next_page che non finisce
        private const int Max429Waits = 5;

        private readonly string _path;
        private readonly string _userAgent;
        private Thread _thread;

        private volatile string _status = "";
        private volatile bool _running;
        private volatile bool _succeeded;
        private volatile bool _cancel;

        public string Status => _status;
        public bool Running => _running;
        public bool Succeeded => _succeeded;
        public ScryfallArenaIndex Result { get; private set; }

        public ScryfallIndexDownloader(string indexPath, string userAgent)
        {
            _path = indexPath;
            _userAgent = userAgent;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _succeeded = false;
            _cancel = false;
            _status = "Starting…";
            _thread = new Thread(Run) { IsBackground = true, Name = "MTGAPlus-ScryfallIndex" };
            _thread.Start();
        }

        public void Cancel() { _cancel = true; }

        private void Run()
        {
            try
            {
                _status = "Downloading set list…";
                var mtgo = ScryfallArenaIndex.ParseMtgoCodes(Get(ScryfallArenaIndex.SetsUrl));

                var prints = new List<ScryfallPrint>(22000);
                string url = ScryfallArenaIndex.SearchUrl;
                int page = 0, total = -1;
                while (url != null)
                {
                    if (_cancel) { _status = "Cancelled"; return; }
                    if (++page > MaxPages) throw new InvalidOperationException("too many pages");
                    Thread.Sleep(PageDelayMs);
                    var json = Get(url);
                    prints.AddRange(ScryfallArenaIndex.ParseSearchPage(json, mtgo, out url, out int t));
                    if (t > 0) total = t;
                    _status = total > 0
                        ? $"Downloading card list… {prints.Count:N0} / {total:N0}"
                        : $"Downloading card list… {prints.Count:N0}";
                }

                // Un totale dichiarato e non raggiunto vuol dire pagine perse:
                // meglio nessun indice nuovo che uno con buchi.
                if (total > 0 && prints.Count < total)
                    throw new InvalidOperationException($"incomplete download: {prints.Count} of {total}");

                var index = new ScryfallArenaIndex(prints, DateTime.UtcNow);
                ScryfallIndexStore.Save(_path, index);
                Result = ScryfallIndexStore.Load(_path);   // riletto dal disco, non dalla memoria
                _succeeded = Result != null;
                _status = _succeeded ? $"Done: {Result.Count:N0} printings" : "Saved index could not be read back";
            }
            catch (Exception ex)
            {
                _status = "Failed: " + ex.Message;
            }
            finally
            {
                _running = false;
            }
        }

        private string Get(string url)
        {
            for (int attempt = 0; ; attempt++)
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = _userAgent;
                req.Accept = "application/json";
                req.Timeout = 30000;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return reader.ReadToEnd();
                }
                catch (WebException ex) when (ex.Response is HttpWebResponse r && (int)r.StatusCode == 429 && attempt < Max429Waits)
                {
                    int wait = 60;
                    if (int.TryParse(r.Headers["Retry-After"], out int ra) && ra > 0) wait = ra;
                    r.Dispose();
                    for (int s = wait + 1; s > 0 && !_cancel; s--)
                    {
                        _status = $"Scryfall asked us to slow down — resuming in {s}s";
                        Thread.Sleep(1000);
                    }
                    if (_cancel) throw new OperationCanceledException();
                }
            }
        }
    }

    /// <summary>Lettura e scrittura del file dell'indice.</summary>
    internal static class ScryfallIndexStore
    {
        /// <summary>
        /// Null se il file non c'e' o non e' integro, con il motivo in
        /// <paramref name="error"/> (null se semplicemente non c'e'): in
        /// entrambi i casi l'export va avanti senza indice, e l'interfaccia lo dice.
        /// </summary>
        public static ScryfallArenaIndex TryLoad(string path, out string error)
        {
            error = null;
            try { return Load(path); }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is OverflowException)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Null se il file non c'e'; lancia se c'e' ma non e' integro.</summary>
        public static ScryfallArenaIndex Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using (var r = new StreamReader(path, new UTF8Encoding(false)))
                return ScryfallArenaIndex.Load(r);
        }

        /// <summary>Temporaneo, poi rinomina: o c'e' il file nuovo intero, o resta il vecchio.</summary>
        public static void Save(string path, ScryfallArenaIndex index)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
                index.Save(w);
            // Controllo sul file scritto, non sulla memoria (regola 47).
            using (var r = new StreamReader(tmp, new UTF8Encoding(false)))
                ScryfallArenaIndex.Load(r);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
