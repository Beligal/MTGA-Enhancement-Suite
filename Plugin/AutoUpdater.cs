using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MTGAEnhancementSuite.UI;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using UnityEngine;

namespace MTGAEnhancementSuite
{
    /// <summary>
    /// Checks GitHub for a newer release on startup, verifies the release
    /// manifest signature (Ed25519) and file hashes (SHA-256) before staging
    /// the update for the bootstrapper to apply on next launch.
    /// </summary>
    internal static class AutoUpdater
    {
        // Le release del fork. L'indirizzo e' compilato dentro il DLL che
        // l'utente ha installato: cambiarlo raggiunge solo chi installa una
        // build nuova, non chi ha gia' una versione vecchia in giro.
        private const string GitHubApiUrl = "https://api.github.com/repos/Beligal/MTGA-Enhancement-Suite/releases/latest";
        private static readonly Version CurrentVersion = new Version(PluginInfo.VERSION);

        // Chiave pubblica Ed25519 con cui si verifica la firma del manifest
        // (base64 dei 32 byte grezzi). La meta' privata sta in signing_key.pem,
        // fuori dal repository: senza quella non si puo' pubblicare nulla che
        // queste installazioni accettino, e per lo stesso motivo nessun altro
        // puo' spacciare un aggiornamento per nostro.
        //
        // Questa e' la chiave del fork, diversa da quella di MayerDaniel: le
        // installazioni nate dalle sue release rifiutano cio' che firmiamo noi,
        // e viceversa. Il passaggio da una all'altra si fa reinstallando.
        private const string PublicKeyBase64 = "QFBVvAx7+8L/wyS9X6iubH5Sn3ezZuz1KbUc3BGlrLk=";

        // Il protocollo dello staging, condiviso con MTGAESBootstrapper
        // (Bootstrapper.cs). Le due assembly non condividono codice: questi
        // quattro nomi sono scritti a mano in tutti e due i file, e cambiarne
        // uno solo da una parte rompe l'aggiornamento in silenzio.
        //
        //   .download        scaricato, non ancora verificato. Il bootstrapper
        //                    non lo guarda: non e' installabile per costruzione.
        //   .pending         scaricato E verificato, pronto da installare.
        //   staged.ok        elenco dei file con i loro hash e quanti sono.
        //                    Pubblicato per temporaneo e rinomina, cosi' o c'e'
        //                    tutto o non c'e': la sua presenza rende il gruppo
        //                    installabile.
        //   transaction.txt  il registro. Scritto PRIMA di toccare qualunque file
        //                    installato, elenca quali sono e porta uno stato:
        //                    installing / installed / confirmed. Solo
        //                    "confirmed" autorizza a cancellare un backup; gli
        //                    altri due vogliono dire "rimetti a posto". Non
        //                    esiste nessuna regola che legga l'ASSENZA del
        //                    registro come permesso di fare pulizia.
        internal const string DownloadSuffix = ".download";
        internal const string PendingSuffix = ".pending";
        internal const string StagedMarkerName = "staged.ok";
        internal const string JournalName = "transaction.txt";
        internal const string StateConfirmed = "confirmed";

