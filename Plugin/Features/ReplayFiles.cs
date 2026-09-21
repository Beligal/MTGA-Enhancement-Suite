using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// I file dei replay sul disco: come si chiamano, quali si possono
    /// cancellare e quali sono "da tenere".
    ///
    /// <para><b>Perche' esiste una classe separata.</b> Questo e' l'unico punto
    /// del mod che cancella file su richiesta dell'utente, ed e' scritto
    /// **senza una riga di Unity** di proposito: cosi' si esegue da un
    /// programma fuori dal gioco e si possono vedere le guardie rifiutare
    /// davvero, invece di fidarsi. Vale la regola 44 di MISTAKES.md.</para>
    ///
    /// <para><b>Le regole che questo file non infrange mai.</b></para>
    /// <list type="number">
    /// <item>Non cancella mai una cartella. Solo file, uno per uno, per nome.</item>
    /// <item>Non usa mai un carattere jolly per decidere cosa cancellare.</item>
    /// <item>Accetta solo un NOME di file — niente separatori di percorso,
    ///       niente percorsi assoluti, niente <c>..</c>.</item>
    /// <item>Accetta solo l'estensione dei replay, e cancella insieme a quella
    ///       solo la pagina <c>.html</c> con lo stesso nome.</item>
    /// <item>Ricontrolla che il percorso risolto stia DENTRO la cartella dei
    ///       replay, e che il suo genitore sia esattamente quella cartella.</item>
    /// </list>
    ///
    /// <para>Il quarto e il quinto controllo si sovrappongono apposta: il primo
    /// guarda il testo, il secondo il percorso che ne esce davvero. Un nome puo'
    /// sembrare innocuo e risolversi altrove.</para>
    ///
    /// <para><b>Le statistiche non si toccano.</b> Cancellare un replay non
    /// scrive niente in <c>matches.jsonl</c>: quel file e' solo-append e
    /// contiene lo storico delle partite, che deve sopravvivere alla
    /// cancellazione delle registrazioni. La finestra dei replay elenca le
    /// partite il cui file **esiste ancora sul disco**, quindi una registrazione
    /// cancellata sparisce dall'elenco da sola, senza che nessuno debba tenere
    /// aggiornato un secondo registro.</para>
    /// </summary>
    internal static class ReplayFiles
    {
        public const string RecordingExtension = ".mtgarep.gz";
        public const string PageExtension = ".html";

        /// <summary>I "da tenere", per nome di file. Sta nella cartella dei
        /// replay e non fra le statistiche, cosi' quel file resta intoccato.</summary>
        public const string FavouritesFileName = "_keep.txt";

        // =================================================================
        // Guardie
        // =================================================================

        /// <summary>
        /// Dice se <paramref name="fileName"/> e' un nome di replay che questa
        /// classe accetta di cancellare dalla cartella <paramref name="folder"/>.
        /// In caso contrario <paramref name="refusal"/> spiega quale controllo
        /// ha detto di no — serve nel log e nei test.
        /// </summary>
        public static bool CanDelete(string folder, string fileName, out string refusal)
        {
            refusal = null;

            if (string.IsNullOrEmpty(folder)) { refusal = "cartella dei replay sconosciuta"; return false; }
            if (string.IsNullOrEmpty(fileName)) { refusal = "nome di file vuoto"; return false; }

            // 1. Dev'essere un NOME, non un percorso. Blocca "..\\x", "sub/x" e
            //    "C:\\x" in un colpo solo, perche' per tutti e tre GetFileName
            //    restituisce qualcosa di diverso dall'originale.
            if (fileName != Path.GetFileName(fileName))
            {
                refusal = "il nome contiene un percorso";
                return false;
            }

            // 2. Niente caratteri che in un nome di file non possono starci.
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                refusal = "il nome contiene caratteri non ammessi";
                return false;
            }

            // 3. Solo registrazioni. Qualunque altra cosa — matches.jsonl, un
            //    .dll, il file dei "da tenere" — non e' roba nostra da togliere.
            if (!fileName.EndsWith(RecordingExtension, StringComparison.OrdinalIgnoreCase))
            {
                refusal = "non e' un file di replay (" + RecordingExtension + ")";
                return false;
            }

            // 4. E il nome non puo' essere SOLO l'estensione.
            if (fileName.Length <= RecordingExtension.Length)
            {
                refusal = "nome troppo corto";
                return false;
            }

            // 5. Ultimo controllo, sul percorso vero che ne esce: deve stare
            //    dentro la cartella dei replay, e il suo genitore dev'essere
            //    esattamente quella cartella.
            string full, root;
            try
            {
                full = Path.GetFullPath(Path.Combine(folder, fileName));
                root = Path.GetFullPath(folder);
            }
            catch (Exception ex)
            {
                refusal = "percorso non risolvibile (" + ex.Message + ")";
                return false;
            }

            var parent = Path.GetDirectoryName(full);
            if (parent == null ||
                !string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar),
                               root.TrimEnd(Path.DirectorySeparatorChar),
                               StringComparison.OrdinalIgnoreCase))
            {
                refusal = "il percorso finisce fuori dalla cartella dei replay";
                return false;
            }

            return true;
        }

        /// <summary>
        /// I file che <see cref="Delete"/> toglierebbe per questo replay, cioe'
        /// quelli che esistono davvero fra la registrazione e la sua pagina.
        /// Lista vuota se il nome non passa le guardie.
        /// </summary>
        public static List<string> Existing(string folder, string fileName)
        {
            var result = new List<string>();
            string refusal;
            if (!CanDelete(folder, fileName, out refusal)) return result;

            foreach (var path in Targets(folder, fileName))
                if (File.Exists(path)) result.Add(path);

            return result;
        }

        /// <summary>
        /// Cancella la registrazione e, se c'e', la pagina generata da lei.
        /// Restituisce false senza aver toccato niente se le guardie dicono no.
        /// </summary>
        public static bool Delete(string folder, string fileName, out string error)
        {
            error = null;

            string refusal;
            if (!CanDelete(folder, fileName, out refusal))
            {
                error = refusal;
                return false;
            }

            var failures = new List<string>();
            foreach (var path in Targets(folder, fileName))
            {
                try
                {
                    // File.Delete, mai Directory.Delete. E su un percorso
                    // costruito qui, non ricevuto da fuori.
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    failures.Add(Path.GetFileName(path) + ": " + ex.Message);
                }
            }

            if (failures.Count > 0)
            {
                error = string.Join("; ", failures.ToArray());
                return false;
            }
            return true;
        }

        /// <summary>
        /// I due percorsi che riguardano un replay: la registrazione e la
        /// pagina con lo stesso nome. Chiamato solo dopo le guardie.
        /// </summary>
        private static IEnumerable<string> Targets(string folder, string fileName)
        {
            yield return Path.Combine(folder, fileName);

            var stem = fileName.Substring(0, fileName.Length - RecordingExtension.Length);
            yield return Path.Combine(folder, stem + PageExtension);
        }

        // =================================================================
        // "Da tenere"
        // =================================================================

        /// <summary>
        /// Legge i nomi dei replay marcati "da tenere". Un file di testo, un
        /// nome per riga: si legge a occhio e si ripara con un editor, che per
        /// un elenco di dieci nomi vale piu' di un formato strutturato.
        /// </summary>
        public static HashSet<string> LoadKept(string folder)
        {
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(folder)) return kept;

            var path = Path.Combine(folder, FavouritesFileName);
            try
            {
                if (!File.Exists(path)) return kept;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    // Anche qui solo nomi: una riga con un percorso e' roba che
                    // non abbiamo scritto noi, e non la si fa girare oltre.
                    if (line != Path.GetFileName(line)) continue;
                    kept.Add(line);
                }
            }
            catch
            {
                // Un elenco illeggibile vuol dire "nessuno protetto", che e'
                // il lato prudente: al massimo si chiede una conferma in piu'.
            }
            return kept;
        }

        /// <summary>Scrive l'elenco, per temporaneo e rinomina.</summary>
        public static bool SaveKept(string folder, IEnumerable<string> kept)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            var path = Path.Combine(folder, FavouritesFileName);
            var tmp = path + ".tmp";
            try
            {
                var sb = new StringBuilder();
                foreach (var name in kept)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name != Path.GetFileName(name)) continue;
                    sb.AppendLine(name);
                }

                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }

        // =================================================================
        // Nomi dei file nuovi
        // =================================================================

        /// <summary>
        /// Il nome di un file di replay: data e ora **locali**, nome del mazzo,
        /// e una coda corta che lo rende unico.
        ///
        /// <para>Prima era <c>DateTime.UtcNow</c> piu' l'identificativo del
        /// match: l'ora era sfasata rispetto all'orologio di chi gioca — due ore
        /// in Italia d'estate — e i 36 caratteri del GUID non dicevano niente a
        /// nessuno.</para>
        ///
        /// <para><b>Il nome del mazzo puo' contenere i tag dello stile dei
        /// nomi</b> (<c>&lt;color=#F00&gt;</c> e simili): vanno tolti prima,
        /// altrimenti finiscono nel nome del file con dentro caratteri che un
        /// nome di file non ammette.</para>
        /// </summary>
        public static string BuildName(DateTime localTime, string deckName, string matchId)
        {
            var sb = new StringBuilder();
            sb.Append(localTime.ToString("yyyyMMdd-HHmm"));

            var deck = Sanitize(StripTags(deckName));
            if (deck.Length > 0)
            {
                if (deck.Length > 40) deck = deck.Substring(0, 40).TrimEnd();
                sb.Append(' ').Append(deck);
            }

            // Una coda corta perche' due partite possono cominciare nello stesso
            // minuto con lo stesso mazzo — succede nei tornei a piu' partite.
            var tail = Sanitize(matchId ?? "");
            if (tail.Length > 6) tail = tail.Substring(0, 6);
            if (tail.Length > 0) sb.Append(" [").Append(tail).Append(']');

            sb.Append(RecordingExtension);
            return sb.ToString();
        }


        /// <summary>
        /// Il nome con dentro l'avversario: da
        /// <c>"20260921-1737 Humans [abc123].mtgarep.gz"</c> a
        /// <c>"20260921-1737 Humans vs WB [abc123].mtgarep.gz"</c>.
        ///
        /// <para>E' una funzione di sole stringhe — non tocca il disco — cosi'
        /// si prova da sola. Il pezzo "vs XX" va PRIMA della coda fra parentesi
        /// quadre, che resta l'ultima cosa del nome e serve a distinguere due
        /// partite cominciate nello stesso minuto con lo stesso mazzo.</para>
        ///
        /// <para>Restituisce il nome invariato se non c'e' niente da
        /// aggiungere: chi chiama non deve preoccuparsi del caso "avversario
        /// sconosciuto".</para>
        /// </summary>
        public static string WithOpponent(string fileName, string opponent)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;

            var vs = Sanitize(StripTags(opponent));
            if (vs.Length == 0) return fileName;
            if (vs.Length > 10) vs = vs.Substring(0, 10).TrimEnd();

            if (!fileName.EndsWith(RecordingExtension, StringComparison.OrdinalIgnoreCase))
                return fileName;

            var stem = fileName.Substring(0, fileName.Length - RecordingExtension.Length);

            // Gia' fatto? Non si accumula "vs WB vs WB" se questo gira due volte.
            if (stem.IndexOf(" vs ", StringComparison.OrdinalIgnoreCase) >= 0) return fileName;

            var insert = " vs " + vs;
            if (stem.EndsWith("]", StringComparison.Ordinal))
            {
                int open = stem.LastIndexOf(" [", StringComparison.Ordinal);
                if (open > 0) stem = stem.Substring(0, open) + insert + stem.Substring(open);
                else stem += insert;
            }
            else stem += insert;

            return stem + RecordingExtension;
        }

        /// <summary>
        /// Rinomina la registrazione (e la sua pagina, se per qualche motivo
        /// esiste gia') aggiungendo i colori dell'avversario. Restituisce il
        /// nome nuovo, oppure <c>null</c> se non si e' fatto niente.
        ///
        /// <para><b>Passa dalle stesse guardie della cancellazione</b>, su
        /// entrambi i nomi: spostare un file e' pericoloso quanto toglierlo, e
        /// "vecchio" e "nuovo" devono stare tutti e due dentro la cartella dei
        /// replay ed essere registrazioni.</para>
        ///
        /// <para>Se qualcosa non va, il file resta col nome che aveva e il
        /// chiamante continua a usare quello: un nome meno bello e' un
        /// inconveniente, un replay che non si trova piu' e' un danno.</para>
        /// </summary>
        public static string RenameWithOpponent(string folder, string fileName, string opponent)
        {
            string refusal;
            if (!CanDelete(folder, fileName, out refusal)) return null;

            var target = WithOpponent(fileName, opponent);
            if (string.Equals(target, fileName, StringComparison.Ordinal)) return null;
            if (!CanDelete(folder, target, out refusal)) return null;

            var from = Path.Combine(folder, fileName);
            var to = Path.Combine(folder, target);

            // Non si sovrascrive mai un file che c'e' gia': nel dubbio si tiene
            // quello che c'e' e si rinuncia al nome piu' bello.
            if (!File.Exists(from) || File.Exists(to)) return null;

            try
            {
                File.Move(from, to);
            }
            catch
            {
                return null;
            }

            // La pagina di solito non esiste ancora — si scrive quando si preme
            // "Open" — ma se c'e' deve seguire il suo file, altrimenti resta
            // orfana e non la cancella piu' nessuno.
            try
            {
                var oldPage = Path.Combine(folder,
                    fileName.Substring(0, fileName.Length - RecordingExtension.Length) + PageExtension);
                var newPage = Path.Combine(folder,
                    target.Substring(0, target.Length - RecordingExtension.Length) + PageExtension);
                if (File.Exists(oldPage) && !File.Exists(newPage)) File.Move(oldPage, newPage);
            }
            catch { }

            return target;
        }

        /// <summary>Toglie i tag di rich text: <c>&lt;...&gt;</c>.</summary>
        public static string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool inside = false;
            foreach (var c in s)
            {
                if (c == '<') { inside = true; continue; }
                if (c == '>') { inside = false; continue; }
                if (!inside) sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Rende una stringa usabile in un nome di file: via i caratteri non
        /// ammessi, via gli spazi doppi, via punti e spazi in coda (Windows non
        /// li tiene).
        /// </summary>
        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            bool lastWasSpace = false;
            foreach (var c in s)
            {
                char ch = c;
                if (Array.IndexOf(invalid, ch) >= 0) ch = ' ';
                if (ch == ' ')
                {
                    if (lastWasSpace) continue;
                    lastWasSpace = true;
                }
                else lastWasSpace = false;
                sb.Append(ch);
            }

            return sb.ToString().Trim().TrimEnd('.', ' ');
        }
    }
}
