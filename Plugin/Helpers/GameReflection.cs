using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MTGAEnhancementSuite.Helpers
{
    /// <summary>
    /// Shared reflection helpers for two things the rest of the codebase keeps
    /// re-implementing inline:
    ///   - <see cref="PantryGet{T}"/>: resolve a service from MTGA's Pantry IoC
    ///     container. Pantry lives in the global-ish `Wizards.Mtga` namespace and
    ///     there are two `Pantry` types in the loaded assemblies, so we resolve it
    ///     by simple name via AccessTools (same approach the existing patches use).
    ///   - <see cref="AwaitPromise"/>: poll a <c>Wizards.Arena.Promises.Promise&lt;T&gt;</c>
    ///     to completion WITHOUT referencing that assembly at compile time. The
    ///     Promise generic lives in an unreferenced DLL, so any method returning
    ///     one (CreateDeck / DeleteDeck / GetFullDeck / UpdateDeck) must be invoked
    ///     reflectively and its result polled via IsDone/Successful/Result.
    /// </summary>
    internal static class GameReflection
    {
        private static Type _pantryType;
        private static MethodInfo _pantryGetOpen;
        private static readonly Dictionary<string, Type> _typeCache =
            new Dictionary<string, Type>();

        /// <summary>
        /// <c>AccessTools.TypeByName</c> con memoria.
        ///
        /// Quello di Harmony, quando il nome non si risolve al primo colpo,
        /// enumera tutti i tipi di tutte le assembly caricate — e lo fa due
        /// volte, prima confrontando FullName (che per un nome senza namespace
        /// non combacia mai) e poi Name. Con le assembly di Arena sono ~200 ms
        /// a chiamata: misurati 213 ms su DeckListReconcile, che scattava a
        /// ogni apertura della lista mazzi.
        ///
        /// Mettiamo in cache solo i successi. Un tipo non trovato puo' vivere in
        /// un'assembly non ancora caricata: memorizzare il fallimento lo
        /// renderebbe introvabile per sempre, e sarebbe un bug ben peggiore di
        /// una scansione ripetuta in un caso che gia' non funziona.
        /// </summary>
        public static Type TypeByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            Type cached;
            if (_typeCache.TryGetValue(name, out cached)) return cached;

            var type = AccessTools.TypeByName(name);
            if (type != null) _typeCache[name] = type;
            return type;
        }

        /// <summary>
        /// Equivalent to <c>Pantry.Get&lt;T&gt;()</c>. Returns null if Pantry isn't
        /// available yet or the service isn't registered.
        /// </summary>
        public static T PantryGet<T>() where T : class
        {
            try
            {
                if (_pantryType == null)
                {
                    _pantryType = AccessTools.TypeByName("Wizards.Mtga.Pantry")
                                  ?? AccessTools.TypeByName("Pantry");
                    if (_pantryType == null) return null;
                    _pantryGetOpen = AccessTools.Method(_pantryType, "Get");
                    if (_pantryGetOpen == null) return null;
                }
                // MakeGenericMethod alloca e costa: farlo a ogni chiamata
                // andava bene quando PantryGet si usava a mano, non quando lo
                // chiama un Tick. La classe generica annidata da' una cache
                // per tipo senza nemmeno una lookup in dizionario.
                var generic = PantryGetCache<T>.Closed
                    ?? (PantryGetCache<T>.Closed = _pantryGetOpen.MakeGenericMethod(typeof(T)));
                return generic.Invoke(null, null) as T;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"PantryGet<{typeof(T).Name}> failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Una casella per tipo: il CLR ne crea una copia per ogni T, quindi
        /// leggere <c>PantryGetCache&lt;T&gt;.Closed</c> e' un accesso a campo
        /// statico e basta.
        /// </summary>
        private static class PantryGetCache<T> where T : class
        {
            public static MethodInfo Closed;
        }

        /// <summary>
        /// Polls a reflected Promise object to completion, then invokes
        /// <paramref name="onDone"/> with (successful, result). <paramref name="result"/>
        /// is the Promise's Result property value (or null). Safe to yield in a
        /// coroutine. Caps the wait at <paramref name="timeoutSeconds"/> so a
        /// stuck server call can't hang the UI forever.
        ///
        /// Uses unscaled time so the F7 fast-forward (Time.timeScale) doesn't
        /// distort the timeout.
        /// </summary>
        public static IEnumerator AwaitPromise(object promise, Action<bool, object> onDone, float timeoutSeconds = 30f)
        {
            if (promise == null) { onDone?.Invoke(false, null); yield break; }

            var t = promise.GetType();
            var isDoneProp = AccessTools.Property(t, "IsDone");
            var successfulProp = AccessTools.Property(t, "Successful");
            var resultProp = AccessTools.Property(t, "Result");

            float elapsed = 0f;
            while (isDoneProp != null)
            {
                bool done = false;
                try { done = (bool)isDoneProp.GetValue(promise); }
                catch { done = true; } // if we can't read it, stop waiting
                if (done) break;
                if (elapsed >= timeoutSeconds) break;
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }

            bool ok = false;
            object res = null;
            try
            {
                ok = successfulProp != null && (bool)successfulProp.GetValue(promise);
                if (ok && resultProp != null) res = resultProp.GetValue(promise);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"AwaitPromise: reading result failed: {ex.Message}");
                ok = false;
            }
            onDone?.Invoke(ok, res);
        }
    }
}
