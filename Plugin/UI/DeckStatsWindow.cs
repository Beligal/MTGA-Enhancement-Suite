using System;
using System.Collections.Generic;
using MTGAEnhancementSuite.Features;
using MTGAEnhancementSuite.State;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static MTGAEnhancementSuite.UI.ListModal;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// I numeri di un mazzo, e lo storico delle sue partite.
    ///
    /// Due schermate nello stesso riquadro, come la finestra di importazione:
    /// i totali, e da li' "Match history" con una riga per partita.
    ///
    /// Il nome del mazzo si disegna **in rich text**: da quando esiste
    /// l'editor dei nomi puo' contenere colori, e mostrarne i tag qui sarebbe
    /// incoerente col resto del gioco.
    /// </summary>
    internal static class DeckStatsWindow
    {
        // Tavolozza, misure e pezzi arrivano da ListModal: vedi il
        // `using static` in cima. Qui restano solo le cose di questa finestra.
        private static ModalFrame _frame;
        private static Transform _listContent;
        private static Guid _deckId;
        private static string _deckName;
        private static List<MatchRecord> _matches;

        // -----------------------------------------------------------------
        public static void Show(Guid deckId, string deckName)
        {
            _deckId = deckId;
            _deckName = deckName;

            MatchStatsStore.Invalidate();      // dal disco: potrebbero esserci match nuovi
            _matches = MatchStatsStore.ForDeck(deckId);

            ShowSummary();
        }

        public static void Close()
        {
            _frame?.Destroy();
            _frame = null;
            _listContent = null;
            _matches = null;
        }

        // -----------------------------------------------------------------
        // Schermata 1: i totali
        // -----------------------------------------------------------------
        private static void ShowSummary()
        {
            // Se qui i dati mancano NON e' una storia vuota: e' un difetto.
            // La differenza va detta, perche' un riassunto vuoto e' identico a
            // un mazzo mai giocato e cosi' il difetto passa per normalita'.
            if (_matches == null)
            {
                Plugin.Log.LogError(
                    "DeckStatsWindow: nessun dato caricato al disegno. " +
                    "Show() non e' stato chiamato, oppure qualcosa ha azzerato lo stato prima.");
                Toast.Error("Deck statistics unavailable - see the log");
                return;
            }

            var footer = BeginModal();
            var s = MatchStatsStore.Summarize(_matches);

            if (s.Matches == 0)
            {
                Notice(_listContent,
                    "No matches recorded for this deck yet.\n" +
                    "MTGA+ records each game as you play it, so games played while it was not running are not counted.");
            }
            else
            {
                Stat(_listContent, "Matches", s.Matches.ToString());
                Stat(_listContent, "Games", s.Games.ToString());
                Stat(_listContent, "Winrate", Percent(s.WinRate), Tint(s.WinRate));

                Spacer(_listContent);

                Stat(_listContent, "On play", Percent(s.OnPlayRate),
                    Tint(s.OnPlayRate), $"{s.OnPlayWins}/{s.OnPlayMatches}");
                Stat(_listContent, "On draw", Percent(s.OnDrawRate),
                    Tint(s.OnDrawRate), $"{s.OnDrawWins}/{s.OnDrawMatches}");

                if (s.OnPlayMatches + s.OnDrawMatches < s.Matches)
                {
                    int unknown = s.Matches - s.OnPlayMatches - s.OnDrawMatches;
                    Note(_listContent,
                        Count(unknown, "match", "matches") +
                        " did not say who was on the play, and " +
                        (unknown == 1 ? "is" : "are") + " left out of the two rows above");
                }

                if (s.MatchDraws > 0)
                    Note(_listContent,
                        Count(s.MatchDraws, "match", "matches") +
                        " ended without a winner, and " + (s.MatchDraws == 1 ? "is" : "are") +
                        " left out of the winrate");

                Spacer(_listContent);

                foreach (var pair in s.ByEvent)
                    Stat(_listContent, pair.Key, pair.Value.ToString(), TextSecondary);

                Spacer(_listContent);

                if (s.LastPlayedUtc != null)
                    Stat(_listContent, "Last played",
                        s.LastPlayedUtc.Value.ToLocalTime().ToString("dd MMM yyyy"), TextSecondary);
            }

            Button(footer, "Match history", new Vector2(0f, 0f), new Vector2(0.32f, 1f),
                _matches.Count > 0 ? ButtonIdle : new Color(0.18f, 0.18f, 0.22f, 1f),
                () => { if (_matches.Count > 0) ShowHistory(); });

            Button(footer, "Close", new Vector2(0.72f, 0f), new Vector2(1f, 1f),
                new Color(0.25f, 0.25f, 0.32f, 1f), Close);
        }

        // -----------------------------------------------------------------
        // Schermata 2: lo storico
        // -----------------------------------------------------------------
        private static void ShowHistory()
        {
            // Se qui i dati mancano NON e' una storia vuota: e' un difetto.
            // La differenza va detta, perche' un riassunto vuoto e' identico a
            // un mazzo mai giocato e cosi' il difetto passa per normalita'.
            if (_matches == null)
            {
                Plugin.Log.LogError(
                    "DeckStatsWindow: nessun dato caricato al disegno. " +
                    "Show() non e' stato chiamato, oppure qualcosa ha azzerato lo stato prima.");
                Toast.Error("Deck statistics unavailable - see the log");
                return;
            }

            var footer = BeginModal();

            foreach (var m in _matches)
            {
                if (m == null) continue;
                var row = Row(_listContent);

                Label(row.transform, m.EndedLocal.ToString("dd/MM  HH:mm"), TextSecondary, 15,
                    TextAlignmentOptions.MidlineLeft, new Vector2(0f, 0f), new Vector2(0.22f, 1f),
                    new Vector4(12, 0, 4, 0));

                string outcome = m.Won == null ? "DRAW" : (m.Won.Value ? "WIN" : "LOSS");
                Label(row.transform, outcome,
                    m.Won == null ? TextSecondary : (m.Won.Value ? WinGreen : LossRed), 15,
                    TextAlignmentOptions.MidlineLeft, new Vector2(0.22f, 0f), new Vector2(0.34f, 1f),
                    new Vector4(0, 0, 4, 0), FontStyles.Bold);

                Label(row.transform, "vs " + OpponentColorLabel.Of(m), TextPrimary, 16,
                    TextAlignmentOptions.MidlineLeft, new Vector2(0.34f, 0f), new Vector2(0.50f, 1f),
                    new Vector4(0, 0, 4, 0));

                var onPlay = m.OnPlayGameOne;
                Label(row.transform,
                    onPlay == null ? "" : (onPlay.Value ? "On play" : "On draw"),
                    TextSecondary, 14, TextAlignmentOptions.MidlineLeft,
                    new Vector2(0.50f, 0f), new Vector2(0.68f, 1f), new Vector4(0, 0, 4, 0));

                bool hasReplay = !string.IsNullOrEmpty(m.ReplayFile);

                Label(row.transform, m.EventName ?? "", TextDisabled, 13,
                    TextAlignmentOptions.MidlineRight,
                    new Vector2(0.68f, 0f), new Vector2(hasReplay ? 0.85f : 1f, 1f),
                    new Vector4(0, 0, hasReplay ? 6 : 12, 0));

                // Il pulsante compare **solo sulle righe che hanno un replay**.
                // Metterlo su tutte e farlo protestare al click sarebbe una
                // promessa ritirata: la registrazione e' spenta di default,
                // quindi la maggior parte delle righe non ne ha uno.
                if (hasReplay)
                {
                    var record = m;   // la variabile del ciclo non si cattura
                    Button(row.transform, "Replay",
                        new Vector2(0.86f, 0.15f), new Vector2(0.99f, 0.85f),
                        new Color(0.30f, 0.50f, 0.70f, 1f),
                        () => Features.ReplayExporter.OpenFor(record));
                }
            }

            Button(footer, "< Back", new Vector2(0f, 0f), new Vector2(0.24f, 1f),
                new Color(0.25f, 0.25f, 0.32f, 1f), ShowSummary);

            // L'interruttore della registrazione sta QUI e non nel pannello
            // Impostazioni, per due motivi. Il primo e' che questo e' il posto
            // in cui i replay contano: ci si arriva quando si guarda lo storico
            // delle partite, non mentre si regolano le animazioni. Il secondo e'
            // che quel pannello ha sei righe ad ancore scritte a mano, la scala
            // e' gia' stata ricalcolata una volta per farci stare la sesta, e
            // una settima stringerebbe tutte le descrizioni del 15% — una
            // regressione visibile su un pannello che non posso provare, per un
            // interruttore che si tocca una volta sola.
            bool recording = ModSettings.Instance.RecordReplays;
            Button(footer, recording ? "Recording replays: ON" : "Recording replays: off",
                new Vector2(0.27f, 0f), new Vector2(0.69f, 1f),
                recording ? new Color(0.20f, 0.45f, 0.30f, 1f) : ButtonIdle,
                () =>
                {
                    ModSettings.Instance.RecordReplays = !ModSettings.Instance.RecordReplays;
                    ModSettings.Instance.Save();
                    Toast.Info(ModSettings.Instance.RecordReplays
                        ? "Replay recording on - starts with your next match"
                        : "Replay recording off");
                    ShowHistory();
                });

            Button(footer, "Close", new Vector2(0.72f, 0f), new Vector2(1f, 1f),
                new Color(0.25f, 0.25f, 0.32f, 1f), Close);
        }

        // -----------------------------------------------------------------
        // Pezzi
        // -----------------------------------------------------------------

        private static string Percent(float? value) =>
            value == null ? "-" : value.Value.ToString("0.0") + "%";

        /// <summary>
        /// Una percentuale si colora solo quando c'e'. Il grigio del "-" dice
        /// "non lo so", che e' diverso da zero e non va confuso con una
        /// sconfitta.
        /// </summary>
        private static Color Tint(float? rate)
        {
            if (rate == null) return TextDisabled;
            if (rate.Value >= 55f) return WinGreen;
            if (rate.Value < 45f) return LossRed;
            return TextPrimary;
        }

        private static void Stat(Transform parent, string name, string value,
                                 Color? valueColor = null, string extra = null)
        {
            var row = Row(parent);
            Label(row.transform, name, TextSecondary, 16, TextAlignmentOptions.MidlineLeft,
                new Vector2(0f, 0f), new Vector2(0.45f, 1f), new Vector4(14, 0, 4, 0));
            Label(row.transform, value, valueColor ?? TextPrimary, 17,
                TextAlignmentOptions.MidlineRight,
                new Vector2(0.45f, 0f), new Vector2(extra == null ? 1f : 0.78f, 1f),
                new Vector4(0, 0, 12, 0), FontStyles.Bold);
            if (extra != null)
                Label(row.transform, extra, TextDisabled, 13, TextAlignmentOptions.MidlineRight,
                    new Vector2(0.78f, 0f), new Vector2(1f, 1f), new Vector4(0, 0, 12, 0));
        }

        private static Transform BeginModal()
        {
            // **Solo la finestra**, non lo stato della sessione.
            //
            // Qui c'era `Close()`, che oltre a distruggere il riquadro azzera
            // anche `_matches` — e le due schermate si ridisegnano passando di
            // qui, quindi i dati sparivano prima di essere letti. Peggio:
            // `Summarize(null)` restituisce un riassunto vuoto senza
            // protestare, quindi ogni mazzo diceva "nessuna partita" invece di
            // rompersi in modo visibile. Vedi MISTAKES.md #37.
            _frame?.Destroy();
            _frame = ListModal.Open("MTGAES_DeckStatsWindow",
                string.IsNullOrEmpty(_deckName) ? "(unnamed deck)" : _deckName,
                // Il nome puo' contenere i tag dell'editor dei nomi: qui si
                // disegnano, non si mostrano.
                richTitle: true,
                onClose: Close);
            _listContent = _frame.List;
            return _frame.Footer;
        }
    }
}
