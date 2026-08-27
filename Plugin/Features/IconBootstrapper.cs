using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using UnityEngine;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Ensures the PNG icon set used by the deck-folder UI is present on disk
    /// at <c>&lt;plugin-dir&gt;/icons/</c>. The installer drops these files
    /// when the user runs the installer EXE, but the in-plugin
    /// <see cref="AutoUpdater"/> only refreshes the signed-manifest assets
    /// (DLLs + config). Users who update via the auto-updater would otherwise
    /// miss icons added in later versions.
    ///
    /// Strategy: on startup, check for each expected PNG. If any is missing,
    /// download <c>icons.zip</c> from the latest GitHub release and extract
    /// it into the icons directory. Fire-and-forget — if the fetch fails,
    /// <see cref="UI.IconLoader"/> already falls back to vector glyphs, so
    /// the UI degrades gracefully.
    ///
    /// We do NOT use the signed manifest pipeline here: icons are cosmetic
    /// PNG assets, not code, and adding them to the manifest would mean
    /// every release has to ship them even when nothing changed. The
    /// trade-off is no signature verification on the icon zip — acceptable
    /// because the worst-case outcome of a malicious zip is bad PNGs, which
    /// Unity will refuse to decode.
    /// </summary>
    internal static class IconBootstrapper
    {
        private const string IconsZipUrl =
            "https://github.com/Beligal/MTGA-Enhancement-Suite/releases/latest/download/icons.zip";

        // The full set of icon files the plugin ships. If any of these is
        // missing locally we re-download the zip. Keep in sync with assets/icons/.
        private static readonly string[] ExpectedIcons =
        {
            "check.png",
            "cancel.png",
            "trash.png",
            "folder.png",
        };

        public static IEnumerator EnsureIcons()
        {
            // Yield once so this never blocks the same frame as Plugin.Awake.
            yield return null;

            string iconsDir;
            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                iconsDir = Path.Combine(pluginDir ?? ".", "icons");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"IconBootstrapper: could not resolve plugin dir: {ex.Message}");
                yield break;
            }

            // Fast path: everything's already on disk.
            if (AllIconsPresent(iconsDir))
            {
                Plugin.Log.LogInfo("IconBootstrapper: icons present, nothing to do");
                yield break;
            }

            Plugin.Log.LogInfo($"IconBootstrapper: missing icons under {iconsDir} — downloading {IconsZipUrl}");

            // Download + extract on a background thread so we don't hitch the
            // main thread on the HTTP request or the zip read.
            bool success = false;
            string failReason = null;

            var thread = new System.Threading.Thread(() =>
            {
                string tempZip = null;
                try
                {
                    if (!Directory.Exists(iconsDir))
                        Directory.CreateDirectory(iconsDir);

                    tempZip = Path.Combine(iconsDir, "icons.zip.tmp");
                    DownloadFile(IconsZipUrl, tempZip);

                    // Extract: write each entry, overwriting any stale copy.
                    // We don't use ExtractToDirectory because that throws when
                    // a file already exists (e.g. partial extract from a
                    // previous run). Manual iteration is overwrite-safe.
                    using (var archive = ZipFile.OpenRead(tempZip))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue; // skip dir entries
                            // Defense: only allow basenames — never write
                            // outside iconsDir even if the zip contains
                            // path-traversal entries.
                            var safeName = Path.GetFileName(entry.FullName);
                            if (string.IsNullOrEmpty(safeName)) continue;
                            var destPath = Path.Combine(iconsDir, safeName);
                            entry.ExtractToFile(destPath, overwrite: true);
                        }
                    }

                    success = true;
                }
                catch (Exception ex)
                {
                    failReason = ex.Message;
                }
                finally
                {
                    try { if (tempZip != null && File.Exists(tempZip)) File.Delete(tempZip); }
                    catch { /* best-effort cleanup */ }
                }
            });
            thread.IsBackground = true;
            thread.Start();

            while (thread.IsAlive)
                yield return new WaitForSeconds(0.5f);

            if (success)
                Plugin.Log.LogInfo("IconBootstrapper: icons downloaded and extracted");
            else
                Plugin.Log.LogWarning($"IconBootstrapper: failed to fetch icons ({failReason ?? "unknown"}) — vector fallbacks will be used");
        }

        private static bool AllIconsPresent(string iconsDir)
        {
            if (!Directory.Exists(iconsDir)) return false;
            foreach (var name in ExpectedIcons)
                if (!File.Exists(Path.Combine(iconsDir, name)))
                    return false;
            return true;
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
