using System;
using System.Collections.Generic;
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
    /// </summary>
    internal static class ReplayBrowserWindow
    {
        private static ModalFrame _frame;

        public static void Show()
        {
            // Dal disco: la sessione potrebbe aver registrato partite dopo
            // l'ultima lettura, ed e' proprio il caso di chi apre questa
            // finestra subito dopo aver giocato.
            MatchStatsStore.Invalidate();

            var all = MatchStatsStore.All();
            var withReplay = new List<MatchRecord>();
            foreach (var m in all)
                if (m != null && !string.IsNullOrEmpty(m.ReplayFile))
                    withReplay.Add(m);

            withReplay.Sort((a, b) => b.EndedUtc.CompareTo(a.EndedUtc));

            Build(withReplay, all.Count);
        }

        public static void Close()
        {
            _frame?.Destroy();
            _frame = null;
        }

        // -----------------------------------------------------------------

        private static void Build(List<MatchRecord> replays, int matchesTotal)
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
                foreach (var m in replays) AddRow(list, m);
                Spacer(list);
                Note(list, Count(replays.Count, "replay", "replays")
                           + ", saved on disk next to your match history");
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

        private static void AddRow(Transform parent, MatchRecord m)
        {
            var row = Row(parent);

            Label(row.transform, m.EndedLocal.ToString("dd/MM  HH:mm"), TextSecondary, 15,
                TextAlignmentOptions.MidlineLeft, new Vector2(0f, 0f), new Vector2(0.19f, 1f),
                new Vector4(12, 0, 4, 0));

            string outcome = m.Won == null ? "DRAW" : (m.Won.Value ? "WIN" : "LOSS");
            Label(row.transform, outcome,
                m.Won == null ? TextSecondary : (m.Won.Value ? WinGreen : LossRed), 15,
                TextAlignmentOptions.MidlineLeft, new Vector2(0.19f, 0f), new Vector2(0.29f, 1f),
                new Vector4(0, 0, 4, 0), FontStyles.Bold);

            // Il nome del mazzo puo' contenere i tag dell'editor dei nomi: qui
            // si disegnano, come ovunque nel gioco.
            var deck = Label(row.transform,
                string.IsNullOrEmpty(m.DeckName) ? "(unnamed deck)" : m.DeckName,
                TextPrimary, 16, TextAlignmentOptions.MidlineLeft,
                new Vector2(0.29f, 0f), new Vector2(0.60f, 1f), new Vector4(0, 0, 6, 0));
            deck.richText = true;

            var versus = Label(row.transform, "vs " + OpponentColorLabel.Of(m), TextPrimary, 16,
                TextAlignmentOptions.MidlineLeft, new Vector2(0.60f, 0f), new Vector2(0.74f, 1f),
                new Vector4(0, 0, 4, 0));
            versus.richText = true;

            Label(row.transform, m.EventName ?? "", TextDisabled, 12,
                TextAlignmentOptions.MidlineRight, new Vector2(0.74f, 0f), new Vector2(0.85f, 1f),
                new Vector4(0, 0, 6, 0));

            var record = m;   // la variabile del ciclo non si cattura
            Button(row.transform, "Open", new Vector2(0.86f, 0.15f), new Vector2(0.99f, 0.85f),
                AccentBlue, () => ReplayExporter.OpenFor(record));
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
                    Show();   // ridisegna: cambia il colore del pulsante e il messaggio
                });
        }
    }
}
