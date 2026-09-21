using System;
using System.Collections.Generic;
using System.IO;
using MTGAEnhancementSuite.Features;
using MTGAEnhancementSuite.State;
using TMPro;
using UnityEngine;
using static MTGAEnhancementSuite.UI.ListModal;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Tutti i replay registrati, di qualunque mazzo, dal piu' recente.
    ///
    /// **Esiste perche' l'unica strada per un replay passava da un mazzo.**
    /// Bisognava aprire la lista mazzi, sapere quale mazzo aveva la partita
    /// giusta, entrare nelle statistiche e poi nello storico. Un replay pero'
    /// non e' una proprieta' di un mazzo: e' una partita, e la si cerca per
    /// quando e' stata giocata. Da qui si arriva col pannello MTGA+, in due
    /// clic e senza sapere niente in anticipo.
    ///
    /// La finestra del mazzo resta: li' i replay stanno accanto ai numeri di
    /// quel mazzo, che e' un'altra domanda e un'altra risposta.
    ///
    /// <para><b>Da qui si cancella (2026-09-21).</b> Ogni riga ha una casella e
    /// un pulsante "Keep"; in cima ci sono i comandi per togliere quelli scelti
    /// o tutti. La cancellazione vera sta in <see cref="ReplayFiles"/>, che non
    /// sa niente di Unity e ha le sue guardie: qui si decide COSA, li' si
    /// controlla che sia lecito.</para>
    ///
    /// <para><b>Le statistiche non si toccano mai.</b> Cancellare un replay
    /// toglie due file dal disco e nient'altro: <c>matches.jsonl</c> resta
    /// com'e', quindi percentuali, storico e conteggi dei mazzi sopravvivono.
    /// L'elenco qui sotto mostra le partite il cui file **esiste ancora sul
    /// disco**, ed e' per questo che una registrazione cancellata sparisce da
    /// sola: nessuno deve tenere aggiornato un secondo registro, e i due non
    /// possono finire in disaccordo.</para>
    /// </summary>
    internal static class ReplayBrowserWindow
    {
        private static ModalFrame _frame;

        /// <summary>Le righe spuntate, per nome di file.</summary>
        private static readonly HashSet<string> _selected =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>I "da tenere", letti dal disco all'apertura.</summary>
        private static HashSet<string> _kept =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Apre la finestra: la selezione riparte vuota.</summary>
        public static void Show()
        {
            _selected.Clear();

            // Dal disco: la sessione potrebbe aver registrato partite dopo
            // l'ultima lettura, ed e' proprio il caso di chi apre questa
            // finestra subito dopo aver giocato.
            MatchStatsStore.Invalidate();
            _kept = ReplayFiles.LoadKept(ReplayRecorder.Directory());

            Redraw();
        }

        public static void Close()
        {
            _frame?.Destroy();
            _frame = null;
        }

        // -----------------------------------------------------------------

        /// <summary>
        /// Ridisegna tenendo selezione e "da tenere". Non rilegge le
        /// statistiche: non le abbiamo cambiate, e rileggerle a ogni spunta
        /// sarebbe un giro sul disco per niente.
        /// </summary>
        private static void Redraw()
        {
            var folder = ReplayRecorder.Directory();

            var all = MatchStatsStore.All();
            var withReplay = new List<MatchRecord>();
            foreach (var m in all)
            {
                if (m == null || string.IsNullOrEmpty(m.ReplayFile)) continue;
                // Il file dev'esserci DAVVERO. E' questo controllo che fa
                // sparire dall'elenco cio' che e' stato cancellato, senza
                // toccare il registro delle partite.
                if (folder == null || !File.Exists(Path.Combine(folder, m.ReplayFile))) continue;
                withReplay.Add(m);
            }

            withReplay.Sort((a, b) => b.EndedUtc.CompareTo(a.EndedUtc));

            // Una spunta su un replay sparito non deve restare in giro.
            _selected.RemoveWhere(name => !withReplay.Exists(
                m => string.Equals(m.ReplayFile, name, StringComparison.OrdinalIgnoreCase)));

            Build(withReplay, all.Count, folder);
        }

        private static void Build(List<MatchRecord> replays, int matchesTotal, string folder)
        {
            Close();
            _frame = ListModal.Open("MTGAES_ReplayBrowser", "Replays", richTitle: false, onClose: Close,
                anchorMin: new Vector2(0.24f, 0.12f), anchorMax: new Vector2(0.76f, 0.88f));

            var list = _frame.List;
            var footer = _frame.Footer;

            if (replays.Count == 0)
            {
                Notice(list, EmptyMessage(matchesTotal));
            }
            else
            {
                Toolbar(list, replays, folder);
                foreach (var m in replays) AddRow(list, m, folder);
                Spacer(list);
                Note(list, Summary(replays, folder));
            }

            RecordingButton(footer);

            Button(footer, "Close", new Vector2(0.78f, 0f), new Vector2(1f, 1f),
                new Color(0.25f, 0.25f, 0.32f, 1f), Close);
        }

        /// <summary>
        /// Cosa dire quando non c'e' niente da mostrare.
        ///
        /// Le due situazioni si assomigliano e vogliono risposte diverse: chi
        /// non ha mai giocato con la mod accesa deve sapere **questo**, non
        /// che l'interruttore e' spento; chi ha partite ma nessun replay deve
        /// sapere che l'interruttore esiste ed e' qui sotto.
        /// </summary>
        private static string EmptyMessage(int matchesTotal)
        {
            if (matchesTotal == 0)
            {
                return "No matches recorded yet.\n" +
                       "MTGA+ records each game as you play it, so games played\n" +
                       "while it was not running are not counted.";
            }

            string none = matchesTotal == 1
                ? "Your one recorded match has no replay."
                : $"None of your {matchesTotal} recorded matches has a replay.";

            return none + "\n" +
                   "Recording is off by default. Turn it on below\n" +
                   "and every match you play after that is saved.";
        }

        /// <summary>
        /// Quanto occupano i replay, e quanta parte di quello e' rigenerabile.
        ///
        /// Le pagine `.html` pesano **piu'** delle registrazioni — ognuna si
        /// porta dentro tutto il visualizzatore — e si riscrivono da sole al
        /// prossimo "Open". Dirlo qui evita che qualcuno cancelli una
        /// registrazione, che e' l'unica cosa irrecuperabile, credendo di
        /// liberare lo spazio che in realta' occupano le pagine.
        /// </summary>
        private static string Summary(List<MatchRecord> replays, string folder)
        {
            long recordings = 0, pages = 0;
            int pageCount = 0;

            foreach (var m in replays)
            {
                foreach (var path in ReplayFiles.Existing(folder, m.ReplayFile))
                {
                    long size;
                    try { size = new FileInfo(path).Length; } catch { continue; }

                    if (path.EndsWith(ReplayFiles.PageExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        pages += size;
                        pageCount++;
                    }
                    else recordings += size;
                }
            }

            string text = Count(replays.Count, "replay", "replays") + ", " + Size(recordings) + " on disk";
            if (pageCount > 0)
                text += "   plus " + Size(pages) + " of viewer pages (" + pageCount +
                        ") - those are rebuilt whenever you press Open";
            return text;
        }

        private static string Size(long bytes)
        {
            if (bytes >= 1024L * 1024L) return (bytes / (1024f * 1024f)).ToString("0.0") + " MB";
            return Math.Max(1, bytes / 1024L) + " KB";
        }

        // -----------------------------------------------------------------
        // I comandi, in cima
        // -----------------------------------------------------------------

        /// <summary>
        /// Selezione e cancellazione stanno **in cima alla lista, non nel pie'
        /// di pagina**: li' sotto ci sono gia' l'interruttore della
        /// registrazione e "Close", e mettere un pulsante che cancella tutto
        /// accanto a quello che chiude la finestra e' un invito a sbagliare clic.
        /// </summary>
        private static void Toolbar(Transform parent, List<MatchRecord> replays, string folder)
        {
            var row = Row(parent);

            Button(row.transform, "Select all", new Vector2(0.01f, 0.12f), new Vector2(0.15f, 0.88f),
                ButtonIdle,
                () =>
                {
                    foreach (var m in replays) _selected.Add(m.ReplayFile);
                    Redraw();
                });

            Button(row.transform, "Clear", new Vector2(0.16f, 0.12f), new Vector2(0.26f, 0.88f),
                ButtonIdle, () => { _selected.Clear(); Redraw(); });

            Button(row.transform,
                _selected.Count == 0 ? "Delete selected" : "Delete selected (" + _selected.Count + ")",
                new Vector2(0.40f, 0.12f), new Vector2(0.66f, 0.88f),
                _selected.Count == 0 ? ButtonIdle : new Color(0.55f, 0.25f, 0.25f, 1f),
                () => AskDeleteSelected(replays, folder));

            Button(row.transform, "Delete all", new Vector2(0.67f, 0.12f), new Vector2(0.84f, 0.88f),
                ButtonIdle, () => AskDeleteAll(replays, folder));

            Button(row.transform, "Folder", new Vector2(0.85f, 0.12f), new Vector2(0.99f, 0.88f),
                ButtonIdle, () => OpenFolder(folder));
        }

        private static void AskDeleteSelected(List<MatchRecord> replays, string folder)
        {
            var chosen = Chosen(replays);
            if (chosen.Count == 0)
            {
                Toast.Info("Tick the replays you want to delete first");
                return;
            }

            int keptAmong = 0;
            foreach (var m in chosen) if (_kept.Contains(m.ReplayFile)) keptAmong++;

            string body = chosen.Count == 1
                ? "Delete this replay?\n\n" + Describe(chosen[0])
                : "Delete these " + chosen.Count + " replays?\n\n" + Describe(chosen[0]) +
                  "\n...and " + (chosen.Count - 1) + " more.";

            // Un "da tenere" si cancella eccome, se lo si e' scelto apposta: la
            // protezione riguarda "Delete all", non la scelta esplicita. Va
            // pero' DETTO, perche' chi l'ha marcato lo ha fatto per non
            // perderlo per sbaglio, ed e' l'unico avviso che lo separa da li'.
            if (keptAmong > 0)
                body += keptAmong == 1
                    ? "\n\nOne of them is marked Keep. It will be deleted too."
                    : "\n\n" + keptAmong + " of them are marked Keep. They will be deleted too.";

            body += "\n\nYour deck statistics are not affected.";

            ConfirmActionModal.Show("Delete replays", body, "Delete", () => DeleteThese(chosen, folder));
        }

        private static void AskDeleteAll(List<MatchRecord> replays, string folder)
        {
            var doomed = new List<MatchRecord>();
            int protectedCount = 0;
            foreach (var m in replays)
            {
                if (_kept.Contains(m.ReplayFile)) protectedCount++;
                else doomed.Add(m);
            }

            if (doomed.Count == 0)
            {
                Toast.Info(protectedCount > 0
                    ? "Every replay is marked Keep - nothing to delete"
                    : "There are no replays to delete");
                return;
            }

            string body = "Delete all " + doomed.Count + " replays?";
            if (protectedCount > 0)
                body += protectedCount == 1
                    ? "\n\nThe one marked Keep will be left alone."
                    : "\n\nThe " + protectedCount + " marked Keep will be left alone.";
            body += "\n\nThis cannot be undone. Your deck statistics are not affected.";

            ConfirmActionModal.Show("Delete all replays", body, "Delete all",
                () => DeleteThese(doomed, folder));
        }

        /// <summary>
        /// L'unico punto che cancella davvero, e anche qui non decide niente:
        /// passa da <see cref="ReplayFiles.Delete"/>, che rifiuta qualunque
        /// nome non sia una registrazione dentro quella cartella.
        /// </summary>
        private static void DeleteThese(List<MatchRecord> records, string folder)
        {
            int done = 0;
            var failures = new List<string>();

            foreach (var m in records)
            {
                string error;
                if (ReplayFiles.Delete(folder, m.ReplayFile, out error))
                {
                    done++;
                    _selected.Remove(m.ReplayFile);
                    _kept.Remove(m.ReplayFile);
                }
                else
                {
                    failures.Add(m.ReplayFile);
                    Plugin.Log.LogWarning($"Replay: non cancellato '{m.ReplayFile}': {error}");
                }
            }

            ReplayFiles.SaveKept(folder, _kept);
            PerPlayerLog.Info($"Replays: cancellati {done} su {records.Count} richiesti");

            if (failures.Count == 0)
                Toast.Success(Count(done, "replay deleted", "replays deleted"));
            else
                Toast.Error(done + " deleted, " + failures.Count + " could not be removed - see log");

            Redraw();
        }

        private static List<MatchRecord> Chosen(List<MatchRecord> replays)
        {
            var chosen = new List<MatchRecord>();
            foreach (var m in replays)
                if (_selected.Contains(m.ReplayFile)) chosen.Add(m);
            return chosen;
        }

        /// <summary>
        /// Una riga che descrive un replay nella conferma. I tag dello stile
        /// del nome vanno tolti: la finestra di conferma non e' in rich text, e
        /// senza toglierli si leggerebbe la marcatura invece del nome.
        /// </summary>
        private static string Describe(MatchRecord m)
        {
            string outcome = m.Won == null ? "draw" : (m.Won.Value ? "win" : "loss");
            string deck = string.IsNullOrEmpty(m.DeckName)
                ? "(unnamed deck)"
                : ReplayFiles.StripTags(m.DeckName);
            return m.EndedLocal.ToString("dd/MM/yyyy HH:mm") + "   " + deck + "   " + outcome;
        }

        // -----------------------------------------------------------------

        private static void AddRow(Transform parent, MatchRecord m, string folder)
        {
            var row = Row(parent);
            var record = m;   // la variabile del ciclo non si cattura

            bool ticked = _selected.Contains(m.ReplayFile);
            bool kept = _kept.Contains(m.ReplayFile);

            // La casella e' un pulsantino con "[ ]" / "[X]", in ASCII di
            // proposito: un quadratino o una spunta dipendono da quali glifi ha
            // il font di Arena, e non e' una scommessa che valga la pena fare
            // per una casella che dev'essere leggibile sempre.
            Button(row.transform, ticked ? "[X]" : "[ ]",
                new Vector2(0.005f, 0.12f), new Vector2(0.045f, 0.88f),
                ticked ? AccentBlue : ButtonIdle,
                () =>
                {
                    if (!_selected.Remove(record.ReplayFile)) _selected.Add(record.ReplayFile);
                    Redraw();
                });

            Label(row.transform, m.EndedLocal.ToString("dd/MM  HH:mm"), TextSecondary, 15,
                TextAlignmentOptions.MidlineLeft, new Vector2(0.05f, 0f), new Vector2(0.21f, 1f),
                new Vector4(8, 0, 4, 0));

            string outcome = m.Won == null ? "DRAW" : (m.Won.Value ? "WIN" : "LOSS");
            Label(row.transform, outcome,
                m.Won == null ? TextSecondary : (m.Won.Value ? WinGreen : LossRed), 15,
                TextAlignmentOptions.MidlineLeft, new Vector2(0.21f, 0f), new Vector2(0.30f, 1f),
                new Vector4(0, 0, 4, 0), FontStyles.Bold);

            // Il nome del mazzo puo' contenere i tag dell'editor dei nomi: qui
            // si disegnano, come ovunque nel gioco.
            var deck = Label(row.transform,
                string.IsNullOrEmpty(m.DeckName) ? "(unnamed deck)" : m.DeckName,
                TextPrimary, 16, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.30f, 0f), new Vector2(0.56f, 1f), new Vector4(0, 0, 6, 0));
            deck.richText = true;

            var versus = Label(row.transform, "vs " + OpponentColorLabel.Of(m), TextPrimary, 16,
                TextAlignmentOptions.MidlineLeft, new Vector2(0.56f, 0f), new Vector2(0.70f, 1f),
                new Vector4(0, 0, 4, 0));
            versus.richText = true;

            Label(row.transform, m.EventName ?? "", TextDisabled, 12,
                TextAlignmentOptions.MidlineRight, new Vector2(0.70f, 0f), new Vector2(0.76f, 1f),
                new Vector4(0, 0, 6, 0));

            Button(row.transform, kept ? "Kept" : "Keep",
                new Vector2(0.765f, 0.15f), new Vector2(0.855f, 0.85f),
                kept ? new Color(0.45f, 0.38f, 0.16f, 1f) : ButtonIdle,
                () => ToggleKept(record, folder));

            Button(row.transform, "Open", new Vector2(0.865f, 0.15f), new Vector2(0.99f, 0.85f),
                AccentBlue, () => ReplayExporter.OpenFor(record));
        }

        /// <summary>
        /// Marca o smarca un replay come "da tenere". La protezione vale solo
        /// contro "Delete all": scegliendolo a mano si cancella lo stesso, e la
        /// conferma lo dice a chiare lettere.
        /// </summary>
        private static void ToggleKept(MatchRecord m, string folder)
        {
            if (!_kept.Remove(m.ReplayFile)) _kept.Add(m.ReplayFile);

            if (!ReplayFiles.SaveKept(folder, _kept))
                Toast.Error("Couldn't save the Keep list");

            Redraw();
        }

        // -----------------------------------------------------------------

        /// <summary>
        /// Apre in Esplora risorse la cartella dove finiscono i replay.
        ///
        /// **Si passa da <see cref="Helpers.ExternalOpen"/>**, mai da
        /// Application.OpenURL: quello che si apre da dentro il gioco nasce
        /// NELL'ALBERO dei processi di MTGA, e finche' resta aperto Steam
        /// considera il gioco in esecuzione e non lo fa ripartire. E' successo
        /// davvero con la pagina dei replay — `MISTAKES.md` #38, regola 52 — e
        /// vale per una cartella quanto per un browser.
        /// </summary>
        private static void OpenFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                Toast.Error("Couldn't locate the replay folder");
                return;
            }

            if (!Helpers.ExternalOpen.Detached(folder))
                Toast.Error("Couldn't open the replay folder");
        }

        /// <summary>
        /// L'interruttore della registrazione, **anche qui**.
        ///
        /// E' lo stesso di "Match history", ripetuto di proposito: chi arriva
        /// in questa finestra e non trova niente ha bisogno dell'interruttore
        /// esattamente in questo momento, e mandarlo a cercarlo dentro le
        /// statistiche di un mazzo qualunque sarebbe la stessa strada tortuosa
        /// che questa finestra esiste per evitare.
        /// </summary>
        private static void RecordingButton(Transform footer)
        {
            bool recording = ModSettings.Instance.RecordReplays;
            Button(footer, recording ? "Recording: ON" : "Recording: off",
                new Vector2(0f, 0f), new Vector2(0.30f, 1f),
                recording ? new Color(0.20f, 0.45f, 0.30f, 1f) : ButtonIdle,
                () =>
                {
                    ModSettings.Instance.RecordReplays = !ModSettings.Instance.RecordReplays;
                    ModSettings.Instance.Save();
                    Toast.Info(ModSettings.Instance.RecordReplays
                        ? "Replay recording on - starts with your next match"
                        : "Replay recording off");
                    Redraw();   // cambia il colore del pulsante e il messaggio
                });
        }
    }
}
