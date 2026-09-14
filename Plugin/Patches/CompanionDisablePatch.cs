using System;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.State;
using UnityEngine;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Suppresses companion (pet) rendering and sounds by letting CompanionBuilder.Create
    /// run normally, then hiding all renderers and muting the AccessoryController.
    /// The companion object stays alive so dependent UI (opponent card counts etc.) still works.
    /// </summary>
    internal static class CompanionDisablePatch
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                MethodInfo targetMethod = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == "CompanionBuilder")
                        {
                            targetMethod = AccessTools.Method(t, "Create");
                            break;
                        }
                    }
                    if (targetMethod != null) break;
                }

                if (targetMethod == null)
                {
                    Plugin.Log.LogWarning("CompanionDisablePatch: Could not find CompanionBuilder.Create");
                    return;
                }

                var postfix = new HarmonyMethod(typeof(CompanionDisablePatch), nameof(Postfix));
                harmony.Patch(targetMethod, postfix: postfix);
                Plugin.Log.LogInfo("CompanionDisablePatch applied");

                // Also patch ZoneCountView.SetVisibility to always show zone counts
                // when companions are disabled (pet collider blocks hover events)
                MethodInfo setVisMethod = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == "ZoneCountView")
                        {
                            setVisMethod = AccessTools.Method(t, "SetVisibility");
                            break;
                        }
                    }
                    if (setVisMethod != null) break;
                }

                if (setVisMethod != null)
                {
                    var visPrefix = new HarmonyMethod(typeof(CompanionDisablePatch), nameof(ZoneCountVisibilityPrefix));
                    harmony.Patch(setVisMethod, prefix: visPrefix);
                    Plugin.Log.LogInfo("CompanionDisablePatch: ZoneCountView.SetVisibility patch applied");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CompanionDisablePatch.Apply failed: {ex}");
            }
        }

        /// <summary>
        /// When companions are disabled, force zone counts to always be visible.
        /// </summary>
        static bool ZoneCountVisibilityPrefix(ref bool visible)
        {
            using var _fc = Diagnostics.FrameCost.Measure("Companions.ZoneVisibility");
            try
            {
                if (ModSettings.Instance.DisableCompanions)
                {
                    visible = true; // always visible
                }
            }
            catch { }
            return true; // run original with modified parameter
        }

        static void Postfix(ref object __result)
        {
            using var _fc = Diagnostics.FrameCost.Measure("Companions.Create");
            try
            {
                if (!ModSettings.Instance.DisableCompanions) return;
                if (__result == null) return;

                var accessoryController = __result as MonoBehaviour;
                if (accessoryController == null) return;

                var go = accessoryController.gameObject;

                // Set _playerMuted = true via reflection — this makes the _muted property
                // return true, which guards all sound/animation code paths in AccessoryController.
                // Unlike SetGlobalMuted, this doesn't affect the emote system.
                var playerMutedField = AccessTools.Field(accessoryController.GetType(), "_playerMuted");
                if (playerMutedField != null)
                {
                    playerMutedField.SetValue(accessoryController, true);
                    PerPlayerLog.Info("CompanionDisablePatch: Set _playerMuted=true on companion");
                }

                // Also invoke _muteEvent to notify prefab-wired listeners (audio controllers etc.)
                var muteEventField = AccessTools.Field(accessoryController.GetType(), "_muteEvent");
                if (muteEventField != null)
                {
                    var muteEvent = muteEventField.GetValue(accessoryController) as UnityEngine.Events.UnityEvent;
                    muteEvent?.Invoke();
                    PerPlayerLog.Info("CompanionDisablePatch: Invoked _muteEvent on companion");
                }

                // Hide all renderers (mesh, skinned mesh, particle systems)
                foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.enabled = false;
                }

                // Disable all particle systems
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    var emission = ps.emission;
                    emission.enabled = false;
                }

                // Mute all audio sources
                foreach (var audio in go.GetComponentsInChildren<AudioSource>(true))
                {
                    audio.mute = true;
                    audio.volume = 0f;
                }

                // Disable all animators (stops animation CPU cost)
                foreach (var animator in go.GetComponentsInChildren<Animator>(true))
                {
                    animator.enabled = false;
                }

                PerPlayerLog.Info("CompanionDisablePatch: Hidden companion renderers, particles, audio, and animators");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CompanionDisablePatch.Postfix error: {ex}");
            }
        }
    }
}
