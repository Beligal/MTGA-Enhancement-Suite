using System;
using System.Reflection;
using GreClient.CardData;
using HarmonyLib;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.State;
using Wotc.Mtga.Cards.Database;
using Wotc.Mtga.DuelScene;
using Wotc.Mtga.DuelScene.Examine;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Forza la vista semplificata delle carte: niente trattamenti grafici
    /// speciali (showcase, borderless, alt-art), illustrazione conservata.
    ///
    /// Non reimplementiamo nulla: Arena ha gia' questo meccanismo e lo applica
    /// a certe carte tramite una tabella di configurazione. Noi rispondiamo
    /// sempre di si.
    ///
    /// Due agganci, perche' i due contesti passano da strade diverse:
    ///
    /// A) CARTE IN PARTITA — <c>DuelScene_CDC.CheckSimplifyOverride</c>
    ///    Popola <c>SimplifiedOverride</c>, che <c>VisualModel</c> antepone al
    ///    modello reale. Normalmente lo fa solo se l asset lookup tree lo
    ///    richiede; col toggle attivo lo impostiamo comunque, chiamando la
    ///    stessa <c>CardSimplifier.Simplify</c> con lo stesso contesto
    ///    (<c>ModelOverride</c>, <c>keepArtId: true</c>).
    ///
    /// B) FINESTRA DI ESAME — <c>ExamineViewCardHolder.SetExamineState</c>
    ///    Riscriviamo il solo stato <c>Styled</c> in <c>Unstyled</c>. Gli altri
    ///    stati (Printing, Instance, Specialize...) restano intatti, quindi il
    ///    pulsante "view printing" continua a funzionare. Le carte senza stile
    ///    usano <c>Instance</c> e non vengono toccate.
    ///
    /// Il toggle e' letto a ogni invocazione: spegnendolo si torna al
    /// comportamento originale senza riavviare.
    ///
    /// LIMITE NOTO (2026-09-02): le terre full art a volte restano senza bordo.
    /// La semplificazione su quelle non attecchisce sempre. Non e' una
    /// corruzione — la carta e' giusta, l'illustrazione c'e', manca solo la
    /// cornice — e per ora e' accettato com'e'. Se un giorno da' fastidio, il
    /// punto da guardare e' ClearRecord/GetArtSize dentro CardSimplifier, che
    /// sono quelli che decidono la cornice a partire dalla dimensione dell'arte.
    /// </summary>
    internal static class ForceSimplifiedPatch
    {
        private static PropertyInfo _simplifiedOverrideProp;
        private static bool _warnedFaceMismatch;
        private static bool _warnedStaleOverride;

        public static void Apply(Harmony harmony)
        {
            // --- A) carte in partita ---
            try
            {
                var target = AccessTools.Method(typeof(DuelScene_CDC), "CheckSimplifyOverride");
                if (target == null)
                {
                    Plugin.Log.LogWarning("ForceSimplified: DuelScene_CDC.CheckSimplifyOverride non trovato, patch A saltata.");
                }
                else
                {
                    _simplifiedOverrideProp = AccessTools.Property(typeof(DuelScene_CDC), "SimplifiedOverride");
                    if (_simplifiedOverrideProp == null || _simplifiedOverrideProp.GetSetMethod(true) == null)
                    {
                        Plugin.Log.LogWarning("ForceSimplified: SimplifiedOverride non scrivibile, patch A saltata.");
                    }
                    else
                    {
                        harmony.Patch(target, postfix: new HarmonyMethod(
                            typeof(ForceSimplifiedPatch), nameof(CheckSimplifyOverride_Postfix)));
                        Plugin.Log.LogInfo("ForceSimplified: patch A (carte in partita) applicata.");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ForceSimplified: patch A fallita: {ex.Message}");
            }

            // --- A2) pulizia dell override sul riciclo ---
            //
            // Teardown() riporta il CDC nel pool e azzera PreviousGrpId e
            // RevealOverride, ma NON SimplifiedOverride: in Arena e' innocuo
            // perche' quell override e' quasi sempre nullo. Con la nostra patch
            // e' sempre valorizzato, quindi sopravvive al riciclo e la carta
            // successiva — magari coperta — eredita l identita' della
            // precedente. E' la causa delle mani avversarie "rivelate".
            try
            {
                var teardown = AccessTools.Method(typeof(DuelScene_CDC), "Teardown");
                if (teardown != null)
                {
                    harmony.Patch(teardown, postfix: new HarmonyMethod(
                        typeof(ForceSimplifiedPatch), nameof(ClearOverride_Postfix)));
                    Plugin.Log.LogInfo("ForceSimplified: patch A2 (pulizia su Teardown) applicata.");
                }
                var setModel = AccessTools.Method(typeof(DuelScene_CDC), "SetModel");
                if (setModel != null)
                {
                    harmony.Patch(setModel, postfix: new HarmonyMethod(
                        typeof(ForceSimplifiedPatch), nameof(ClearOverride_Postfix)));
                    Plugin.Log.LogInfo("ForceSimplified: patch A3 (pulizia su SetModel) applicata.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ForceSimplified: patch di pulizia fallita: {ex.Message}");
            }

            // --- A4) rete di sicurezza in lettura ---
            try
            {
                var visual = AccessTools.PropertyGetter(typeof(DuelScene_CDC), "VisualModel");
                if (visual != null)
                {
                    harmony.Patch(visual, postfix: new HarmonyMethod(
                        typeof(ForceSimplifiedPatch), nameof(VisualModel_Postfix)));
                    Plugin.Log.LogInfo("ForceSimplified: patch A4 (validazione VisualModel) applicata.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ForceSimplified: validazione VisualModel fallita: {ex.Message}");
            }

            // --- B) finestra di esame ---
            try
            {
                var target = AccessTools.Method(typeof(ExamineViewCardHolder), "SetExamineState");
                if (target == null)
                {
                    Plugin.Log.LogWarning("ForceSimplified: ExamineViewCardHolder.SetExamineState non trovato, patch B saltata.");
                }
                else
                {
                    harmony.Patch(target, prefix: new HarmonyMethod(
                        typeof(ForceSimplifiedPatch), nameof(SetExamineState_Prefix)));
                    Plugin.Log.LogInfo("ForceSimplified: patch B (finestra di esame) applicata.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ForceSimplified: patch B fallita: {ex.Message}");
            }
        }

        /// <summary>
        /// A) Dopo il calcolo originale: se il toggle e' attivo e Arena non ha
        /// gia' deciso di semplificare, lo decidiamo noi.
        /// </summary>
        private static void CheckSimplifyOverride_Postfix(DuelScene_CDC __instance, ICardDataAdapter model)
        {
            try
            {
                if (!ModSettings.Instance.ForceSimplifiedCards) return;
                if (__instance == null || model == null) return;

                // Arena ha gia' impostato l override per questa carta: non
                // ricalcoliamo, il risultato sarebbe lo stesso.
                if (__instance.SimplifiedOverride != null) return;

                // GUARDIA 1 — mai semplificare una carta mostrata coperta.
                //
                // IsDisplayedFaceDown dipende, fra le altre cose, da
                // "_instance.GrpId > 11" e dalla lista Viewers. Le carte
                // nascoste (dorso, Obscured) hanno un GrpId basso; Simplify
                // pero' ricostruisce il record di stampa a partire dal GrpId e
                // restituisce una carta con identita' reale, ribaltando il
                // flag. Risultato: la mano dell avversario veniva disegnata
                // scoperta. Arena stessa rifiuta la vista semplificata sulle
                // carte coperte (ViewSimplifiedButton.ShouldShowButton).
                if (model.IsDisplayedFaceDown) return;

                var inst = model.Instance;
                if (inst != null && inst.FaceDownState != null &&
                    (inst.FaceDownState.IsFaceDown || inst.FaceDownState.IsCopiedFaceDown)) return;

                // GUARDIA 3 — mai semplificare un modello senza illustrazione.
                //
                // Nei dialoghi a scelta multipla ("Choose One": carte modali,
                // planeswalker, terre a doppio uso) i riquadri non sono carte ma
                // rappresentazioni di abilita'. Misurati in gioco il 2026-09-02,
                // dentro un 'BrowserCardHolder_Default':
                //
                //     grpId=188913  artId=0  skin='DA'  abilita'=0  tipi=<vuoto>
                //
                // contro una carta vera nella stessa partita:
                //
                //     grpId=95716   artId=449709  skin='DA'  abilita'=3  tipi=Enchantment
                //
                // Il marcatore e' l'ILLUSTRAZIONE, non l'identificatore: il grpId
                // c'e' ed e' alto, ma artId e' 0 e la lista dei tipi e' vuota.
                //
                // Perche' si rompono: Simplify azzera lo skin code ('DA' -> ''), che
                // su questi oggetti e' cio' che li fa disegnare. Il risultato e' il
                // segnaposto col triangolo.
                //
                // La regola e' anche quella giusta di principio: questa funzione
                // promette di togliere lo stile *conservando l'illustrazione*. Se
                // illustrazione non ce n'e', non c'e' niente da conservare e niente
                // da semplificare. Nel peggiore dei casi qualche oggetto anomalo
                // resta non semplificato — un inconveniente, non un danno.
                //
                // Le guardie 1 e 2 non lo intercettavano: controllano la faccia
                // coperta, e questi non sono coperti.
                //
                // Terzo contesto in cui questa patch colpisce piu' largamente di
                // quanto faccia Arena (vedi MISTAKES.md #10).
                if (model.GrpId == 0) return;
                if (model.Printing == null || model.Printing.ArtId == 0) return;

                var db = GameReflection.PantryGet<CardDatabase>();
                if (db == null) return;

                var simplified = CardSimplifier.Simplify(
                    CardSimplifier.Context.ModelOverride,
                    model,
                    db.CardDataProvider,
                    db.AbilityDataProvider,
                    keepArtId: true);

                if (simplified == null || ReferenceEquals(simplified, model)) return;

                // GUARDIA 2 — invariante di sicurezza.
                //
                // Se semplificare cambia la faccia mostrata, la sostituzione
                // rivelerebbe (o nasconderebbe) informazione: si rinuncia. Vale
                // anche per cause che non abbiamo previsto, non solo per quella
                // sopra. Uno stile non semplificato e' un inconveniente; una
                // carta rivelata per errore e' un bug grave.
                if (simplified.IsDisplayedFaceDown != model.IsDisplayedFaceDown)
                {
                    if (!_warnedFaceMismatch)
                    {
                        _warnedFaceMismatch = true;
                        Plugin.Log.LogWarning(
                            "ForceSimplified: semplificazione scartata, avrebbe cambiato la faccia mostrata " +
                            $"(grpId={model.GrpId}). Segnalato una sola volta per sessione.");
                    }
                    return;
                }

                _simplifiedOverrideProp.SetValue(__instance, simplified, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ForceSimplified (partita): {ex.Message}");
            }
        }

        /// <summary>
        /// A2/A3) Azzera l override quando il CDC torna nel pool o cambia carta.
        /// Se i visual verranno aggiornati, CheckSimplifyOverride lo ricalcola
        /// subito dopo; altrimenti resta nullo e VisualModel ricade sul modello
        /// reale, che e' esattamente il comportamento originale di Arena.
        /// </summary>
        private static void ClearOverride_Postfix(DuelScene_CDC __instance)
        {
            try
            {
                if (__instance == null || _simplifiedOverrideProp == null) return;
                if (__instance.SimplifiedOverride != null)
                    _simplifiedOverrideProp.SetValue(__instance, null, null);
            }
            catch { /* pulizia best-effort: non deve mai far fallire il chiamante */ }
        }

        /// <summary>
        /// A4) Ultima difesa, al momento del disegno: se l override in uso non
        /// corrisponde alla carta attualmente assegnata, lo scartiamo e
        /// restituiamo il modello reale.
        ///
        /// Le due pulizie sopra dovrebbero bastare, ma il costo qui e' un
        /// confronto fra due interi e la modalita' di guasto e' mostrare
        /// l identita' sbagliata di una carta coperta. Vale la ridondanza.
        /// </summary>
        private static void VisualModel_Postfix(DuelScene_CDC __instance, ref ICardDataAdapter __result)
        {
            try
            {
                if (__instance == null || __result == null) return;
                var ov = __instance.SimplifiedOverride;
                if (ov == null || !ReferenceEquals(__result, ov)) return;

                var real = __instance.Model;
                if (real == null) return;
                if (ov.GrpId == real.GrpId) return;   // coerente: nulla da fare

                if (!_warnedStaleOverride)
                {
                    _warnedStaleOverride = true;
                    Plugin.Log.LogWarning(
                        $"ForceSimplified: override obsoleto scartato (override grpId={ov.GrpId}, " +
                        $"carta grpId={real.GrpId}). Segnalato una sola volta per sessione.");
                }
                __result = real;
            }
            catch { /* in caso di dubbio si mostra il modello reale */ }
        }

        /// <summary>
        /// B) Prima dell applicazione: lo stato "stilizzato" diventa "senza
        /// stile". Tutti gli altri stati passano invariati.
        /// </summary>
        private static void SetExamineState_Prefix(ref ExamineState state)
        {
            try
            {
                if (!ModSettings.Instance.ForceSimplifiedCards) return;
                if (state == ExamineState.Styled) state = ExamineState.Unstyled;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ForceSimplified (esame): {ex.Message}");
            }
        }
    }
}
