using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.Firebase;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.Helpers;
using UnityEngine;

namespace MTGAEnhancementSuite.Patches
{
    internal static class ChallengeCreatePatch
    {
        /// <summary>
        /// Cached PVPChallengeController instance — captured from Harmony __instance.
        /// Used by join request handler to call AddChallengeInvite/SendChallengeInvites.
        /// </summary>
        internal static object CachedController;
        internal static Type CachedControllerType;

        public static void Apply(Harmony harmony)
        {
            try
            {
                MethodInfo targetMethod = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == "PVPChallengeController")
                        {
                            targetMethod = AccessTools.Method(type, "CreateAndCacheChallenge");
                            if (targetMethod != null)
                            {
                                Plugin.Log.LogInfo($"Found PVPChallengeController.CreateAndCacheChallenge in {asm.GetName().Name}");
                                break;
                            }
                        }
                    }
                    if (targetMethod != null) break;
                }

                if (targetMethod == null)
                {
                    Plugin.Log.LogWarning("ChallengeCreatePatch: Could not find CreateAndCacheChallenge");
                    return;
                }

                var postfix = new HarmonyMethod(typeof(ChallengeCreatePatch), nameof(Postfix));
                harmony.Patch(targetMethod, postfix: postfix);
                Plugin.Log.LogInfo("ChallengeCreatePatch applied");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ChallengeCreatePatch.Apply failed: {ex}");
            }
        }

        static void Postfix(object __instance, object __result)
        {
            try
            {
                // Cache the PVPChallengeController instance for join request handling
                if (__instance != null)
                {
                    CachedController = __instance;
                    CachedControllerType = __instance.GetType();
                    PerPlayerLog.Info($"Cached PVPChallengeController instance: {CachedControllerType.Name}");
                }

                if (!ChallengeFormatState.HasFormat)
                    return;

                Plugin.Log.LogInfo("Challenge creation detected with format, will register lobby");
                FirebaseClient.Instance.StartCoroutine(WaitAndRegisterLobby());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ChallengeCreatePatch.Postfix failed: {ex}");
            }
        }

        private static IEnumerator WaitAndRegisterLobby()
        {
            yield return new WaitForSeconds(1f);
            DoRegisterLobby();
        }

        private static void DoRegisterLobby()
        {
            try
            {
                // PVPChallengeController is a plain class (NOT MonoBehaviour)
                // — FindObjectOfType won't work. Get it via the widget instead.

                // Get ChallengeId from UnifiedChallengeBladeWidget which IS a MonoBehaviour
                var widget = UnityEngine.Object.FindObjectOfType<UnifiedChallengeBladeWidget>();
                if (widget != null)
                {
                    var challengeIdField = AccessTools.Field(typeof(UnifiedChallengeBladeWidget), "CurrentChallengeId")
                        ?? AccessTools.Field(typeof(UnifiedChallengeBladeWidget).BaseType, "CurrentChallengeId");
                    if (challengeIdField != null)
                    {
                        var cid = (Guid)challengeIdField.GetValue(widget);
                        if (cid != Guid.Empty)
                        {
                            ChallengeFormatState.ActiveChallengeId = cid;
                            PerPlayerLog.Info($"Got ChallengeId from widget: {cid}");

                            // Now that the challenge actually exists, push the
                            // current mode's match settings (deck type +
                            // Bo3-default) to MTGA. The user typically picks
                            // a format BEFORE the challenge is created, so the
                            // spinner.onValueChanged listener was firing with
                            // ActiveChallengeId == Guid.Empty and skipping the
                            // SetGameSettings call entirely.
                            try { ChallengeSettingsPatch.PushMatchTypeForCurrentMode(); }
                            catch (Exception pmtEx)
                            {
                                Plugin.Log.LogWarning($"PushMatchType after challenge create failed: {pmtEx.Message}");
                            }

                            // Get player info from Pantry
                            var pantryType = GameReflection.TypeByName("Pantry");
                            var accountClientType = GameReflection.TypeByName("IAccountClient");
                            if (pantryType != null && accountClientType != null)
                            {
                                var getMethod = pantryType.GetMethod("Get").MakeGenericMethod(accountClientType);
                                var accountClient = getMethod.Invoke(null, null);
                                var accountInfoProp = AccessTools.Property(accountClientType, "AccountInformation");
                                var accountInfo = accountInfoProp.GetValue(accountClient);

                                var playerId = AccessTools.Field(accountInfo.GetType(), "PersonaID").GetValue(accountInfo) as string;
                                var playerName = AccessTools.Field(accountInfo.GetType(), "DisplayName").GetValue(accountInfo) as string;

                                Plugin.Log.LogInfo($"Registering lobby {cid} with format {ChallengeFormatState.SelectedFormat}");
                                PerPlayerLog.Info($"Registering lobby {cid} format={ChallengeFormatState.SelectedFormat}");

                                FirebaseClient.Instance.RegisterLobby(
                                    cid.ToString(),
                                    ChallengeFormatState.SelectedFormat,
                                    playerName, playerId, "DirectGame",
                                    false,
                                    success =>
                                    {
                                        if (success)
                                        {
                                            Plugin.Log.LogInfo("Lobby registered in Firebase (private)");
                                            PerPlayerLog.Info("Lobby registered in Firebase (private)");
                                            FirebaseClient.Instance.StartHeartbeat(cid.ToString());
                                        }
                                        else
                                        {
                                            Plugin.Log.LogWarning("Failed to register lobby in Firebase");
                                            PerPlayerLog.Warning("Failed to register lobby in Firebase");
                                        }
                                    }
                                );
                                return;
                            }
                        }
                    }
                }

                // Fallback — log failure
                Plugin.Log.LogWarning("DoRegisterLobby: Could not get challenge data from widget");
                PerPlayerLog.Warning("DoRegisterLobby: Could not get challenge data from widget");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DoRegisterLobby failed: {ex}");
            }
        }
    }
}
