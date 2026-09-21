using System;
using System.Collections.Generic;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.State;
using UnityEngine;
using Wotc.Mtgo.Gre.External.Messaging;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Registra l'esito delle partite, leggendo il flusso di messaggi che Arena
    /// scambia col server.
    ///
    /// **Un solo aggancio: <c>MatchManager.MessageReceived</c>.** Non usiamo
    /// <c>MatchCompleted</c>, e non e' una preferenza di stile:
    /// <c>MatchManager.Reset()</c> — che gira all'inizio di ogni partita dentro
    /// <c>Initialize</c> — **azzera <c>MatchCompleted</c>** insieme ad altri
    /// eventi, mentre lascia stare <c>MessageReceived</c>. Iscriversi a
    /// <c>MatchCompleted</c> una volta funzionerebbe per una partita sola, e
    /// dalla seconda in poi non lo direbbe nessuno. Iscriversi a ogni partita,
    /// all'opposto, accumulerebbe gestori duplicati su <c>MessageReceived</c>,
    /// che invece sopravvive. Un aggancio solo, dedotto dal contenuto: nessuno
    /// dei due modi di sbagliare. Vedi
    /// <c>research/arena-internals/match-stats-and-replays.md</c>.
    ///
    /// **Leggiamo lo stato gia' digerito da MatchManager, non il messaggio.**
    /// In <c>OnMessageReceived</c> Arena aggiorna <c>GameResults</c>,
    /// <c>MatchState</c>, <c>CurrentGameNumber</c> e <c>Format</c> **prima** di
    /// invocare il nostro gestore (riga 567, dopo il blocco che li scrive).
    /// Quindi quando ci arriva il controllo quei campi sono gia' quelli nuovi,
    /// e rifare il lavoro a mano vorrebbe dire solo poterlo sbagliare in modo
    /// diverso da Arena.
    /// </summary>
    internal static class MatchRecorder
    {
        /// <summary>Ogni quanto guardare se c'e' un MatchManager a cui agganciarsi.</summary>
        private const float PollSeconds = 0.25f;

        private static float _nextPoll;
        private static MatchManager _attached;

        private static MatchRecord _current;
        private static bool _saved;

        /// <summary>Sul gioco, per numero di partita. La prima lettura vince.</summary>
        private static readonly Dictionary<uint, bool> _onPlayByGame = new Dictionary<uint, bool>();

        /// <summary>
        /// Carte viste all'avversario: grpId -> colori che **l'oggetto stesso
        /// dichiara**.
        ///
        /// Il colore si legge da <c>GameObjectInfo.Color</c> e NON cercando il
        /// grpId nel database carte. Il perche' e' un difetto pagato il
        /// 2026-09-20: un'abilita' sullo stack e' anch'essa un GameObject, e il
        /// suo <c>GrpId</c> identifica **l'abilita'**, non una carta — ma vive
        /// nello stesso spazio numerico. Cercandolo fra le carte si trova
        /// un'altra carta qualunque: l'abilita' 92970 di Ajani's Pridemate
        /// (bianca) risolveva a "Deal Gone Bad", che e' nera, e un avversario
        /// mono-bianco veniva registrato come WB.
        ///
        /// Col campo <c>Color</c> il problema sparisce alla radice: le abilita'
        /// non ne hanno, le terre dicono <c>CardColor_Land</c> e gli artefatti
        /// <c>CardColor_Artifact</c>, che non sono colori. Resta solo cio' che
        /// e' davvero W/U/B/R/G.
        /// </summary>
        private static readonly Dictionary<uint, CardColorFlags> _opponentCards =
            new Dictionary<uint, CardColorFlags>();

        /// <summary>
        /// Il NOSTRO posto al tavolo in QUESTA partita, 0 = ancora ignoto.
        ///
        /// Non si usa <c>MatchManager.LocalPlayerSeatId</c> e il motivo e' un
        /// difetto pagato il 2026-09-20: quel campo si aggiorna solo dai
        /// messaggi indirizzati a un posto solo, e **`Reset()` non lo azzera
        /// fra una partita e l'altra**. All'inizio di un match vale ancora
        /// quello del match precedente (o 0 alla prima partita della sessione),
        /// e in quella finestra il filtro "scarta cio' che e' mio" scartava il
        /// posto sbagliato: le nostre carte finivano fra quelle dell'avversario.
        /// Con un mazzo mono-nero contro un Azorius usciva UWB.
        ///
        /// Questo invece si azzera a ogni <c>BeginMatch</c> e si impara dai
        /// messaggi di questa partita.
        /// </summary>
        private static uint _localSeat;

        /// <summary>
        /// Quanti oggetti abbiamo lasciato perdere perche' non sapevamo ancora
        /// dove fossimo seduti. Va nel log a fine match: e' la prova che la
        /// finestra cieca esiste davvero, e sa dire anche di no (zero).
        /// </summary>
        private static int _skippedBeforeSeatKnown;

        private static bool _loggedAttach;

        // -----------------------------------------------------------------
        // Aggancio
        // -----------------------------------------------------------------

        /// <summary>
        /// Chiamato dal ciclo del plugin. Cerca il <c>MatchManager</c> e si
        /// iscrive quando ne vede uno nuovo.
        ///
        /// Il sondaggio serve perche' <c>MatchManager</c> **non e' nella tabella
        /// dei servizi del Pantry**: il contenitore restituisce solo istanze
        /// registrate a mano, e un tipo che non c'e' non viene costruito ma
        /// torna null. Fuori dal duello quindi non esiste.
        /// </summary>
        public static void Tick()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollSeconds;

            MatchManager mm;
            try { mm = GameReflection.PantryGet<MatchManager>(); }
            catch { return; }

            if (mm == null) return;
            if (ReferenceEquals(mm, _attached)) return;

            Detach();

            try
            {
                mm.MessageReceived += OnMessage;
                _attached = mm;

                if (!_loggedAttach)
                {
                    _loggedAttach = true;
                    Plugin.Log.LogInfo("MatchRecorder: agganciato a MatchManager.MessageReceived.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"MatchRecorder: aggancio fallito: {ex.Message}");
            }
        }

        private static void Detach()
        {
            if (_attached == null) return;
            try { _attached.MessageReceived -= OnMessage; }
            catch { /* l'istanza puo' essere gia' morta: non e' un problema */ }
            _attached = null;
        }

        // -----------------------------------------------------------------
        // Il flusso
        // -----------------------------------------------------------------
        private static void OnMessage(GREToClientMessage msg)
        {
            // Un'eccezione qui verrebbe lanciata dentro il gestore dei messaggi
            // di Arena: la inghiottiamo tutta, perche' rompere il flusso GRE
            // vorrebbe dire rompere la partita in corso.
            try { OnMessageCore(msg); }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"MatchRecorder: {ex}");
            }
        }

        private static void OnMessageCore(GREToClientMessage msg)
        {
            if (msg == null || _attached == null) return;

            if (ReplayRecorder.IsRecording) ReplayRecorder.Capture(msg);

            if (msg.Type != GREMessageType.GameStateMessage) return;
            var gsm = msg.GameStateMessage;
            if (gsm == null) return;

            var mm = _attached;

            // --- inizio match ---
            if (mm.MatchState == MatchState.GameInProgress && _current == null)
                BeginMatch(mm);

            if (_current == null) return;

            // Il nostro posto, imparato da QUESTA partita. SystemSeatIds dice a
            // chi e' indirizzato il messaggio: quando c'e' un destinatario solo,
            // siamo noi.
            //
            // DOPO BeginMatch, non prima: BeginMatch azzera _localSeat, e
            // impararlo un attimo prima vorrebbe dire buttarlo via subito.
            if (msg.SystemSeatIds != null && msg.SystemSeatIds.Count == 1)
                _localSeat = msg.SystemSeatIds[0];

            // --- chi e' sul gioco, per partita ---
            // Stesso posto, stesso motivo: confrontare con
            // MatchManager.LocalPlayerSeatId qui avrebbe potuto invertire
            // "sul gioco" e "in risposta" nella stessa finestra cieca che
            // sporcava i colori. Se non sappiamo dove sediamo non si risponde:
            // la statistica sa gia' reggere un "non lo so" (resta null e non
            // entra in nessuna delle due percentuali).
            var turn = gsm.TurnInfo;
            if (_localSeat != 0 && turn != null && turn.TurnNumber == 1 && turn.ActivePlayer != 0)
            {
                uint game = mm.CurrentGameNumber == 0 ? 1u : mm.CurrentGameNumber;
                if (!_onPlayByGame.ContainsKey(game))
                    _onPlayByGame[game] = turn.ActivePlayer == _localSeat;
            }

            // --- cosa ha giocato l'avversario ---
            //
            // Finche' non sappiamo dove siamo seduti NON si raccoglie niente.
            // Sbagliare in apertura qui vuol dire attribuire all'avversario il
            // proprio mazzo, e siccome l'insieme e' cumulativo quell'errore non
            // si corregge piu' da solo. Perdere le prime carte e' il verso
            // giusto in cui sbagliare.
            var objects = gsm.GameObjects;
            if (objects != null)
            {
                foreach (var obj in objects)
                {
                    if (obj == null || obj.GrpId == 0) continue;
                    if (obj.OwnerSeatId == 0) continue;
                    if (_localSeat == 0) { _skippedBeforeSeatKnown++; continue; }
                    if (obj.OwnerSeatId == _localSeat) continue;

                    var colors = ColorsOf(obj);
                    if (colors == CardColorFlags.None) continue;   // abilita', terre, artefatti incolori

                    CardColorFlags had;
                    _opponentCards[obj.GrpId] = _opponentCards.TryGetValue(obj.GrpId, out had)
                        ? (had | colors) : colors;
                }
            }

            // --- fine match ---
            if (mm.MatchState == MatchState.MatchComplete && !_saved)
                FinishMatch(mm);
        }

        // -----------------------------------------------------------------
        // Inizio e fine
        // -----------------------------------------------------------------
        private static void BeginMatch(MatchManager mm)
        {
            _current = new MatchRecord
            {
                MatchId = mm.MatchID,
                StartedUtc = DateTime.UtcNow,
            };
            _saved = false;
            _onPlayByGame.Clear();
            _opponentCards.Clear();
            _localSeat = 0;
            _skippedBeforeSeatKnown = 0;

            ReadContext(mm, _current);
            // Il nome del mazzo lo sa gia': ReadContext() e' appena girato.
            // Serve per dare al file un nome leggibile invece del solo
            // identificativo del match.
            ReplayRecorder.BeginMatch(_current.MatchId, _current.DeckName);

            Plugin.Log.LogInfo(
                $"MatchRecorder: inizio match {_current.MatchId ?? "?"} " +
                $"mazzo='{_current.DeckName ?? "?"}' evento='{_current.EventName ?? "?"}'.");
        }

        /// <summary>
        /// Mazzo, evento e formato. Si leggono all'inizio e non alla fine:
        /// <c>MatchManager.Reset()</c> azzera <c>Event</c>, e a fine partita il
        /// contesto potrebbe essere gia' sparito.
        /// </summary>
        private static void ReadContext(MatchManager mm, MatchRecord rec)
        {
            try { rec.Format = mm.Format.ToString(); } catch { }

            try
            {
                var evt = mm.Event;
                var playerEvent = evt?.PlayerEvent;
                if (playerEvent == null) return;

                var info = playerEvent.EventInfo;
                if (info != null)
                {
                    rec.EventName = info.InternalEventName;
                    rec.Ranked = info.IsRanked;
                }

                var deck = playerEvent.CourseData?.CourseDeck;
                if (deck?.Summary != null)
                {
                    rec.DeckId = deck.Summary.DeckId.ToString();
                    rec.DeckName = deck.Summary.Name;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"MatchRecorder: contesto evento non letto ({ex.Message}).");
            }
        }

        private static void FinishMatch(MatchManager mm)
        {
            _saved = true;
            var rec = _current;
            _current = null;

            rec.EndedUtc = DateTime.UtcNow;

            try { rec.OpponentName = mm.OpponentInfo?.ScreenName; } catch { }
            rec.OpponentColors = (int)OpponentColorMask();
            rec.OpponentColorCards = OpponentColorCounts();

            // Arena calcola gia' chi ha vinto e lo espone come GREPlayerNum:
            // usiamo il suo risultato invece di riconfrontare gli id, cosi' non
            // possiamo dissentire dal client su chi ha vinto.
            try
            {
                foreach (var r in mm.GameResults)
                {
                    bool? won = (r.Result == ResultType.WinLoss)
                        ? (bool?)(r.Winner == GREPlayerNum.LocalPlayer)
                        : null;

                    if (r.Scope == MatchScope.Match)
                    {
                        rec.Won = won;
                    }
                    else if (r.Scope == MatchScope.Game)
                    {
                        uint n = (uint)(rec.Games.Count + 1);
                        bool onPlay;
                        rec.Games.Add(new GameRecord
                        {
                            GameNumber = n,
                            Won = won,
                            OnPlay = _onPlayByGame.TryGetValue(n, out onPlay) ? (bool?)onPlay : null,
                            Reason = r.Reason.ToString(),
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"MatchRecorder: esiti non letti ({ex.Message}).");
            }

            rec.ReplayFile = ReplayRecorder.FinishMatch(rec.MatchId);

            // Il file nasce all'INIZIO della partita, quando dell'avversario
            // non si sa ancora niente: i suoi colori si imparano guardando le
            // carte che gioca. Qui la partita e' finita e si sanno, quindi il
            // file prende adesso il suo nome definitivo — prima di Append(),
            // cosi' il registro memorizza il nome giusto la prima volta.
            var renamed = ReplayFiles.RenameWithOpponent(
                ReplayRecorder.Directory(), rec.ReplayFile, UI.OpponentColorLabel.Plain(rec));
            if (!string.IsNullOrEmpty(renamed)) rec.ReplayFile = renamed;

            bool ok = MatchStatsStore.Append(rec);

            string esito = rec.Won == null ? "pareggio/sospeso" : (rec.Won.Value ? "vinto" : "perso");
            Plugin.Log.LogInfo(
                $"MatchRecorder: fine match {rec.MatchId ?? "?"} — {esito}, " +
                $"{rec.Games.Count} partite, avversario '{rec.OpponentName ?? "?"}', " +
                $"colori W{rec.OpponentColorCards[0]} U{rec.OpponentColorCards[1]} " +
                $"B{rec.OpponentColorCards[2]} R{rec.OpponentColorCards[3]} G{rec.OpponentColorCards[4]} " +
                $"su {_opponentCards.Count} carte colorate viste, " +
                $"posto nostro {_localSeat}, {_skippedBeforeSeatKnown} oggetti ignorati " +
                $"prima di sapere dove sedevamo, salvato={ok}.");
            PerPlayerLog.Info(
                $"Match {esito}: deck='{rec.DeckName ?? "?"}' event='{rec.EventName ?? "?"}' " +
                $"games={rec.Games.Count}");
        }

        /// <summary>
        /// Quante carte distinte abbiamo visto per ciascun colore, nell'ordine
        /// W, U, B, R, G.
        ///
        /// Si contano le **carte**, non le apparizioni: la stessa creatura
        /// compare in decine di messaggi di stato, e contarle tutte farebbe
        /// sembrare enorme qualunque colore.
        /// </summary>
        private static int[] OpponentColorCounts()
        {
            var counts = new int[5];
            foreach (var pair in _opponentCards)
            {
                var c = pair.Value;
                if ((c & CardColorFlags.White) != 0) counts[0]++;
                if ((c & CardColorFlags.Blue) != 0) counts[1]++;
                if ((c & CardColorFlags.Black) != 0) counts[2]++;
                if ((c & CardColorFlags.Red) != 0) counts[3]++;
                if ((c & CardColorFlags.Green) != 0) counts[4]++;
            }
            return counts;
        }

        private static CardColorFlags OpponentColorMask()
        {
            var all = CardColorFlags.None;
            foreach (var pair in _opponentCards) all |= pair.Value;
            return all;
        }

        /// <summary>
        /// I colori che l'oggetto dichiara di avere. <c>CardColor_Land</c> e
        /// <c>CardColor_Artifact</c> non sono colori e non entrano: una
        /// pianura non rende bianco il mazzo di chi la gioca piu' di quanto lo
        /// renda la sua presenza in campo.
        /// </summary>
        private static CardColorFlags ColorsOf(GameObjectInfo obj)
        {
            var result = CardColorFlags.None;
            var colors = obj.Color;
            if (colors == null) return result;

            foreach (var c in colors)
            {
                switch (c)
                {
                    case CardColor.White: result |= CardColorFlags.White; break;
                    case CardColor.Blue:  result |= CardColorFlags.Blue;  break;
                    case CardColor.Black: result |= CardColorFlags.Black; break;
                    case CardColor.Red:   result |= CardColorFlags.Red;   break;
                    case CardColor.Green: result |= CardColorFlags.Green; break;
                }
            }
            return result;
        }
    }
}
