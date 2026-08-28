using System;
using System.IO;
using System.Reflection;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Toglie le tracce lasciate dalle funzioni server (lobby private, browser
    /// delle partite, inviti <c>mtgaes://</c>), rimosse nella 0.20.0.
    ///
    /// Serve perche' l'aggiornatore **sostituisce** i file, non li cancella: su
    /// chi aggiorna da una versione precedente, cio' che non fa piu' parte della
    /// release resta li' com'era. L'unico modo di ripulirlo e' che sia la
    /// versione nuova a farlo, all'avvio.
    ///
    /// Regole che questa classe si impone, perche' cancella roba sul computer
    /// di qualcun altro:
    ///   - un solo nome di file, esatto, nella cartella del plugin. Niente
    ///     pattern, niente ricorsione, niente cancellazioni per estensione;
    ///   - non tocca i backup delle impostazioni dell'utente
    ///     (settings.json.*.bak): sono suoi, non nostri;
    ///   - se non trova qualcosa, non fa niente e tira dritto;
    ///   - nessuna eccezione puo' uscire da qui. Una pulizia opzionale non deve
    ///     mai poter impedire l'avvio della mod.
    /// </summary>
    internal static class LegacyCleanup
    {
        /// <summary>Configurazione del backend Firebase di Dan: ApiKey, ProjectId, URL.</summary>
        private const string ObsoleteConfigFileName = "config.json";

        /// <summary>Schema URL degli inviti alle lobby, sotto HKEY_CURRENT_USER.</summary>
        private const string UrlSchemeKeyParent = @"Software\Classes";
        private const string UrlSchemeKeyName = "mtgaes";

        public static void Run()
        {
            RemoveObsoleteConfig();
            RemoveUrlSchemeKey();
        }

        /// <summary>
        /// Cancella <c>config.json</c> dalla cartella in cui gira il plugin.
        /// Conteneva le credenziali del progetto Firebase di Dan — un servizio a
        /// pagamento di un'altra persona — per funzioni che non esistono piu'.
        /// Si cancella invece di svuotarlo: svuotarlo lascerebbe li' un file da
        /// zero byte per sempre.
        /// </summary>
        private static void RemoveObsoleteConfig()
        {
            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(pluginDir))
                    return;

                var path = Path.Combine(pluginDir, ObsoleteConfigFileName);
                if (!File.Exists(path))
                    return;

                File.Delete(path);
                Plugin.Log.LogInfo($"LegacyCleanup: rimosso {ObsoleteConfigFileName} (configurazione del server, non piu' usata)");
            }
            catch (Exception ex)
            {
                // Cartella di sola lettura, file in uso, permessi: pazienza.
                Plugin.Log.LogWarning($"LegacyCleanup: {ObsoleteConfigFileName} non rimosso ({ex.Message})");
            }
        }

        /// <summary>
        /// Cancella <c>HKCU\Software\Classes\mtgaes</c>, lo schema URL degli
        /// inviti. Esiste anche una chiave gemella in <c>HKLM</c>, scritta
        /// dall'MSI per tutta la macchina: quella **non** la tocchiamo, perche'
        /// il gioco non gira con privilegi di amministratore. Se ne occupa
        /// Windows Installer al primo aggiornamento dall'MSI, dato che il
        /// componente e' stato tolto dal pacchetto.
        /// </summary>
        private static void RemoveUrlSchemeKey()
        {
            try
            {
                // Reflection su mscorlib, non Microsoft.Win32.Registry diretto:
                // da quando il plugin ha come target netstandard 2.1 (Unity 6.3)
                // quel tipo si risolve sullo stub in Managed\, che dichiara i
                // campi statici senza inizializzarli — a runtime CurrentUser e'
                // null. Stessa tecnica che usava UrlSchemeRegistrar.
                var registryType = Type.GetType("Microsoft.Win32.Registry, mscorlib");
                var currentUser = registryType?
                    .GetField("CurrentUser", BindingFlags.Public | BindingFlags.Static)?
                    .GetValue(null);
                if (currentUser == null)
                    return;

                var openSubKey = currentUser.GetType().GetMethod(
                    "OpenSubKey", new[] { typeof(string), typeof(bool) });
                if (openSubKey == null)
                    return;

                var classes = openSubKey.Invoke(currentUser, new object[] { UrlSchemeKeyParent, true });
                if (classes == null)
                    return;

                using ((IDisposable)classes)
                {
                    // Se la sottochiave non c'e', non facciamo niente: aprirla e
                    // basta evita di dover distinguere "assente" da "errore".
                    var probe = openSubKey.Invoke(classes, new object[] { UrlSchemeKeyName, false });
                    if (probe == null)
                        return;
                    ((IDisposable)probe).Dispose();

                    var deleteTree = classes.GetType().GetMethod(
                        "DeleteSubKeyTree", new[] { typeof(string) });
                    if (deleteTree == null)
                        return;

                    deleteTree.Invoke(classes, new object[] { UrlSchemeKeyName });
                    Plugin.Log.LogInfo($@"LegacyCleanup: rimossa HKCU\{UrlSchemeKeyParent}\{UrlSchemeKeyName} (schema degli inviti, non piu' usato)");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LegacyCleanup: chiave {UrlSchemeKeyName} non rimossa ({ex.Message})");
            }
        }
    }
}
