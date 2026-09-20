using System;
using System.Diagnostics;
using System.IO;

namespace MTGAEnhancementSuite.Helpers
{
    /// <summary>
    /// Apre un file (o un indirizzo) col programma predefinito **senza
    /// lasciarselo attaccato addosso**.
    ///
    /// <para><b>Il difetto che ha prodotto questo file.</b> Il visualizzatore
    /// dei replay apriva la pagina con <c>Application.OpenURL</c>. Se il
    /// browser non era gia' in esecuzione, il processo del browser nasceva
    /// **figlio di MTGA**: chiudendo il gioco, Steam continuava a considerarlo
    /// in esecuzione — l'albero dei processi non era vuoto — e rifiutava di
    /// riavviarlo finche' non si chiudeva il browser. Il gioco nel frattempo
    /// non compariva piu' in Gestione attivita', quindi il sintomo sembrava
    /// non avere causa.</para>
    ///
    /// <para><b>Perche' passare da explorer.</b> <c>explorer.exe &lt;cosa&gt;</c>
    /// delega alla copia di Explorer gia' in esecuzione e termina subito: il
    /// programma che si apre diventa figlio **di quella**, non nostro.
    /// Misurato su questa macchina il 2026-09-20 lanciando un processo di prova
    /// dalle due strade:</para>
    /// <code>
    /// ShellExecute  -> figlio, padre = il processo che ha lanciato  (20348)
    /// via explorer  -> figlio, padre = explorer.exe                 (20996)
    /// </code>
    ///
    /// <para>Vale per qualunque cosa il mod aprira' in futuro, non solo per i
    /// replay: se un giorno si apre un indirizzo web o una cartella, si passa
    /// di qui.</para>
    /// </summary>
    internal static class ExternalOpen
    {
        /// <summary>
        /// Apre <paramref name="target"/> — un percorso di file o un indirizzo —
        /// e restituisce false se non c'e' riuscito.
        /// </summary>
        public static bool Detached(string target)
        {
            if (string.IsNullOrEmpty(target)) return false;

            try
            {
                // UseShellExecute: cosi' non si ereditano handle nemmeno verso
                // l'explorer.exe di passaggio, che comunque vive un istante.
                var info = new ProcessStartInfo("explorer.exe", Quote(target))
                {
                    UseShellExecute = true,
                };
                Process.Start(info);

                // explorer.exe restituisce 1 anche quando ha funzionato, quindi
                // il codice di uscita non dice niente e non lo guardiamo.
                Plugin.Log.LogInfo($"Aperto tramite explorer: {target}");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(
                    $"explorer.exe non ha aperto '{target}' ({ex.Message}); " +
                    "ripiego su Application.OpenURL.");
            }

            // Ripiego. Riporta il difetto che sta sopra — il browser torna a
            // essere figlio nostro — ma aprire il replay e' meglio che non
            // aprirlo, e qui ci si finisce solo se Explorer non c'e'.
            try
            {
                UnityEngine.Application.OpenURL(AsUrl(target));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Apertura di '{target}' fallita ({ex.Message}).");
                return false;
            }
        }

        private static string Quote(string target)
        {
            // Le virgolette servono ai percorsi con spazi, e il nome utente ne
            // ha spesso. Un percorso che ne contiene di sue non si puo' passare
            // in nessun modo sensato, e non puo' venire da noi: i nomi dei file
            // di replay li costruiamo noi da cifre e trattini.
            return "\"" + target.Replace("\"", "") + "\"";
        }

        /// <summary>
        /// <c>Application.OpenURL</c> vuole un indirizzo: un percorso Windows
        /// va convertito, le barre comprese.
        /// </summary>
        private static string AsUrl(string target)
        {
            if (target.Contains("://")) return target;
            if (Path.IsPathRooted(target)) return "file:///" + target.Replace('\\', '/');
            return target;
        }
    }
}