        public static IEnumerator CheckForUpdate()
        {
            Plugin.Log.LogInfo($"AutoUpdater: Checking for updates (current: {PluginInfo.VERSION})");

            // Small delay to not compete with auth and other startup tasks
            yield return new WaitForSeconds(10f);

            string json = null;
            Exception fetchError = null;

            // Fetch release info on a background thread
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(GitHubApiUrl);
                    request.UserAgent = $"MTGAEnhancementSuite/{PluginInfo.VERSION}";
                    request.Timeout = 15000;

                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        json = reader.ReadToEnd();
                    }
                }
                catch (Exception ex)
                {
                    fetchError = ex;
                }
            });
            thread.IsBackground = true;
            thread.Start();

            while (thread.IsAlive)
                yield return new WaitForSeconds(0.5f);

            if (fetchError != null)
            {
                Plugin.Log.LogWarning($"AutoUpdater: Failed to check for updates: {fetchError.Message}");
                yield break;
            }

            if (string.IsNullOrEmpty(json))
            {
                Plugin.Log.LogWarning("AutoUpdater: Empty response from GitHub");
                yield break;
            }

            // Parse release info
            JObject release;
            try
            {
                release = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"AutoUpdater: Failed to parse release JSON: {ex.Message}");
                yield break;
            }

            var tagName = release["tag_name"]?.ToString();
            if (string.IsNullOrEmpty(tagName))
            {
                Plugin.Log.LogWarning("AutoUpdater: No tag_name in release");
                yield break;
            }

            var versionStr = tagName.TrimStart('v');
            Version remoteVersion;
            if (!Version.TryParse(versionStr, out remoteVersion))
            {
                Plugin.Log.LogWarning($"AutoUpdater: Could not parse version from tag: {tagName}");
                yield break;
            }

            Plugin.Log.LogInfo($"AutoUpdater: Latest release: {remoteVersion}, current: {CurrentVersion}");

            if (remoteVersion <= CurrentVersion)
            {
                Plugin.Log.LogInfo("AutoUpdater: Up to date");
                yield break;
            }

            // Find asset URLs
            var assets = release["assets"] as JArray;
            if (assets == null || assets.Count == 0)
            {
                Plugin.Log.LogWarning("AutoUpdater: No assets in release");
                yield break;
            }

            string dllUrl = null;
            string bootstrapperUrl = null;
            string manifestUrl = null;

            foreach (var asset in assets)
            {
                var name = asset["name"]?.ToString();
                var url = asset["browser_download_url"]?.ToString();
                if (name == "MTGAEnhancementSuite.dll") dllUrl = url;
                else if (name == "MTGAESBootstrapper.dll") bootstrapperUrl = url;
                else if (name == "manifest.json") manifestUrl = url;
            }

            if (dllUrl == null)
            {
                Plugin.Log.LogWarning("AutoUpdater: No DLL found in release assets");
                yield break;
            }

            Toast.Info($"MTGA+ update available: v{remoteVersion}. Downloading...");
            Plugin.Log.LogInfo($"AutoUpdater: Downloading update v{remoteVersion}");

            var pluginDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            var updateDir = Path.Combine(pluginDir, "update");

            bool downloadSuccess = false;
            string downloadFailReason = null;

            var dlThread = new System.Threading.Thread(() =>
            {
                try
                {
                    if (!Directory.Exists(updateDir))
                        Directory.CreateDirectory(updateDir);

                    // Scarti di un tentativo interrotto prima d'ora.
                    ClearStagingArtifacts(updateDir);

                    // Il manifest e' obbligatorio: e' l'unica cosa firmata, e senza
                    // di lui non c'e' modo di sapere se quello che stiamo per
                    // installare arriva da noi. Prima una release senza manifest
                    // faceva saltare il controllo e l'aggiornamento passava lo
                    // stesso: bastava omettere un file per annullare la firma.
                    if (manifestUrl == null)
                    {
                        downloadFailReason = "release without manifest.json — update rejected";
                        return;
                    }

                    var manifestPath = Path.Combine(updateDir, "manifest.json");
                    DownloadFile(manifestUrl, manifestPath);
                    var manifest = JObject.Parse(File.ReadAllText(manifestPath));

                    if (!VerifyManifestSignature(manifest))
                    {
                        downloadFailReason = "Manifest signature verification FAILED — update rejected";
                        return;
                    }
                    Plugin.Log.LogInfo("AutoUpdater: Manifest signature verified");

                    // Si scarica su *.download, NON su *.pending. Prima il file
                    // arrivava direttamente con l'estensione che il bootstrapper
                    // considera installabile, e la verifica dell'hash veniva
                    // dopo: se il gioco veniva chiuso a meta' scaricamento
                    // restava li' un DLL troncato e pronto da installare, e la
                    // pulizia prevista per gli errori non poteva scattare perche'
                    // il processo non c'era piu'. Adesso niente diventa
                    // installabile finche' non e' stato verificato tutto quanto.
                    var staged = new List<string>();

                    var dllDownload = Path.Combine(updateDir, "MTGAEnhancementSuite.dll" + DownloadSuffix);
                    DownloadFile(dllUrl, dllDownload);

                    // L'hash del DLL e' obbligatorio quanto il manifest: e' il file
                    // che poi viene eseguito dentro il gioco. Un manifest firmato ma
                    // senza la sua voce significa release malformata, non "va bene".
                    var expectedDllHash = manifest["files"]?["MTGAEnhancementSuite.dll"]?.ToString();
                    if (expectedDllHash == null)
                    {
                        downloadFailReason = "manifest has no hash for MTGAEnhancementSuite.dll — update rejected";
                        return;
                    }
                    if (!VerifyFileHash(dllDownload, expectedDllHash))
                    {
                        downloadFailReason = "DLL hash mismatch — update rejected";
                        return;
                    }
                    Plugin.Log.LogInfo("AutoUpdater: DLL hash verified");
                    staged.Add("MTGAEnhancementSuite.dll");

                    // Download bootstrapper (if available)
                    if (bootstrapperUrl != null)
                    {
                        var bsDownload = Path.Combine(updateDir, "MTGAESBootstrapper.dll" + DownloadSuffix);
                        DownloadFile(bootstrapperUrl, bsDownload);

                        // Anche questo e' codice che verra' eseguito (all'avvio, per
                        // spostare i file al loro posto): se il manifest non lo elenca,
                        // la release e' malformata e non la installiamo.
                        var expectedBsHash = manifest["files"]?["MTGAESBootstrapper.dll"]?.ToString();
                        if (expectedBsHash == null || !VerifyFileHash(bsDownload, expectedBsHash))
                        {
                            downloadFailReason = expectedBsHash == null
                                ? "manifest has no hash for MTGAESBootstrapper.dll — update rejected"
                                : "Bootstrapper hash mismatch — update rejected";
                            return;
                        }
                        Plugin.Log.LogInfo("AutoUpdater: Bootstrapper hash verified");
                        staged.Add("MTGAESBootstrapper.dll");
                    }

                    // Tutto scaricato e tutto verificato. Adesso si pubblica, e
                    // l'ordine e' la garanzia: prima i rinomini a *.pending, il
                    // marcatore per ULTIMO. Interrotti nel mezzo, al prossimo
                    // avvio il bootstrapper trova dei .pending senza marcatore e
                    // li butta via invece di installarli.
                    foreach (var name in staged)
                    {
                        var from = Path.Combine(updateDir, name + DownloadSuffix);
                        var to = Path.Combine(updateDir, name + PendingSuffix);
                        if (File.Exists(to)) File.Delete(to);
                        File.Move(from, to);
                    }

                    var marker = new StringBuilder();
                    marker.AppendLine("version=" + remoteVersion);
                    marker.AppendLine("files=" + staged.Count);
                    foreach (var name in staged)
                        marker.AppendLine(name + "=" + Sha256Hex(Path.Combine(updateDir, name + PendingSuffix)));

                    // Anche il marcatore va pubblicato per temporaneo e rinomina,
                    // ed e' lo stesso principio applicato ai mazzi. Scriverlo per
                    // ultimo dice QUANDO comincia a esistere, non che sia
                    // completo: un WriteAllText interrotto a meta' puo' lasciare
                    // un elenco parziale, e il bootstrapper installerebbe un
                    // pezzo dell'aggiornamento credendolo intero. Il rinomina e'
                    // indivisibile, e la riga files= rende la completezza una
                    // cosa che si verifica invece che si spera.
                    var markerPath = Path.Combine(updateDir, StagedMarkerName);
                    var markerTmp = markerPath + ".tmp";
                    File.WriteAllText(markerTmp, marker.ToString());
                    if (File.Exists(markerPath)) File.Delete(markerPath);
                    File.Move(markerTmp, markerPath);

                    downloadSuccess = true;
                }
                catch (Exception ex)
                {
                    downloadFailReason = ex.Message;
                }
            });
            dlThread.IsBackground = true;
            dlThread.Start();

            while (dlThread.IsAlive)
                yield return new WaitForSeconds(0.5f);

            if (downloadSuccess)
            {
                Toast.Success($"MTGA+ v{remoteVersion} downloaded. Restart the game to apply.");
                Plugin.Log.LogInfo("AutoUpdater: Update staged successfully");
                PerPlayerLog.Info($"Update staged: v{CurrentVersion} -> v{remoteVersion}");
            }
            else
            {
                // Ripulire resta necessario, ma non e' piu' l'unica difesa:
                // adesso gli scarti si chiamano *.download e il bootstrapper non
                // li guarda, e un gruppo senza marcatore viene scartato comunque.
                // Questo e' il caso in cui siamo ancora vivi per farlo noi.
                //
                // Si tolgono i soli scarti dello staging e non la cartella
                // intera, perche' li' dentro puo' esserci applied.ok, cioe' un
                // aggiornamento appena applicato che aspetta conferma: portarlo
                // via vorrebbe dire perdere la possibilita' di tornare indietro.
                try
                {
                    ClearStagingArtifacts(updateDir);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning(
                        $"AutoUpdater: could not clean up {updateDir}: {ex.Message}. " +
                        "Delete it by hand before restarting.");
                }

                Toast.Warning("Failed to download MTGA+ update.");
                Plugin.Log.LogError($"AutoUpdater: Update failed: {downloadFailReason}");
            }
        }

        /// <summary>
        /// Chiamata dal plugin appena e' in esecuzione. La sua esecuzione E' la
        /// conferma: il DLL nuovo si e' caricato, quindi i backup che il
        /// bootstrapper aveva tenuto da parte possono andarsene.
        ///
        /// Se non gira — perche' la build nuova non parte, o perche' e' partita
        /// male — il registro e i backup sopravvivono, e al prossimo avvio il
        /// bootstrapper rimette a posto la versione precedente. Prima i backup
        /// venivano cancellati dentro lo stesso Awake che applicava
        /// l'aggiornamento, cioe' prima che qualcuno avesse visto il plugin
        /// nuovo funzionare: se non partiva, non c'era piu' niente a cui tornare.
        ///
        /// Il chiamante decide QUANDO siamo "in esecuzione": non basta essere
        /// entrati in Awake, perche' i blocchi che applicano le patch
        /// intercettano i propri errori e si arriverebbe qui lo stesso.
        /// </summary>
        public static void ConfirmUpdateApplied()
        {
            try
            {
                var pluginDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                var updateDir = Path.Combine(pluginDir, "update");
                var journalPath = Path.Combine(updateDir, JournalName);
                if (!File.Exists(journalPath)) return;

                var version = "?";
                var files = new List<string>();
                foreach (var raw in File.ReadAllLines(journalPath))
                {
                    var line = raw.Trim();
                    var eq = line.IndexOf('=');
                    if (eq <= 0 || eq == line.Length - 1) continue;
                    var key = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim();
                    if (key == "version") version = value;
                    else if (key == "file") files.Add(value);
                }

                // Prima si SCRIVE che la transazione e' confermata, poi si tocca
                // qualcosa. L'ordine e' tutto: morendo fra le due cose, il
                // bootstrapper al prossimo avvio legge "confirmed" e finisce le
                // pulizie, invece di leggere uno stato da recuperare e disfare
                // un aggiornamento che invece funziona.
                //
                // Se questa scrittura non riesce non si cancella niente: si
                // resta in uno stato da recuperare, che e' il lato giusto in cui
                // sbagliare.
                if (!SetJournalState(journalPath, StateConfirmed))
                {
                    Plugin.Log.LogWarning(
                        "AutoUpdater: could not mark the update confirmed — leaving the backups in place.");
                    return;
                }

                // Solo i backup che QUESTA transazione ha creato, non tutti i
                // *.bak della cartella: un backup di qualcun altro non e' roba
                // nostra da cancellare, ed e' il registro a dire quali sono.
                var removed = 0;
                var allGone = true;
                foreach (var name in files)
                {
                    var bak = Path.Combine(pluginDir, name + ".bak");
                    try { if (File.Exists(bak)) { File.Delete(bak); removed++; } }
                    catch (Exception ex)
                    {
                        allGone = false;
                        Plugin.Log.LogWarning(
                            $"AutoUpdater: could not remove backup {name}.bak: {ex.Message}");
                    }
                }

                if (!allGone)
                {
                    // Il registro resta, con lo stato "confirmed": il prossimo
                    // avvio finira' le pulizie e non ripristinera' niente.
                    Plugin.Log.LogInfo(
                        $"AutoUpdater: update v{version} confirmed running — {removed} backup(s) removed, "
                        + "the rest will be cleared on the next launch");
                    return;
                }

                File.Delete(journalPath);
                try { if (Directory.GetFiles(updateDir).Length == 0) Directory.Delete(updateDir); } catch { }

                Plugin.Log.LogInfo(
                    $"AutoUpdater: update v{version} confirmed running — {removed} backup(s) removed");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"AutoUpdater.ConfirmUpdateApplied: {ex.Message}");
            }
        }

        /// <summary>
        /// Riscrive lo stato dentro il registro, per temporaneo e rinomina, cosi'
        /// che non possa esistere un registro mezzo scritto. Restituisce false se
        /// non c'e' riuscita: in quel caso il chiamante non deve cancellare
        /// niente, perche' sul disco lo stato precedente vale ancora.
        /// </summary>
        private static bool SetJournalState(string journalPath, string state)
        {
            try
            {
                var lines = new List<string>();
                var written = false;
                foreach (var raw in File.ReadAllLines(journalPath))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("state=", StringComparison.Ordinal))
                    {
                        if (written) continue;
                        lines.Add("state=" + state);
                        written = true;
                    }
                    else lines.Add(raw);
                }
                if (!written) lines.Insert(0, "state=" + state);

                var tmp = journalPath + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(journalPath)) File.Delete(journalPath);
                File.Move(tmp, journalPath);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"AutoUpdater.SetJournalState: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Toglie gli scarti dello staging: scaricati, verificati-ma-non-ancora
        /// -installati, marcatore di staging e manifest. NON tocca il registro.
        /// </summary>
        private static void ClearStagingArtifacts(string updateDir)
        {
            if (!Directory.Exists(updateDir)) return;

            foreach (var pattern in new[] { "*" + DownloadSuffix, "*" + PendingSuffix })
                foreach (var f in Directory.GetFiles(updateDir, pattern))
                    try { File.Delete(f); } catch { }

            foreach (var f in Directory.GetFiles(updateDir, "*.tmp"))
                try { File.Delete(f); } catch { }

            foreach (var name in new[] { StagedMarkerName, "manifest.json" })
            {
                var p = Path.Combine(updateDir, name);
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }

        /// <summary>SHA-256 di un file, in esadecimale minuscolo.</summary>
        private static string Sha256Hex(string filePath)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Verifies the Ed25519 signature on the manifest.
        /// The signed content is the canonical JSON of {version, files} with sorted keys.
        /// </summary>
        private static bool VerifyManifestSignature(JObject manifest)
        {
            try
            {
                var signatureB64 = manifest["signature"]?.ToString();
                if (string.IsNullOrEmpty(signatureB64))
                {
                    Plugin.Log.LogWarning("AutoUpdater: No signature in manifest");
                    return false;
                }

                // Reconstruct the signed content (files + version, no signature field)
                // JObject preserves insertion order — files first, version second
                var contentObj = new JObject
                {
                    ["files"] = manifest["files"],
                    ["version"] = manifest["version"]
                };
                var contentToVerify = contentObj.ToString(Newtonsoft.Json.Formatting.None);

                var signatureBytes = Convert.FromBase64String(signatureB64);
                var publicKeyBytes = Convert.FromBase64String(PublicKeyBase64);

                var publicKeyParams = new Ed25519PublicKeyParameters(publicKeyBytes, 0);
                var verifier = new Ed25519Signer();
                verifier.Init(false, publicKeyParams);

                var contentBytes = Encoding.UTF8.GetBytes(contentToVerify);
                verifier.BlockUpdate(contentBytes, 0, contentBytes.Length);

                return verifier.VerifySignature(signatureBytes);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"AutoUpdater: Signature verification error: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Verifies a file's SHA-256 hash matches the expected value.
        /// </summary>
        private static bool VerifyFileHash(string filePath, string expectedHash)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    var hash = sha.ComputeHash(stream);
                    var hashHex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    if (hashHex != expectedHash.ToLowerInvariant())
                    {
                        Plugin.Log.LogError($"AutoUpdater: Hash mismatch for {Path.GetFileName(filePath)}: expected {expectedHash}, got {hashHex}");
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"AutoUpdater: Hash check error: {ex}");
                return false;
            }
        }

        private static void DownloadFile(string url, string destPath)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = $"MTGAEnhancementSuite/{PluginInfo.VERSION}";
            request.Timeout = 30000;
            request.AllowAutoRedirect = true;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
            {
                stream.CopyTo(fs);
            }
        }
    }
}
