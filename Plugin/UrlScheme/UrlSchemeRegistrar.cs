using System;
using System.Diagnostics;
using System.Reflection;

namespace MTGAEnhancementSuite.UrlScheme
{
    internal static class UrlSchemeRegistrar
    {
        private const string Scheme = "mtgaes";

        /// <summary>
        /// Registers the mtgaes:// URL protocol handler pointing at the current MTGA.exe.
        /// Uses HKCU so no admin rights needed.
        /// </summary>
        public static void Register()
        {
            var platform = Environment.OSVersion.Platform;

            if (platform == PlatformID.Win32NT || platform == PlatformID.Win32Windows)
            {
                RegisterWindows();
            }
            else if (platform == PlatformID.Unix || platform == PlatformID.MacOSX)
            {
                // macOS/Linux: URL scheme registration will be handled by the installer.
                // The plugin cannot register schemes at runtime on these platforms
                // without root access or a properly signed .app bundle.
                Plugin.Log.LogInfo("URL scheme registration skipped (non-Windows platform)");
            }
            else
            {
                Plugin.Log.LogWarning($"URL scheme registration not supported on platform: {platform}");
            }
        }

        private static void RegisterWindows()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule.FileName;

                using (var scheme = CreateSubKey(CurrentUser(), $@"Software\Classes\{Scheme}"))
                {
                    SetValue(scheme, "", "URL:MTGA Enhancement Suite");
                    SetValue(scheme, "URL Protocol", "");

                    using (var commandKey = CreateSubKey(scheme, @"shell\open\command"))
                    {
                        SetValue(commandKey, "", $"\"{exePath}\" \"%1\"");
                    }
                }

                Plugin.Log.LogInfo($"URL scheme '{Scheme}://' registered → {exePath}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Failed to register URL scheme: {ex.Message}");
            }
        }

        // Il registro si raggiunge per reflection, non via Microsoft.Win32.Registry.
        //
        // Da quando il plugin ha come target netstandard 2.1 (Unity 6.3), il
        // compilatore risolve quel tipo su Managed\Microsoft.Win32.Registry.dll,
        // che e' un ref assembly stub: dichiara i campi statici senza
        // inizializzarli, quindi a runtime Registry.CurrentUser e' null e la
        // registrazione moriva con una NullReferenceException. L'implementazione
        // vera sta in mscorlib, che pero' non possiamo referenziare senza
        // scontrarci con il facade netstandard. Quindi reflection su mscorlib: i
        // RegistryKey restano object, perche' il tipo compile-time (quello dello
        // stub) non e' quello che torna a runtime.
        private static object CurrentUser()
        {
            var registry = Type.GetType("Microsoft.Win32.Registry, mscorlib");
            if (registry == null)
                throw new InvalidOperationException("Microsoft.Win32.Registry non trovato in mscorlib.");

            var key = registry.GetField("CurrentUser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (key == null)
                throw new InvalidOperationException("Registry.CurrentUser nullo (assembly stub?).");

            return key;
        }

        private static IDisposable CreateSubKey(object key, string path)
        {
            var method = key.GetType().GetMethod("CreateSubKey", new[] { typeof(string) });
            if (method == null)
                throw new InvalidOperationException("RegistryKey.CreateSubKey(string) non trovato.");

            var sub = method.Invoke(key, new object[] { path });
            if (sub == null)
                throw new InvalidOperationException($"CreateSubKey ha restituito null per '{path}'.");

            return (IDisposable)sub;
        }

        private static void SetValue(object key, string name, object value)
        {
            var method = key.GetType().GetMethod("SetValue", new[] { typeof(string), typeof(object) });
            if (method == null)
                throw new InvalidOperationException("RegistryKey.SetValue(string, object) non trovato.");

            method.Invoke(key, new[] { name, value });
        }

        /// <summary>
        /// Checks command line args for a mtgaes:// URL.
        /// Returns the URL if found, null otherwise.
        /// </summary>
        public static string GetUrlFromArgs()
        {
            var args = Environment.GetCommandLineArgs();
            foreach (var arg in args)
            {
                if (arg.StartsWith("mtgaes://", StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.LogInfo($"Found URL in command line args: {arg}");
                    return arg;
                }
            }
            return null;
        }

        /// <summary>
        /// Parses a mtgaes://join/{challengeId}/{format} URL.
        /// Returns true if valid, with challengeId and format populated.
        /// </summary>
        public static bool ParseJoinUrl(string url, out string challengeId, out string format)
        {
            challengeId = null;
            format = null;

            if (string.IsNullOrEmpty(url))
                return false;

            try
            {
                // mtgaes://join/{challengeId}/{format}
                var uri = new Uri(url);
                if (uri.Scheme != Scheme)
                    return false;

                var path = uri.Host + uri.AbsolutePath; // "join/{id}/{format}"
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 3 && parts[0].Equals("join", StringComparison.OrdinalIgnoreCase))
                {
                    challengeId = parts[1];
                    format = parts[2].ToLowerInvariant();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Failed to parse URL '{url}': {ex.Message}");
            }

            return false;
        }
    }
}
