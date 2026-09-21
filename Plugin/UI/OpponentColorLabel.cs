using MTGAEnhancementSuite.State;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// "vs WB": i colori visti all'avversario, come testo colorato.
    ///
    /// Sta in un file suo perche' lo usano due finestre — le statistiche di un
    /// mazzo e l'elenco dei replay — e perche' la regola della soglia e' una
    /// **decisione**, non un dettaglio di disegno: merita un posto dove si
    /// trova, non una riga in fondo a una finestra.
    /// </summary>
    internal static class OpponentColorLabel
    {
        /// <summary>Lettere e tinte dei cinque colori, come nella finestra di importazione.</summary>
        private static readonly (int Bit, string Letter, string Hex)[] Palette =
        {
            (1,  "W", "EFE7CB"),
            (2,  "U", "4A90D9"),
            (4,  "B", "A98FC0"),
            (8,  "R", "D9534F"),
            (16, "G", "58B368"),
        };

        /// <summary>Quante carte di un colore servono per entrare nell'etichetta.</summary>
        private const int SplashThreshold = 2;

        /// <summary>
        /// I colori dell'avversario, con le sbavature messe da parte.
        ///
        /// **Un colore entra nell'etichetta con almeno due carte distinte.**
        /// Serve perche' "ogni colore visto" e' letteralmente vero e
        /// praticamente inutile: una sola rimozione splashata trasformava un
        /// mazzo mono-bianco in "WB", che non e' l'avversario che il giocatore
        /// ricorda di aver affrontato.
        ///
        /// Le carte singole non si buttano: si scrivono piu' piccole e
        /// smorzate dopo un "+". Nascondere un dato osservato sarebbe mentire
        /// al contrario.
        ///
        /// I conteggi arrivano dal record. Per i match salvati prima che
        /// esistessero si ricade sulla vecchia maschera, che e' tutto quello
        /// che quei record hanno.
        /// </summary>
        public static string Of(MatchRecord m)
        {
            var counts = m.OpponentColorCards;
            if (counts == null || counts.Length < 5) return Pips(m.OpponentColors);

            int total = 0;
            foreach (var n in counts) total += n;
            if (total == 0) return Pips(m.OpponentColors);

            var main = new System.Text.StringBuilder();
            var splash = new System.Text.StringBuilder();

            for (int i = 0; i < Palette.Length; i++)
            {
                if (counts[i] <= 0) continue;
                var c = Palette[i];
                if (counts[i] >= SplashThreshold)
                    main.Append("<color=#").Append(c.Hex).Append(">").Append(c.Letter).Append("</color>");
                else
                    splash.Append(c.Letter);
            }

            // Nessun colore arriva alla soglia (una partita finita subito):
            // meglio mostrare il poco che si e' visto che un punto interrogativo.
            if (main.Length == 0) return Pips(m.OpponentColors);

            if (splash.Length > 0)
                main.Append("<size=70%><color=#6A6A80> +").Append(splash).Append("</color></size>");

            return main.ToString();
        }

        /// <summary>
        /// Le stesse lettere di <see cref="Of"/> ma in chiaro: niente tag, solo
        /// i colori che superano la soglia. Serve dove il rich text non si puo'
        /// usare — il nome di un file, una finestra di conferma — e sta qui
        /// perche' la soglia dev'essere UNA: due funzioni che decidono da sole
        /// chi e' "WB" finirebbero per non essere d'accordo.
        ///
        /// Restituisce stringa vuota se non si sa niente, cosi' chi compone un
        /// nome di file puo' semplicemente saltare il pezzo.
        /// </summary>
        public static string Plain(MatchRecord m)
        {
            var counts = m.OpponentColorCards;
            if (counts == null || counts.Length < 5) return PlainMask(m.OpponentColors);

            int total = 0;
            foreach (var n in counts) total += n;
            if (total == 0) return PlainMask(m.OpponentColors);

            var main = new System.Text.StringBuilder();
            for (int i = 0; i < Palette.Length; i++)
                if (counts[i] >= SplashThreshold) main.Append(Palette[i].Letter);

            // Nessuno arriva alla soglia: si ricade sulla maschera, come Of().
            return main.Length > 0 ? main.ToString() : PlainMask(m.OpponentColors);
        }

        private static string PlainMask(int mask)
        {
            if (mask == 0) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var c in Palette)
                if ((mask & c.Bit) != 0) sb.Append(c.Letter);
            return sb.ToString();
        }

        private static string Pips(int mask)
        {
            if (mask == 0) return "<color=#8080A0>?</color>";
            var sb = new System.Text.StringBuilder();
            foreach (var c in Palette)
                if ((mask & c.Bit) != 0)
                    sb.Append("<color=#").Append(c.Hex).Append(">").Append(c.Letter).Append("</color>");
            return sb.Length == 0 ? "<color=#8080A0>?</color>" : sb.ToString();
        }
    }
}
