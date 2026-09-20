using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// La pagina del visualizzatore: **dati in JSON e modello HTML**, e niente
    /// altro.
    ///
    /// Sta in un file suo, separato da <see cref="ReplayExporter"/>, perche'
    /// qui dentro non c'e' una riga di Unity: cosi' la pagina si puo' generare
    /// e **aprire davvero in un browser** da un piccolo programma di prova,
    /// senza avviare Arena. Una pagina che nessuno ha mai visto e' una pagina
    /// non provata, e questo e' il modo meno costoso di vederla.
    ///
    /// Tutto cio' che richiede il gioco — il database delle carte, i messaggi
    /// all'utente, l'apertura del browser — resta nell'esportatore.
    /// </summary>
    internal static class ReplayHtml
    {
        /// <summary>L'intestazione della pagina. Il replay da solo non la sa.</summary>
        internal sealed class Meta
        {
            public string Deck;
            public string Opponent;
            public string Event;
            public string Format;
            public bool Ranked;
            public string Date;
            public bool? Won;
            /// <summary>La registrazione si e' interrotta a meta' partita.</summary>
            public bool Truncated;
        }

        // -----------------------------------------------------------------
        // JSON

        /// <summary>
        /// Il replay in un solo oggetto JSON — **il confine** fra il nostro C#
        /// e la pagina.
        ///
        /// Le chiavi sono corte di proposito: una partita lunga sono centinaia
        /// di passi per qualche decina di oggetti l'uno, e questo testo finisce
        /// dentro il file che si apre. Pubblico perche' un programma di prova
        /// possa produrlo senza Unity.
        /// </summary>
        public static string Json(ReplayReconstructor.ReplayDocument doc, Meta meta)
        {
            var cards = new Dictionary<string, object>();
            foreach (var kv in doc.Cards)
            {
                var c = kv.Value;
                cards[kv.Key.ToString()] = new Dictionary<string, object>
                {
                    { "n", c.Name ?? "" },
                    { "sup", c.SuperTypes },
                    { "ct", c.CoreTypes },
                    { "st", c.Subtypes },
                    { "c", c.Colors ?? "" },
                };
            }

            var steps = new List<object>(doc.Steps.Count);
            foreach (var s in doc.Steps)
            {
                var seats = new List<object>(s.Seats.Count);
                foreach (var p in s.Seats)
                    seats.Add(new object[] { p.Seat, p.Life, p.Hand, p.Graveyard, p.Exile, p.Library });

                var board = new List<object>(s.Board.Count);
                foreach (var b in s.Board)
                {
                    // Le bandierine stanno in una maschera e non in sei numeri
                    // separati: sono sei cifre per OGNI oggetto di OGNI passo,
                    // e questo testo viaggia dentro il file che si apre.
                    //   1 abilita'  2 gettone  4 malattia d'evocazione
                    //   8 attacca  16 blocca
                    int flags =
                        (b.IsAbility ? 1 : 0) | (b.IsToken ? 2 : 0) | (b.SummoningSick ? 4 : 0) |
                        (b.Attacking ? 8 : 0) | (b.Blocking ? 16 : 0);

                    board.Add(new object[]
                    {
                        b.InstanceId, b.GrpId, (int)b.Zone, b.Seat,
                        b.Tapped ? 1 : 0, b.Power, b.Toughness, b.Damage, flags
                    });
                }

                steps.Add(new Dictionary<string, object>
                {
                    { "g", s.GameNumber },
                    { "t", s.Turn },
                    { "ap", s.ActiveSeat },
                    { "ph", s.Phase ?? "" },
                    { "st", s.Step ?? "" },
                    { "ev", s.Events },
                    { "p", seats },
                    { "b", board },
                });
            }

            var payload = new Dictionary<string, object>
            {
                { "meta", new Dictionary<string, object>
                    {
                        { "deck", meta.Deck ?? "" },
                        { "opp", meta.Opponent ?? "" },
                        { "event", meta.Event ?? "" },
                        { "format", meta.Format ?? "" },
                        { "ranked", meta.Ranked },
                        { "date", meta.Date ?? "" },
                        { "won", meta.Won.HasValue ? (meta.Won.Value ? 1 : 0) : -1 },
                        { "seat", doc.LocalSeat },
                        { "truncated", meta.Truncated },
                    }
                },
                { "cards", cards },
                { "steps", steps },
            };

            return JsonConvert.SerializeObject(payload, Formatting.None);
        }

        // -----------------------------------------------------------------
        // La pagina

        /// <summary>Il segnaposto che il modello costruito porta al posto dei dati.</summary>
        public const string DataPlaceholder = "__MTGAPLUS_REPLAY_DATA__";

        /// <summary>
        /// Mette i dati dentro il modello. **Funzione pura**: il modello glielo
        /// passa il chiamante, cosi' un programma di prova puo' leggerlo da
        /// disco e questo file non deve sapere nulla di risorse incorporate.
        /// </summary>
        public static string Inject(string template, string json)
        {
            if (template == null) return null;

            // Due porte da chiudere prima di infilare dati in una pagina.
            //
            // 1. `</script>` dentro una stringa JSON chiude il blocco. Non puo'
            //    venire dal nostro schema, ma i nomi delle carte arrivano dal
            //    database di Arena e non si mettono dati in una pagina senza
            //    chiudere questa porta.
            // 2. `$` e' speciale per Regex.Replace ma **non** per string.Replace,
            //    che e' quello che usiamo: nessun problema, ed e' il motivo per
            //    cui resta string.Replace.
            json = json.Replace("</", "<\\/");

            int at = template.IndexOf(DataPlaceholder, StringComparison.Ordinal);
            if (at < 0)
            {
                // Il modello costruito non contiene il segnaposto: qualcuno ha
                // ricostruito il visualizzatore cambiandogli il nome. Meglio
                // saperlo qui che vedere una pagina vuota.
                throw new InvalidOperationException(
                    "Il modello del visualizzatore non contiene " + DataPlaceholder +
                    ": ricostruisci viewer/ con `npm run build`.");
            }

            return template.Substring(0, at) + json + template.Substring(at + DataPlaceholder.Length);
        }
    }
}
