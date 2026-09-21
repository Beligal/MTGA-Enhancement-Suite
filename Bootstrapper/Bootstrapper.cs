using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;

namespace MTGAESBootstrapper
{
    /// <summary>
    /// Tiny BepInEx plugin that runs BEFORE the main Enhancement Suite plugin.
    /// Its only job: check for staged update files and swap them in before the
    /// main DLL gets loaded and locked by the runtime.
    ///
    /// This DLL rarely changes — updates are applied to MTGAEnhancementSuite.dll only.
    ///
    /// The staging protocol, shared with AutoUpdater.cs in the main plugin (the
    /// two assemblies share no code, so both sides hard-code these names):
    ///
    ///   *.download        downloaded, NOT yet verified. Never installed, ever.
    ///   *.pending         downloaded and verified, ready to install.
    ///   staged.ok         the file list with their hashes and how many there
    ///                     are, published by rename so it is all there or not
    ///                     there at all. Its presence makes the set installable.
    ///   transaction.txt   the journal. Written BEFORE the first installed file
    ///                     is touched; it names the files and carries a state.
    ///
    /// The journal is the only thing that may authorise removing a backup, and
    /// only in state "confirmed". "installing" and "installed" both mean
    /// recover; an unreadable journal means touch nothing at all. There is no
    /// rule that reads an ABSENT journal as permission to clean up, because a
    /// backup nothing vouches for is exactly the one worth keeping.
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Bootstrapper : BaseUnityPlugin
    {
        public const string GUID = "com.mtgaenhancement.bootstrapper";
        public const string NAME = "MTGA+ Bootstrapper";
        public const string VERSION = "1.0.0";

        private const string DownloadSuffix = ".download";
        private const string PendingSuffix = ".pending";
        private const string BackupSuffix = ".bak";
        private const string StagedMarkerName = "staged.ok";
        private const string JournalName = "transaction.txt";

        // The three states a transaction can be in. They exist so that removing
        // a backup is always an explicit decision about a named transaction,
        // never an inference from something being absent.
        //
        //   installing  the installed files are being replaced right now
        //   installed   all files replaced, waiting for the new plugin to run
        //   confirmed   the new plugin ran; these backups may now go
        //
        // Only "confirmed" permits deleting a backup. The first two both mean
        // recover, and an unreadable journal means touch nothing.
        private const string StateInstalling = "installing";
        private const string StateInstalled = "installed";
        private const string StateConfirmed = "confirmed";

        private void Awake()
        {
            try
            {
                var pluginDir = Path.GetDirectoryName(Info.Location);
                var updateDir = Path.Combine(pluginDir, "update");
                var journalPath = Path.Combine(updateDir, JournalName);

                // 1. A journal means a transaction that hasn't been closed out.
                //    What to do depends on its state, and nothing else — a
                //    backup is only ever removed because a journal says its
                //    transaction was confirmed.
                if (File.Exists(journalPath))
                {
                    var state = ReadMarkerValue(journalPath, "state");
                    var journalled = ReadJournalFiles(journalPath);

                    if (state == StateConfirmed)
                    {
                        // The plugin confirmed it but couldn't finish tidying up.
                        // Finish the job; never roll a confirmed update back.
                        FinishCleanup(pluginDir, updateDir, journalPath, journalled);
                        return;
                    }

                    if (journalled.Count == 0)
                    {
                        // We know a transaction was open and we cannot tell which
                        // files it covered. Removing anything on that basis would
                        // be guessing — and so would removing the journal, which
                        // would let the NEXT launch guess in turn. This used to
                        // delete the journal here and leave the backups, which
                        // amounted to the same thing one launch later.
                        Logger.LogError(
                            "An update journal is present but names no files. Leaving the installation and every "
                            + "backup untouched, and installing nothing. Check the plugin folder, then delete "
                            + JournalName + " by hand.");
                        return;
                    }

                    RollBackTransaction(pluginDir, updateDir, journalPath, journalled, state);
                    return;
                }

                // 2. No journal: there is deliberately NO sweep of *.bak here.
                //    A backup that belongs to no transaction is not ours to
                //    delete, and the blanket delete that used to live here is
                //    exactly what could throw away copies a failed recovery had
                //    kept on purpose.
                WarnAboutStrayBackups(pluginDir);

                if (!Directory.Exists(updateDir))
                    return;

                // 3. No staging marker means the set is incomplete. This is the
                //    case that used to install a truncated DLL: the download
                //    went straight to the name we treat as installable, and the
                //    clean-up meant to cover it lived in a process that had
                //    already been killed.
                var stagedMarker = Path.Combine(updateDir, StagedMarkerName);
                if (!File.Exists(stagedMarker))
                {
                    if (Directory.GetFiles(updateDir, "*" + PendingSuffix).Length > 0 ||
                        Directory.GetFiles(updateDir, "*" + DownloadSuffix).Length > 0)
                        Logger.LogWarning("Incomplete update found (no staging marker) — discarding it");
                    DiscardStaged(updateDir);
                    return;
                }

                // 4. The marker has to account for itself: the declared count
                //    must match what we actually parsed. A marker cut short
                //    would otherwise read as a smaller but perfectly valid
                //    update, and we would install part of a set.
                var expected = ReadMarker(stagedMarker);
                var declared = ReadMarkerValue(stagedMarker, "files");
                int declaredCount;
                if (expected.Count == 0 ||
                    !int.TryParse(declared, out declaredCount) ||
                    declaredCount != expected.Count)
                {
                    Logger.LogError(
                        $"Staging marker is incomplete (declares '{declared}', parsed {expected.Count}) — discarding the update");
                    DiscardStaged(updateDir);
                    return;
                }

                // 5. Re-check every file against the hashes recorded when the
                //    update was staged. The signature was verified back then, by
                //    the plugin, which had the key and the JSON parser; this
                //    pass is about integrity — a file truncated or altered after
                //    staging.
                foreach (var entry in expected)
                {
                    var pending = Path.Combine(updateDir, entry.Key + PendingSuffix);
                    if (!File.Exists(pending))
                    {
                        Logger.LogError($"Staged update is missing {entry.Key} — discarding the update");
                        DiscardStaged(updateDir);
                        return;
                    }
                    if (!HashMatches(pending, entry.Value))
                    {
                        Logger.LogError($"Staged {entry.Key} does not match its recorded hash — discarding the update");
                        DiscardStaged(updateDir);
                        return;
                    }
                }

                var stagedVersion = ReadMarkerValue(stagedMarker, "version");
                var names = new List<string>(expected.Keys);

                // 6. The journal goes down FIRST, before a single installed file
                //    is touched. Writing it afterwards left a window in which
                //    the installation had already changed and nothing on disk
                //    said the backups mattered: killed in there, the next launch
                //    read the missing journal as "all settled" and deleted them.
                WriteJournal(journalPath, stagedVersion, StateInstalling, names);
                Logger.LogInfo($"Applying staged update v{stagedVersion} ({names.Count} file(s))");

                // 7. Apply. Anything that throws restores the whole transaction.
                try
                {
                    foreach (var name in names)
                        ApplyOne(pluginDir, updateDir, name);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to apply the update: {ex.Message} — rolling back");

                    // Every file in the transaction, INCLUDING the one that just
                    // failed. ApplyOne can die between moving the old file aside
                    // and moving the new one in, and that file is exactly the one
                    // left with nothing in place — restoring only the ones that
                    // fully succeeded would skip it.
                    FinishRollback(pluginDir, updateDir, journalPath, names);
                    return;
                }

                // 8. Applied but not yet proven. Move the journal on to
                //    "installed": recovery treats it the same as "installing",
                //    but the log then says which of the two a later rollback is
                //    recovering from. If this write fails the journal stays at
                //    "installing", which recovers just the same.
                WriteJournal(journalPath, stagedVersion, StateInstalled, names);

                DiscardStaged(updateDir);
                Logger.LogInfo(
                    $"Update v{stagedVersion} applied — backups kept until the new plugin confirms it runs");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Bootstrapper error: {ex}");
            }
        }

        // ------------------------------------------------------------ applying

        private void ApplyOne(string pluginDir, string updateDir, string fileName)
        {
            var targetPath = Path.Combine(pluginDir, fileName);
            var pendingPath = Path.Combine(updateDir, fileName + PendingSuffix);

            if (File.Exists(targetPath))
            {
                var backupPath = targetPath + BackupSuffix;
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Move(targetPath, backupPath);
            }

            File.Move(pendingPath, targetPath);
            Logger.LogInfo($"Updated {fileName}");
        }

        /// <summary>
        /// Puts every file named in the transaction back the way it was.
        /// Returns false if any of them could not be restored — in which case
        /// the journal and the backups must be kept for another try.
        /// </summary>
        private bool RestoreAll(string pluginDir, List<string> names)
        {
            var allOk = true;
            foreach (var name in names)
            {
                var targetPath = Path.Combine(pluginDir, name);
                var backupPath = targetPath + BackupSuffix;
                try
                {
                    if (!File.Exists(backupPath))
                    {
                        // No backup: either we never got to this file, or there
                        // was nothing installed here to begin with. Both are
                        // fine — unless there's also nothing where the file
                        // should be, which we cannot repair.
                        if (!File.Exists(targetPath))
                        {
                            Logger.LogError($"{name}: no backup and no installed file — cannot restore it");
                            allOk = false;
                        }
                        continue;
                    }

                    if (File.Exists(targetPath)) File.Delete(targetPath);
                    File.Move(backupPath, targetPath);
                    Logger.LogInfo($"Restored {name} from backup");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to restore {name}: {ex.Message}");
                    allOk = false;
                }
            }
            return allOk;
        }

        /// <summary>
        /// Restores the transaction and clears the journal ONLY if every file
        /// came back. A partial rollback that dropped the journal would tell the
        /// next launch that everything was settled, and step 2 would then delete
        /// the very backups still needed.
        /// </summary>
        private void FinishRollback(string pluginDir, string updateDir, string journalPath, List<string> names)
        {
            var restored = RestoreAll(pluginDir, names);
            DiscardStaged(updateDir);

            if (restored)
            {
                try { File.Delete(journalPath); } catch { }
                Logger.LogInfo("Previous version restored.");
            }
            else
            {
                Logger.LogError(
                    "Rollback incomplete — keeping the journal and the remaining backups so the next launch can retry. "
                    + "If this repeats, the installation needs repairing by hand.");
            }
        }

        /// <summary>
        /// A journal in state "installing" or "installed" was found at startup:
        /// the previous launch either died part-way through replacing the files,
        /// or replaced them all and the new plugin never confirmed it ran. The
        /// answer to both is the same — restore.
        /// </summary>
        private void RollBackTransaction(string pluginDir, string updateDir, string journalPath,
                                         List<string> names, string state)
        {
            Logger.LogWarning(
                $"An update from a previous launch was never confirmed (state: {state}) — it either didn't "
                + "finish installing, or the new plugin didn't run. Restoring the previous version.");

            FinishRollback(pluginDir, updateDir, journalPath, names);
        }

        /// <summary>
        /// The journal says the plugin confirmed this update, so its backups may
        /// go. Reached when the plugin marked the transaction confirmed and then
        /// couldn't finish removing them — marking comes first precisely so that
        /// an interruption there can never be mistaken for "needs recovery" and
        /// undo a working update.
        /// </summary>
        private void FinishCleanup(string pluginDir, string updateDir, string journalPath, List<string> names)
        {
            var allGone = true;
            foreach (var name in names)
            {
                var backupPath = Path.Combine(pluginDir, name + BackupSuffix);
                try
                {
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Could not remove {name}{BackupSuffix}: {ex.Message}");
                    allGone = false;
                }
            }

            DiscardStaged(updateDir);

            if (allGone)
            {
                try { File.Delete(journalPath); } catch { }
                Logger.LogInfo("Finished tidying up a confirmed update.");
            }
            else
            {
                Logger.LogWarning(
                    "Some backups of a confirmed update could not be removed — keeping the journal so the "
                    + "next launch can finish. Nothing is at risk: the update itself is in place.");
            }
        }

        /// <summary>
        /// DLL backups with no journal behind them. They are NOT deleted: we
        /// can't say which transaction they belong to, and guessing is how a
        /// recovery copy gets thrown away. Mentioned once so they don't sit
        /// there unseen.
        ///
        /// Note the "*.dll" in the pattern. The plugin folder holds other
        /// people's backups too — settings.json.startup.bak is the plugin's own
        /// — and the update mechanism only ever backs up DLLs, so anything else
        /// is none of our business. A blanket "*.bak" here once meant this ran
        /// on every launch over a settings backup nobody had asked us about.
        /// </summary>
        private void WarnAboutStrayBackups(string pluginDir)
        {
            try
            {
                var strays = Directory.GetFiles(pluginDir, "*.dll" + BackupSuffix);
                if (strays.Length == 0) return;
                Logger.LogInfo(
                    $"{strays.Length} DLL backup(s) present with no update journal: "
                    + string.Join(", ", Array.ConvertAll(strays, Path.GetFileName))
                    + ". Leaving them alone.");
            }
            catch { }
        }

        // ------------------------------------------------------------- journal

        private void WriteJournal(string journalPath, string version, string state, List<string> names)
        {
            var sb = new StringBuilder();
            sb.AppendLine("version=" + version);
            sb.AppendLine("state=" + state);
            sb.AppendLine("started=" + DateTime.Now.ToString("s"));
            foreach (var name in names)
                sb.AppendLine("file=" + name);

            // Published by rename, like everything else that has to be all there
            // or not there at all: a half-written journal naming one file out of
            // two would leave the other unrecoverable.
            var dir = Path.GetDirectoryName(journalPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var tmp = journalPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(journalPath)) File.Delete(journalPath);
            File.Move(tmp, journalPath);
        }

        private List<string> ReadJournalFiles(string journalPath)
        {
            var names = new List<string>();
            try
            {
                foreach (var raw in File.ReadAllLines(journalPath))
                {
                    var line = raw.Trim();
                    var eq = line.IndexOf('=');
                    if (eq <= 0 || eq == line.Length - 1) continue;
                    if (line.Substring(0, eq).Trim() != "file") continue;
                    names.Add(line.Substring(eq + 1).Trim());
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Could not read the journal: {ex.Message}");
                names.Clear();
            }
            return names;
        }

        // ------------------------------------------------------------- helpers

        /// <summary>
        /// Reads the staging marker: one <c>name=value</c> per line, where the
        /// value is a SHA-256 in hex. The <c>version=</c>, <c>files=</c>,
        /// <c>state=</c> and <c>started=</c> lines are metadata, not files. Deliberately not JSON — this assembly
        /// references no parser, and the fewer moving parts guard the update,
        /// the better.
        /// </summary>
        private Dictionary<string, string> ReadMarker(string markerPath)
        {
            var result = new Dictionary<string, string>();
            try
            {
                foreach (var raw in File.ReadAllLines(markerPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0 || eq == line.Length - 1) continue;
                    var key = line.Substring(0, eq).Trim();
                    if (key == "version" || key == "files" ||
                        key == "state" || key == "started") continue;
                    result[key] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Could not read the staging marker: {ex.Message}");
                result.Clear();
            }
            return result;
        }

        /// <summary>Reads one <c>key=value</c> line from the marker.</summary>
        private string ReadMarkerValue(string markerPath, string key)
        {
            try
            {
                foreach (var raw in File.ReadAllLines(markerPath))
                {
                    var line = raw.Trim();
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim() == key)
                        return line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return "?";
        }

        private bool HashMatches(string filePath, string expectedHex)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    var hex = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    return hex == (expectedHex ?? "").Trim().ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Could not hash {Path.GetFileName(filePath)}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes the staging artifacts — downloads, verified-but-not-installed
        /// files, stray temporaries, the staging marker and the manifest. Never
        /// touches the journal or the backups.
        /// </summary>
        private void DiscardStaged(string updateDir)
        {
            try
            {
                if (!Directory.Exists(updateDir)) return;

                foreach (var pattern in new[] { "*" + DownloadSuffix, "*" + PendingSuffix, "*.tmp" })
                    foreach (var f in Directory.GetFiles(updateDir, pattern))
                        try { File.Delete(f); } catch { }

                foreach (var name in new[] { StagedMarkerName, "manifest.json" })
                {
                    var p = Path.Combine(updateDir, name);
                    try { if (File.Exists(p)) File.Delete(p); } catch { }
                }

                if (Directory.GetFiles(updateDir).Length == 0)
                    Directory.Delete(updateDir);
            }
            catch { }
        }
    }
}
