using System;
using System.Collections;
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
            string configUrl = null;
            string bootstrapperUrl = null;
            string manifestUrl = null;

            foreach (var asset in assets)
            {
                var name = asset["name"]?.ToString();
                var url = asset["browser_download_url"]?.ToString();
                if (name == "MTGAEnhancementSuite.dll") dllUrl = url;
                else if (name == "config.json") configUrl = url;
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

                    // Download DLL
                    var dllPath = Path.Combine(updateDir, "MTGAEnhancementSuite.dll.pending");
                    DownloadFile(dllUrl, dllPath);

                    // L'hash del DLL e' obbligatorio quanto il manifest: e' il file
                    // che poi viene eseguito dentro il gioco. Un manifest firmato ma
                    // senza la sua voce significa release malformata, non "va bene".
                    var expectedDllHash = manifest["files"]?["MTGAEnhancementSuite.dll"]?.ToString();
                    if (expectedDllHash == null)
                    {
                        downloadFailReason = "manifest has no hash for MTGAEnhancementSuite.dll — update rejected";
                        File.Delete(dllPath);
                        return;
                    }
                    if (!VerifyFileHash(dllPath, expectedDllHash))
                    {
                        downloadFailReason = "DLL hash mismatch — update rejected";
                        File.Delete(dllPath);
                        return;
                    }
                    Plugin.Log.LogInfo("AutoUpdater: DLL hash verified");

                    // Download config.json (not locked, overwrite directly)
                    if (configUrl != null)
                    {
                        var configTempPath = Path.Combine(updateDir, "config.json.tmp");
                        DownloadFile(configUrl, configTempPath);

                        if (manifest != null)
                        {
                            var expectedHash = manifest["files"]?["config.json"]?.ToString();
                            if (expectedHash != null && !VerifyFileHash(configTempPath, expectedHash))
                            {
                                downloadFailReason = "config.json hash mismatch — update rejected";
                                File.Delete(dllPath);
                                File.Delete(configTempPath);
                                return;
                            }
                            Plugin.Log.LogInfo("AutoUpdater: config.json hash verified");
                        }

                        // Move to final location
                        var configFinalPath = Path.Combine(pluginDir, "config.json");
                        File.Copy(configTempPath, configFinalPath, true);
                        File.Delete(configTempPath);
                    }

                    // Download bootstrapper (if available)
                    if (bootstrapperUrl != null)
                    {
                        var bsPath = Path.Combine(updateDir, "MTGAESBootstrapper.dll.pending");
                        DownloadFile(bootstrapperUrl, bsPath);

                        // Anche questo e' codice che verra' eseguito (all'avvio, per
                        // spostare i file al loro posto): se il manifest non lo elenca,
                        // la release e' malformata e non la installiamo.
                        var expectedBsHash = manifest["files"]?["MTGAESBootstrapper.dll"]?.ToString();
                        if (expectedBsHash == null || !VerifyFileHash(bsPath, expectedBsHash))
                        {
                            downloadFailReason = expectedBsHash == null
                                ? "manifest has no hash for MTGAESBootstrapper.dll — update rejected"
                                : "Bootstrapper hash mismatch — update rejected";
                            File.Delete(dllPath);
                            File.Delete(bsPath);
                            return;
                        }
                        Plugin.Log.LogInfo("AutoUpdater: Bootstrapper hash verified");
                    }

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
                // Ripulire non e' cortesia: e' necessario. I file scaricati si
                // chiamano *.pending, e al prossimo avvio il bootstrapper mette al
                // loro posto *qualunque* .pending trovi, senza guardarci dentro. Se
                // la rete cade a meta' download il file resta li' troncato, e le
                // uscite controllate qui sopra non lo coprono: cancellarlo per
                // eccezione e' l'unico punto che le copre tutte.
                try
                {
                    if (Directory.Exists(updateDir))
                        Directory.Delete(updateDir, true);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning(
                        $"AutoUpdater: could not clean up {updateDir}: {ex.Message}. " +
                        "Delete it by hand before restarting, or a partial download may be installed.");
                }

                Toast.Warning("Failed to download MTGA+ update.");
                Plugin.Log.LogError($"AutoUpdater: Update failed: {downloadFailReason}");
            }
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
