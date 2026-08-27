using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace MTGAEnhancementSuite.Firebase
{
    /// <summary>
    /// Uploads the MTGA Raw_CardDatabase_*.mtga file to Cloud Storage when the
    /// locally-installed MTGA version is strictly newer than the version that
    /// produced the server's /cardMetadata. Runs once per session, after auth.
    ///
    /// Why version, not hash: a user reinstalling/rolling-back to an older
    /// build would have a different filename hash, but their DB is *older*
    /// than what the server already has. Hash equality is necessary
    /// (we don't re-upload identical content) but not sufficient (different
    /// hashes don't imply newer).
    ///
    /// MTGA's build version lives in `<install>/version` as a JSON object:
    ///   { "Versions": { "0.1.11950.1257485": "4/1/26" } }
    /// The dotted number is monotonic across patches (4-component lexicographic).
    ///
    /// Two-step upload (the raw file is too big for Cloud Run's 32MB body cap):
    ///   1. POST /requestCardDbUpload with hash + mtgaVersion + Bearer token.
    ///      Function compares vs server, returns a v4 signed PUT URL or skip.
    ///   2. PUT the gzipped bytes directly to that URL — no body cap.
    ///   3. Storage trigger parseUploadedCardDb handles parsing.
    /// </summary>
    internal static class CardDbUploader
    {
        private static readonly string[] CardDbSearchRoots = new[]
        {
            @"C:\Program Files\Wizards of the Coast\MTGA\MTGA_Data\Downloads\Raw",
            @"C:\Program Files (x86)\Wizards of the Coast\MTGA\MTGA_Data\Downloads\Raw",
            @"C:\Program Files (x86)\Steam\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
            @"C:\Program Files\Steam\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
            @"D:\SteamLibrary\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
            @"E:\SteamLibrary\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
            @"R:\SteamLibrary\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
        };

        private static bool _hasRunThisSession;

        public static IEnumerator CheckAndUpload()
        {
            if (_hasRunThisSession) yield break;
            _hasRunThisSession = true;

            string dbPath = null;
            try { dbPath = FindLatestCardDb(); }
            catch (Exception ex) { Plugin.Log.LogWarning($"CardDbUploader: error finding card DB: {ex.Message}"); }

            if (string.IsNullOrEmpty(dbPath))
            {
                Plugin.Log.LogInfo("CardDbUploader: no MTGA card DB found in known locations, skipping");
                yield break;
            }

            string hash = ExtractHashFromFilename(dbPath);
            if (string.IsNullOrEmpty(hash))
            {
                Plugin.Log.LogInfo($"CardDbUploader: could not extract hash from filename: {Path.GetFileName(dbPath)}");
                yield break;
            }

            // Read the installed MTGA build version. If we can't, abort —
            // we don't want to upload without a version because the server
            // refuses unsupervised uploads.
            string mtgaVersion = ReadInstalledMtgaVersion(dbPath);
            if (string.IsNullOrEmpty(mtgaVersion))
            {
                Plugin.Log.LogWarning("CardDbUploader: versione MTGA non determinabile, upload saltato");
                yield break;
            }

            // Fetch server's current cardMetadataVersion and decide whether to upload.
            JObject serverVersion = null;
            bool fetchDone = false;
            FirebaseClient.Instance.DatabaseGet("cardMetadataVersion", data =>
            {
                if (data != null && data.Type == JTokenType.Object)
                    serverVersion = (JObject)data;
                fetchDone = true;
            });
            float timeout = 10f;
            while (!fetchDone && timeout > 0f)
            {
                yield return new WaitForSeconds(0.25f);
                timeout -= 0.25f;
            }

            string serverHash = serverVersion?["hash"]?.ToString();
            string serverMtgaVersion = serverVersion?["mtgaVersion"]?.ToString();
            var localParts = ParseVersion(mtgaVersion);
            var serverParts = ParseVersion(serverMtgaVersion);

            // Three skip cases, in order of importance:
            //  1. Server already has the same hash AND >= our version → identical content
            //  2. Server has a strictly newer mtgaVersion → don't regress
            //  3. Server has the same version (regardless of hash) → don't churn
            int versionCmp = CompareVersions(localParts, serverParts);

            if (string.Equals(hash, serverHash, StringComparison.OrdinalIgnoreCase) && versionCmp <= 0)
            {
                Plugin.Log.LogInfo($"CardDbUploader: server already at hash={Snippet(hash)} ver={serverMtgaVersion ?? "?"}, skipping");
                yield break;
            }

            if (serverParts != null && versionCmp < 0)
            {
                Plugin.Log.LogInfo($"CardDbUploader: local MTGA {mtgaVersion} is OLDER than server {serverMtgaVersion}, skipping (will not regress server)");
                yield break;
            }

            if (serverParts != null && versionCmp == 0 && !string.Equals(hash, serverHash, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogInfo($"CardDbUploader: same MTGA version {mtgaVersion} but different hash (server={Snippet(serverHash)} local={Snippet(hash)}). Skipping — content tied to version.");
                yield break;
            }

            Plugin.Log.LogInfo($"CardDbUploader: local MTGA {mtgaVersion} > server {serverMtgaVersion ?? "<none>"}, uploading {Path.GetFileName(dbPath)}");

            // Step 1: Ask the function for a signed upload URL
            string uploadUrl = null;
            yield return RequestUploadUrl(hash, mtgaVersion, url => uploadUrl = url);
            if (string.IsNullOrEmpty(uploadUrl))
            {
                Plugin.Log.LogWarning("CardDbUploader: failed to obtain signed upload URL — aborting");
                yield break;
            }

            // Step 2: Gzip the file in memory and PUT it to the signed URL
            byte[] gzippedBytes;
            long rawSize;
            try
            {
                rawSize = new FileInfo(dbPath).Length;
                gzippedBytes = GzipFile(dbPath);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CardDbUploader: failed to read/gzip card DB: {ex.Message}");
                yield break;
            }

            Plugin.Log.LogInfo($"CardDbUploader: gzipped {rawSize / (1024 * 1024)}MB → {gzippedBytes.Length / (1024 * 1024)}MB, PUT-ing to GCS");

            yield return PutToSignedUrl(uploadUrl, gzippedBytes);
        }

        // ---- Version reading & comparison ----

        /// <summary>
        /// Returns the installed MTGA build version, e.g. "2026.62.30.14056".
        /// Returns null if no source yields a parseable version.
        ///
        /// Primary source is <c>Application.version</c> (Unity's bundleVersion),
        /// which is what Arena itself prints at startup:
        ///   <c>Version: 2026.62.30.14056 / 2026.62.30.14056.1313448 /</c>
        ///
        /// It used to be read from the JSON file <c>&lt;install&gt;/version</c>, but the
        /// Unity 6.3 build (2026-08-25) stopped shipping that file: the read failed
        /// and the upload was skipped at every launch. The file is still tried as a
        /// fallback, since other launchers and older installs may still have it.
        ///
        /// Note the numbering scheme changed with that same build, from
        /// "0.1.11950.1257485" to "2026.62.30.14056". Comparison stays correct
        /// because the first component grew (2026 > 0), so the new scheme sorts
        /// above every version the server may still hold in the old one.
        /// </summary>
        private static string ReadInstalledMtgaVersion(string dbPath)
        {
            try
            {
                var appVersion = Application.version;
                if (ParseVersion(appVersion) != null)
                    return appVersion;

                Plugin.Log.LogInfo(
                    $"CardDbUploader: Application.version non utilizzabile ('{appVersion}'), provo <install>/version");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogInfo($"CardDbUploader: Application.version non leggibile ({ex.Message})");
            }

            return ReadVersionFile(dbPath);
        }

        /// <summary>
        /// Fallback: reads the JSON at <c>&lt;install&gt;/version</c> and returns the
        /// highest key under "Versions". Returns null on any error.
        /// </summary>
        private static string ReadVersionFile(string dbPath)
        {
            // Due modi per trovare <install>:
            //   - Application.dataPath = <install>/MTGA_Data, il piu' affidabile
            //     visto che giriamo dentro MTGA.
            //   - risalendo da dbPath: <install>/MTGA_Data/Downloads/Raw/Raw_*.mtga
            try
            {
                string installRoot = null;
                try
                {
                    var dataPath = Application.dataPath;
                    if (!string.IsNullOrEmpty(dataPath))
                        installRoot = Path.GetDirectoryName(dataPath);
                }
                catch { /* fall back below */ }

                if (string.IsNullOrEmpty(installRoot) || !Directory.Exists(installRoot))
                {
                    var dir = Path.GetDirectoryName(dbPath);
                    for (int i = 0; i < 3 && !string.IsNullOrEmpty(dir); i++)
                        dir = Path.GetDirectoryName(dir);
                    installRoot = dir;
                }

                if (string.IsNullOrEmpty(installRoot) || !Directory.Exists(installRoot))
                    return null;

                var versionFile = Path.Combine(installRoot, "version");
                if (!File.Exists(versionFile))
                {
                    Plugin.Log.LogInfo($"CardDbUploader: nessun file version in {installRoot} (atteso su build Unity 6+)");
                    return null;
                }

                var json = JObject.Parse(File.ReadAllText(versionFile));
                var versions = json["Versions"] as JObject;
                if (versions == null || !versions.Properties().Any())
                    return null;

                // Piu' di una chiave: teniamo la piu' alta.
                string best = null;
                long[] bestParts = null;
                foreach (var prop in versions.Properties())
                {
                    var parts = ParseVersion(prop.Name);
                    if (parts == null) continue;
                    if (bestParts == null || CompareVersions(parts, bestParts) > 0)
                    {
                        best = prop.Name;
                        bestParts = parts;
                    }
                }
                return best;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CardDbUploader: failed to read MTGA version: {ex.Message}");
                return null;
            }
        }

        /// <summary>Parses "0.1.11950.1257485" into long[]; null on bad input.</summary>
        internal static long[] ParseVersion(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split('.');
            var result = new long[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!long.TryParse(parts[i], out var p)) return null;
                if (p < 0) return null;
                result[i] = p;
            }
            return result;
        }

        /// <summary>
        /// Lexicographic compare. Treats null as "no version" — null sorts
        /// less than any concrete version so "first upload" beats nothing.
        /// </summary>
        internal static int CompareVersions(long[] a, long[] b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int len = Math.Max(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                long av = i < a.Length ? a[i] : 0;
                long bv = i < b.Length ? b[i] : 0;
                if (av != bv) return av < bv ? -1 : 1;
            }
            return 0;
        }

        // ---- Networking ----

        /// <summary>
        /// Reads the .mtga SQLite file and returns gzip-compressed bytes.
        /// </summary>
        private static byte[] GzipFile(string path)
        {
            using (var input = File.OpenRead(path))
            using (var output = new MemoryStream())
            {
                using (var gz = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
                {
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        gz.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        /// <summary>
        /// Calls the requestCardDbUpload Cloud Function with the local hash +
        /// MTGA version. Returns the signed PUT URL via callback (null on
        /// skip/failure). Auth is the user's Firebase ID token.
        /// </summary>
        private static IEnumerator RequestUploadUrl(string hash, string mtgaVersion, Action<string> onResult)
        {
            var config = FirebaseConfig.Instance;
            // mtgaVersion may contain dots; safe in URL but URL-encode anyway.
            var encodedVersion = UnityWebRequest.EscapeURL(mtgaVersion);
            var url = config.ScopeFunctionUrl(
                $"{config.FunctionUrl}/requestCardDbUpload?hash={hash}&mtgaVersion={encodedVersion}");

            using (var request = new UnityWebRequest(url, "POST"))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 30;
                var idToken = FirebaseClient.Instance.IdToken;
                if (!string.IsNullOrEmpty(idToken))
                    request.SetRequestHeader("Authorization", "Bearer " + idToken);
                request.SetRequestHeader("Content-Type", "application/json");

                yield return request.SendWebRequest();

                if (request.responseCode != 200)
                {
                    Plugin.Log.LogWarning($"CardDbUploader: requestCardDbUpload failed (HTTP {request.responseCode}): {request.error} {request.downloadHandler?.text}");
                    onResult?.Invoke(null);
                    yield break;
                }

                JObject body = null;
                try { body = JObject.Parse(request.downloadHandler.text); }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"CardDbUploader: bad JSON from requestCardDbUpload: {ex.Message}");
                    onResult?.Invoke(null);
                    yield break;
                }

                if (body["skipped"]?.Value<bool>() == true)
                {
                    var reason = body["reason"]?.ToString() ?? "skipped";
                    Plugin.Log.LogInfo($"CardDbUploader: server reports skip ({reason}) — not uploading");
                    onResult?.Invoke(null);
                    yield break;
                }

                var signed = body["uploadUrl"]?.ToString();
                if (string.IsNullOrEmpty(signed))
                {
                    Plugin.Log.LogWarning($"CardDbUploader: response has no uploadUrl: {request.downloadHandler.text}");
                    onResult?.Invoke(null);
                    yield break;
                }
                onResult?.Invoke(signed);
            }
        }

        /// <summary>
        /// PUTs the gzipped bytes directly to a signed Cloud Storage URL.
        /// No auth header — the URL itself authenticates. Content-Type must
        /// match what the URL was signed with (application/gzip).
        /// </summary>
        private static IEnumerator PutToSignedUrl(string signedUrl, byte[] gzippedBytes)
        {
            using (var request = UnityWebRequest.Put(signedUrl, gzippedBytes))
            {
                request.timeout = 600; // up to 10 minutes for slow connections
                request.SetRequestHeader("Content-Type", "application/gzip");
                // Don't add an Authorization header — the URL signature IS the auth.

                yield return request.SendWebRequest();

                if (request.responseCode == 200 || request.responseCode == 201)
                {
                    Plugin.Log.LogInfo("CardDbUploader: upload succeeded — Storage trigger will parse and update /cardMetadata shortly");
                }
                else
                {
                    var truncated = request.downloadHandler?.text ?? "";
                    if (truncated.Length > 500) truncated = truncated.Substring(0, 500) + "…";
                    Plugin.Log.LogWarning($"CardDbUploader: PUT failed (HTTP {request.responseCode}): {request.error} {truncated}");
                }
            }
        }

        private static string FindLatestCardDb()
        {
            string newestPath = null;
            DateTime newestWriteTime = DateTime.MinValue;

            foreach (var root in CardDbSearchRoots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                string[] files;
                try { files = Directory.GetFiles(root, "Raw_CardDatabase_*.mtga"); }
                catch { continue; }

                foreach (var path in files)
                {
                    try
                    {
                        var lastWrite = File.GetLastWriteTimeUtc(path);
                        if (lastWrite > newestWriteTime)
                        {
                            newestWriteTime = lastWrite;
                            newestPath = path;
                        }
                    }
                    catch { }
                }
            }

            return newestPath;
        }

        private static string ExtractHashFromFilename(string filePath)
        {
            // Filename pattern: Raw_CardDatabase_<hex chars>.mtga
            var name = Path.GetFileNameWithoutExtension(filePath);
            const string prefix = "Raw_CardDatabase_";
            if (!name.StartsWith(prefix)) return null;
            var hash = name.Substring(prefix.Length);
            if (hash.Length < 8 || !IsHex(hash)) return null;
            return hash.ToLowerInvariant();
        }

        private static bool IsHex(string s)
        {
            return s.All(c =>
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F'));
        }

        private static string Snippet(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            return s.Length <= 8 ? s : s.Substring(0, 8) + "…";
        }
    }
}
