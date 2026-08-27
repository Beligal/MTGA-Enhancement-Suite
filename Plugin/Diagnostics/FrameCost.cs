using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MTGAEnhancementSuite.Diagnostics
{
    /// <summary>
    /// Quanto costa il nostro codice, frame per frame.
    ///
    /// Serve a rispondere a una domanda che a occhio non si risponde: se il
    /// client si impunta per un secondo, e' colpa nostra o di Arena? Ogni
    /// blocco misurato somma i propri millisecondi in un secchiello azzerato a
    /// ogni frame; quando <see cref="PluginBehaviour"/> vede un frame lungo,
    /// stampa il secchiello. Una riga che dice "frame da 1300 ms, di cui 0,00
    /// nostri" e' un'assoluzione, e vale quanto la riga opposta.
    ///
    /// Tutto gira sul thread principale di Unity (Update e patch Harmony sui
    /// metodi del client), quindi niente lock.
    /// </summary>
    internal static class FrameCost
    {
        private static readonly Dictionary<string, double> _current =
            new Dictionary<string, double>();

        /// <summary>
        /// Quante volte un blocco e' stato eseguito nel frame. Un totale di 45 ms
        /// vuol dire cose molto diverse se e' una chiamata lenta o quaranta
        /// chiamate rapide: nel primo caso si ottimizza il corpo, nel secondo si
        /// cerca chi lo chiama cosi' spesso.
        /// </summary>
        private static readonly Dictionary<string, int> _counts =
            new Dictionary<string, int>();

        /// <summary>Sotto questa soglia una voce non entra nel dettaglio: e' rumore.</summary>
        private const double DetailFloorMs = 0.05;

        /// <summary>
        /// <c>using (FrameCost.Measure("nome")) { ... }</c>. Scope e' una struct:
        /// nessuna allocazione, quindi si puo' mettere anche su un postfix che
        /// scatta 192 volte di fila senza inquinare la misura con la sua stessa
        /// spazzatura.
        /// </summary>
        public static Scope Measure(string name)
        {
            return new Scope(name);
        }

        public static void Add(string name, double ms)
        {
            double existing;
            _current[name] = _current.TryGetValue(name, out existing) ? existing + ms : ms;

            int count;
            _counts[name] = _counts.TryGetValue(name, out count) ? count + 1 : 1;
        }

        /// <summary>Totale del frame e dettaglio delle voci non trascurabili.</summary>
        public static double Total(out string detail)
        {
            double total = 0;
            var sb = new StringBuilder();
            foreach (var entry in _current)
            {
                total += entry.Value;
                if (entry.Value < DetailFloorMs) continue;

                int count;
                _counts.TryGetValue(entry.Key, out count);
                sb.Append(count > 1
                    ? $" {entry.Key}={entry.Value:F2}x{count}"
                    : $" {entry.Key}={entry.Value:F2}");
            }
            detail = sb.ToString();
            return total;
        }

        public static bool IsEmpty
        {
            get { return _current.Count == 0; }
        }

        public static void Clear()
        {
            _current.Clear();
            _counts.Clear();
        }

        internal struct Scope : IDisposable
        {
            private readonly string _name;
            private readonly long _started;

            public Scope(string name)
            {
                _name = name;
                _started = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                Add(_name, (Stopwatch.GetTimestamp() - _started) * 1000.0 / Stopwatch.Frequency);
            }
        }
    }
}
