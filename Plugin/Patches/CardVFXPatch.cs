using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.State;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Suppresses card-specific ETB (enter-the-battlefield) VFX, SFX, and custom
    /// spline paths while keeping the generic woosh/impact arrival sounds and
    /// default card movement trajectory.
    ///
    /// Three patches:
    /// 1. GenerateEtbSplineEvents postfix — strips card-specific VFX/SFX events,
    ///    keeps only the last 2 SplineEventAudio entries (generic woosh + impact).
    /// 2. GenerateEtbTriggerEvents prefix — skips triggered-ability cosmetic VFX.
    /// 3. GetLayoutSplinePath prefix — returns null to force the default movement
    ///    trajectory instead of a card-specific spline (fixes jumpy animations).
    /// </summary>
    internal static class CardVFXPatch
    {
        private static Type _splineEventAudioType;

        public static void Apply(Harmony harmony)
        {
            try
            {
                MethodInfo etbMethod = null;
                MethodInfo triggerMethod = null;
                MethodInfo splinePathMethod = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == "VfxProvider")
                        {
                            etbMethod = AccessTools.Method(t, "GenerateEtbSplineEvents");
                            triggerMethod = AccessTools.Method(t, "GenerateEtbTriggerEvents");
                        }
                        if (t.Name == "CardHolderBase")
                        {
                            splinePathMethod = AccessTools.Method(t, "GetLayoutSplinePath");
                        }
                        if (t.Name == "SplineEventAudio")
                        {
                            _splineEventAudioType = t;
                        }
                    }
                }

                if (etbMethod != null)
                {
                    harmony.Patch(etbMethod, postfix: new HarmonyMethod(typeof(CardVFXPatch), nameof(EtbPostfix)));
                    Plugin.Log.LogInfo("CardVFXPatch: GenerateEtbSplineEvents postfix applied");
                }
                else
                {
                    Plugin.Log.LogWarning("CardVFXPatch: Could not find VfxProvider.GenerateEtbSplineEvents");
                }

                if (triggerMethod != null)
                {
                    harmony.Patch(triggerMethod, prefix: new HarmonyMethod(typeof(CardVFXPatch), nameof(TriggerPrefix)));
                    Plugin.Log.LogInfo("CardVFXPatch: GenerateEtbTriggerEvents prefix applied");
                }

                if (splinePathMethod != null)
                {
                    harmony.Patch(splinePathMethod, postfix: new HarmonyMethod(typeof(CardVFXPatch), nameof(SplinePathPostfix)));
                    Plugin.Log.LogInfo("CardVFXPatch: GetLayoutSplinePath postfix applied");
                }
                else
                {
                    Plugin.Log.LogWarning("CardVFXPatch: Could not find CardHolderBase.GetLayoutSplinePath");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CardVFXPatch.Apply failed: {ex}");
            }
        }

        /// <summary>
        /// Postfix on GenerateEtbSplineEvents: strip all card-specific VFX/SFX,
        /// keep only SplineEventAudio entries (generic woosh + impact).
        /// </summary>
        static void EtbPostfix(ref object __result)
        {
            using var _fc = Diagnostics.FrameCost.Measure("CardVFX.Etb");
            try
            {
                if (!ModSettings.Instance.DisableCardVFX) return;
                if (__result == null) return;

                var enumerable = __result as System.Collections.IEnumerable;
                if (enumerable == null) return;

                var allEvents = new List<object>();
                foreach (var item in enumerable)
                    allEvents.Add(item);

                if (allEvents.Count < 2) return;

                // Keep only SplineEventAudio entries (generic woosh/impact)
                var audioEvents = new List<object>();
                if (_splineEventAudioType != null)
                {
                    foreach (var evt in allEvents)
                    {
                        if (_splineEventAudioType.IsInstanceOfType(evt))
                            audioEvents.Add(evt);
                    }
                }

                List<object> kept;
                if (audioEvents.Count >= 2)
                {
                    kept = audioEvents.Skip(audioEvents.Count - 2).ToList();
                }
                else
                {
                    kept = allEvents.Skip(allEvents.Count - 2).ToList();
                }

                // Build a new List<SplineEvent> via reflection
                var splineEventType = allEvents[0].GetType().BaseType;
                while (splineEventType != null && splineEventType.Name != "SplineEvent")
                    splineEventType = splineEventType.BaseType;
                if (splineEventType == null)
                    splineEventType = allEvents[0].GetType();

                var listType = typeof(List<>).MakeGenericType(splineEventType);
                var newList = Activator.CreateInstance(listType);
                var addMethod = listType.GetMethod("Add");
                foreach (var item in kept)
                    addMethod.Invoke(newList, new[] { item });

                __result = newList;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CardVFXPatch.EtbPostfix error: {ex}");
            }
        }

        /// <summary>
        /// Prefix on GenerateEtbTriggerEvents: skip entirely when disabled.
        /// </summary>
        static bool TriggerPrefix()
        {
            using var _fc = Diagnostics.FrameCost.Measure("CardVFX.Trigger");
            try
            {
                if (ModSettings.Instance.DisableCardVFX)
                    return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CardVFXPatch.TriggerPrefix error: {ex}");
            }
            return true;
        }

        /// <summary>
        /// Postfix on CardHolderBase.GetLayoutSplinePath: when the destination is
        /// the battlefield, replace the card-specific spline with null so the
        /// movement system uses the default trajectory. Preserves card draw and
        /// other zone transition splines.
        /// </summary>
        static void SplinePathPostfix(object __instance, ref string __result)
        {
            using var _fc = Diagnostics.FrameCost.Measure("CardVFX.SplinePath");
            try
            {
                if (!ModSettings.Instance.DisableCardVFX) return;
                if (string.IsNullOrEmpty(__result)) return; // already default

                // Only suppress for battlefield destinations
                var cardHolderTypeProp = AccessTools.Property(__instance.GetType(), "CardHolderType");
                if (cardHolderTypeProp != null)
                {
                    var holderType = cardHolderTypeProp.GetValue(__instance);
                    // CardHolderType.Battlefield = 4 (Invalid=0, Library=1, OffCameraLibrary=2, Hand=3, Battlefield=4)
                    if (holderType != null && (int)holderType == 4)
                    {
                        __result = null; // force default trajectory for ETBs
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CardVFXPatch.SplinePathPostfix error: {ex}");
            }
        }
    }
}
