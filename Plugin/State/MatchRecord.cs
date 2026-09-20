using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MTGAEnhancementSuite.State
{
    /// <summary>Esito di una partita singola dentro un match.</summary>
    internal sealed class GameRecord
    {
        [JsonProperty("n")]
        public uint GameNumber { get; set; }

        /// <summary>true vinta, false persa, null pareggio o sospesa.</summary>
        [JsonProperty("won")]
        public bool? Won { get; set; }

        /// <summary>true sul gioco, false in risposta, null se non l'abbiamo visto.</summary>
        [JsonProperty("play")]
        public bool? OnPlay { get; set; }

        /// <summary>Come e' finita: Game, Concede, Timeout... (nome dell'enum di Arena).</summary>
        [JsonProperty("why")]
        public string Reason { get; set; }
    }

    /// <summary>
    /// Un match giocato, come lo salviamo.
    ///
    /// **I nomi sono salvati accanto agli id, non al posto loro.** Se l'utente
    /// cancella o ricrea un mazzo l'id cambia e lo storico si stacca: senza il
    /// nome del momento resterebbe un GUID illeggibile. Vale anche per il
    /// formato e per l'evento.
    ///
    /// Le proprieta' JSON hanno nomi corti di proposito: questo file si scrive
    /// una riga per match e cresce per sempre.
    /// </summary>
    internal sealed class MatchRecord
    {
        /// <summary>Versione del formato: se un giorno cambia, si sa cosa si sta leggendo.</summary>
        [JsonProperty("v")]
        public int Version { get; set; } = 1;

        [JsonProperty("id")]
        public string MatchId { get; set; }

        [JsonProperty("deck")]
        public string DeckId { get; set; }

        /// <summary>Il nome che il mazzo aveva quando si e' giocato, tag compresi.</summary>
        [JsonProperty("deckName")]
        public string DeckName { get; set; }

        [JsonProperty("event")]
        public string EventName { get; set; }

        [JsonProperty("ranked")]
        public bool Ranked { get; set; }

        [JsonProperty("format")]
        public string Format { get; set; }

        [JsonProperty("start")]
        public DateTime StartedUtc { get; set; }

        [JsonProperty("end")]
        public DateTime EndedUtc { get; set; }

        /// <summary>true vinto, false perso, null pareggio o sospeso.</summary>
        [JsonProperty("won")]
        public bool? Won { get; set; }

        [JsonProperty("opp")]
        public string OpponentName { get; set; }

        /// <summary>
        /// Colori visti all'avversario, come maschera di <c>CardColorFlags</c>
        /// (W=1, U=2, B=4, R=8, G=16). Arena **non** dice il mazzo
        /// dell'avversario: questo e' cio' che si e' visto giocare, non cio'
        /// che aveva in mano.
        /// </summary>
        [JsonProperty("oppColors")]
        public int OpponentColors { get; set; }

        /// <summary>
        /// Quante carte distinte per colore, nell'ordine W, U, B, R, G.
        ///
        /// Si salvano i **conteggi** e non solo la maschera perche' la regola
        /// per decidere cosa sta nell'etichetta ("almeno N carte") e' una
        /// scelta che puo' cambiare: coi conteggi si ricalcola sullo storico,
        /// con la sola maschera si ricomincerebbe da capo.
        /// </summary>
        [JsonProperty("oppColorCards")]
        public int[] OpponentColorCards { get; set; } = new int[5];

        [JsonProperty("games")]
        public List<GameRecord> Games { get; set; } = new List<GameRecord>();

        /// <summary>Nome del file di replay accanto a questo match, se registrato.</summary>
        [JsonProperty("replay", NullValueHandling = NullValueHandling.Ignore)]
        public string ReplayFile { get; set; }

        // ---- comodita' di lettura, non salvate ----

        [JsonIgnore]
        public DateTime EndedLocal => EndedUtc.ToLocalTime();

        /// <summary>
        /// Sul gioco nella PRIMA partita del match. E' la definizione usata per
        /// la statistica "On Play": dalla seconda in poi la scelta non e' piu'
        /// del sorteggio ma di chi ha perso, quindi mescolarle direbbe un'altra
        /// cosa.
        /// </summary>
        [JsonIgnore]
        public bool? OnPlayGameOne
        {
            get
            {
                if (Games == null) return null;
                foreach (var g in Games)
                    if (g != null && g.GameNumber <= 1) return g.OnPlay;
                return null;
            }
        }
    }
    /// <summary>
    /// I numeri di un mazzo, gia' calcolati.
    ///
    /// **Politica sui pareggi:** un match senza vincitore (pareggio o sospeso)
    /// conta fra i match giocati ma **non entra nella percentuale**, ne' sopra
    /// ne' sotto la riga. Dividere per un totale che comprende partite senza
    /// esito darebbe una percentuale piu' bassa del vero senza dirlo. La
    /// finestra lo scrive quando ce n'e' almeno uno.
    /// </summary>
    internal sealed class DeckSummary
    {
        public int Matches;
        public int MatchWins;
        public int MatchDraws;

        public int Games;
        public int GameWins;
        public int GameDraws;

        public int OnPlayMatches;
        public int OnPlayWins;
        public int OnDrawMatches;
        public int OnDrawWins;

        /// <summary>Quante volte si e' giocato in ogni evento, dal piu' frequente.</summary>
        public List<KeyValuePair<string, int>> ByEvent = new List<KeyValuePair<string, int>>();

        public DateTime? LastPlayedUtc;

        /// <summary>Percentuale sui match decisi, o null se non ce n'e' nessuno.</summary>
        public float? WinRate => Decided(Matches, MatchDraws, MatchWins);

        public float? OnPlayRate => Decided(OnPlayMatches, 0, OnPlayWins);

        public float? OnDrawRate => Decided(OnDrawMatches, 0, OnDrawWins);

        private static float? Decided(int total, int draws, int wins)
        {
            int decided = total - draws;
            if (decided <= 0) return null;
            return wins * 100f / decided;
        }
    }
}
