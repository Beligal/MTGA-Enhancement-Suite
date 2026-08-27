using System;
using System.Collections;
using HarmonyLib;
using MTGAEnhancementSuite.Firebase;
using MTGAEnhancementSuite.UI;
using MTGAEnhancementSuite.Helpers;
using UnityEngine;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Hooks into MTGA's login flow to authenticate with Firebase
    /// when the player successfully logs into MTGA servers.
    /// Retries up to MaxRetries times, then stops and only retries
    /// when a user explicitly triggers a feature that needs auth.
    /// </summary>
    internal static class AuthPatch
    {
        private const int MaxRetries = 5;
        private const float RetryDelaySeconds = 5f;

        private static bool _authInProgress = false;
        private static bool _gaveUp = false;
        private static string _cachedPersonaId;
        private static string _cachedDisplayName;

        /// <summary>
        /// Whether auth has permanently failed and needs manual retry.
        /// </summary>
        public static bool HasGivenUp => _gaveUp;

        /// <summary>
        /// Called from PluginBehaviour to set up the auth listener.
        /// </summary>
        public static IEnumerator WaitForLoginAndAuth()
        {
            Plugin.Log.LogInfo("AuthPatch: Waiting for MTGA login...");

            // Wait for Pantry and IAccountClient
            object accountClient = null;
            Type accountClientType = null;

            while (accountClient == null)
            {
                yield return new WaitForSeconds(2f);
                accountClient = TryGetAccountClient(out accountClientType);
            }

            Plugin.Log.LogInfo("AuthPatch: IAccountClient available, waiting for login...");

            // Wait for FullyRegisteredLogin
            while (true)
            {
                var loginState = GetLoginState(accountClient, accountClientType);
                if (loginState == "FullyRegisteredLogin")
                    break;
                yield return new WaitForSeconds(2f);
            }

            // Cache identity info
            CacheIdentity(accountClient, accountClientType);

            if (string.IsNullOrEmpty(_cachedPersonaId))
            {
                Plugin.Log.LogError("AuthPatch: Could not read player identity");
                yield break;
            }

            // Attempt auth with retries
            yield return AttemptAuthWithRetries();
        }

        private static IEnumerator AttemptAuthWithRetries()
        {
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                Plugin.Log.LogInfo($"AuthPatch: Auth attempt {attempt}/{MaxRetries} for {_cachedDisplayName}");

                bool? result = null;
                _authInProgress = true;

                FirebaseClient.Instance.SignInWithMTGAIdentity(_cachedPersonaId, _cachedDisplayName,
                    success => { result = success; _authInProgress = false; });

                // Wait for result
                while (result == null)
                    yield return new WaitForSeconds(0.2f);

                if (result == true)
                {
                    Toast.Success($"Connected as {_cachedDisplayName}");
                    _gaveUp = false;
                    yield break;
                }

                if (attempt < MaxRetries)
                {
                    Plugin.Log.LogWarning($"AuthPatch: Attempt {attempt} failed, retrying in {RetryDelaySeconds}s...");
                    yield return new WaitForSeconds(RetryDelaySeconds);
                }
            }

            // All retries exhausted
            _gaveUp = true;
            Toast.Error("Could not connect to MTGA+ services after 5 attempts.");
            Plugin.Log.LogError("AuthPatch: All auth attempts failed");
        }

        /// <summary>
        /// Called when a feature needs auth but we've given up.
        /// Shows a toast and retries once.
        /// </summary>
        public static void RetryAuth(Action<bool> callback)
        {
            if (FirebaseClient.Instance.IsAuthenticated)
            {
                callback?.Invoke(true);
                return;
            }

            if (_authInProgress)
            {
                Toast.Info("Authentication in progress...");
                callback?.Invoke(false);
                return;
            }

            if (string.IsNullOrEmpty(_cachedPersonaId))
            {
                Toast.Error("No player identity available. Restart the game.");
                callback?.Invoke(false);
                return;
            }

            Toast.Info("Reconnecting to MTGA+ services...");
            _authInProgress = true;

            FirebaseClient.Instance.SignInWithMTGAIdentity(_cachedPersonaId, _cachedDisplayName,
                success =>
                {
                    _authInProgress = false;
                    if (success)
                    {
                        _gaveUp = false;
                        Toast.Success($"Connected as {_cachedDisplayName}");
                    }
                    else
                    {
                        Toast.Error("Reconnection failed. Try again later.");
                    }
                    callback?.Invoke(success);
                });
        }

        private static void CacheIdentity(object accountClient, Type accountClientType)
        {
            try
            {
                var accountInfoProp = AccessTools.Property(accountClientType, "AccountInformation");
                var accountInfo = accountInfoProp?.GetValue(accountClient);
                if (accountInfo == null) return;

                _cachedPersonaId = AccessTools.Field(accountInfo.GetType(), "PersonaID")
                    ?.GetValue(accountInfo) as string;
                _cachedDisplayName = AccessTools.Field(accountInfo.GetType(), "DisplayName")
                    ?.GetValue(accountInfo) as string;

                Plugin.Log.LogInfo($"AuthPatch: Identity cached: {_cachedDisplayName} ({_cachedPersonaId})");

                // Start a per-player log file so multiple instances don't overwrite each other
                try
                {
                    var safeName = _cachedDisplayName.Split('#')[0];
                    var logPath = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                        $"mtgaes_{safeName}.log");
                    PerPlayerLog.Init(logPath, _cachedDisplayName);
                }
                catch { }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"AuthPatch: Failed to cache identity: {ex}");
            }
        }

        private static object TryGetAccountClient(out Type accountClientType)
        {
            accountClientType = null;
            try
            {
                var pantryType = GameReflection.TypeByName("Pantry");
                if (pantryType == null) return null;

                accountClientType = GameReflection.TypeByName("IAccountClient");
                if (accountClientType == null) return null;

                var getMethod = pantryType.GetMethod("Get").MakeGenericMethod(accountClientType);
                return getMethod.Invoke(null, null);
            }
            catch
            {
                return null;
            }
        }

        private static string GetLoginState(object accountClient, Type accountClientType)
        {
            try
            {
                var stateProp = AccessTools.Property(accountClientType, "CurrentLoginState");
                return stateProp?.GetValue(accountClient)?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
