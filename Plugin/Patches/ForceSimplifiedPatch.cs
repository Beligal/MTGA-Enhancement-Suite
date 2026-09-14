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
    /// C) CORNICE DELLE TERRE — <c>CardSimplifier.ClearRecord</c> (patch A5).
    ///    La ripulitura del record butta via il dettaglio di cornice, e per le
    ///    terre quello e' l'unico posto dove vive il colore: uscivano grigie.
    ///    Ne ricostruiamo uno di sola tinta. Il ragionamento per esteso sta sul
    ///    metodo <c>ClearRecord_Postfix</c>.
    ///
    /// LIMITE NOTO (2026-09-02, ancora aperto): le terre full art a volte
    /// restano senza bordo. Non e' una corruzione — la carta e' giusta,
    /// l'illustrazione c'e', manca solo la cornice. Cugino del bug delle terre
    /// grigie ma causa diversa: li' era <c>RawFrameDetail</c>, qui e'
    /// <c>GetArtSize</c>, che riporta l'arte a <c>Normal</c>. Non toccato:
    /// normalizzare la dimensione dell'arte e' esattamente cio' che la funzione
    /// deve fare, e la cornice mancante e' il prezzo.
    /// </summary>
    internal static class ForceSimplifiedPatch
    {
        private static PropertyInfo _simplifiedOverrideProp;
        private static bool _warnedFaceMismatch;
        private static bool _warnedStaleOverride;
        private static bool _warnedLandFrame;

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

            // --- A5) le terre si tengono la loro cornice ---
            try
            {
                var clearRecord = AccessTools.Method(typeof(CardSimplifier), "ClearRecord");
                if (clearRecord == null)
                {
                    Plugin.Log.LogWarning("ForceSimplified: CardSimplifier.ClearRecord non trovato, patch A5 saltata. Le terre resteranno grigie.");
                }
                else
                {
                    harmony.Patch(clearRecord, postfix: new HarmonyMethod(
                        typeof(ForceSimplifiedPatch), nameof(ClearRecord_Postfix)));
                    Plugin.Log.LogInfo("ForceSimplified: patch A5 (cornice delle terre) applicata.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ForceSimplified: patch A5 fallita: {ex.Message}");
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
            using var _fc = Diagnostics.FrameCost.Measure("ForceSimplified.CheckOverride");
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

                // Nessuna guardia sulle terre: si semplificano come le
                // altre carte. Il grigio delle terre "gold" non nasceva qui ma
                // nella ripulitura del record — vedi la patch A5.

                var db = GameReflection.PantryGet<CardDatabase>();
                if (db == null) return;

                // GUARDIA 5 — carte che il database non conosce.
                //
                // IL SINTOMO: nel pannello Scry/Surveil la carta mostrata a
                // destra — la terra che ha innescato l'abilita' — aveva
                // l'illustrazione giusta e la cornice grigia.
                //
                // LA CAUSA: quella non e' la terra, e' l'**abilita' sullo
                // stack**, che e' una carta con un grpId generato a runtime
                // (nel log: 121445, mentre il database delle carte si ferma a
                // 108028). Simplify fa
                // GetCardRecordById(grpId, "") per ricostruire la stampa, e per
                // un id sconosciuto quel metodo restituisce
                // CardPrintingRecord.Blank: un record con GrpId 0, senza tipi,
                // senza colori, senza cornice. Simplify ci costruisce sopra
                // tutta la carta. Sopravvive solo l'illustrazione, perche'
                // passiamo keepArtId: true — ed e' esattamente il sintomo.
                //
                // Nessuna riparazione a valle poteva funzionare: A5 e' gated su
                // IsLand, e sull'abilita' IsLand e' falso proprio perche' il
                // record e' vuoto. La cura e' non semplificare affatto una
                // carta che il database non sa cos'e'.
                //
                // Stessa famiglia di MISTAKES.md #10: applicare una
                // trasformazione a ogni carta senza controllare che per quella
                // carta abbia senso.
                var conosciuta = db.CardDataProvider.GetCardRecordById(model.GrpId, string.Empty);
                if (conosciuta.GrpId == 0)
                {
                    if (_unknownDiagSeen.Count <= 24 && _unknownDiagSeen.Add(model.GrpId))
                        Plugin.Log.LogInfo(
                            $"ForceSimplified/guardia5: carta sconosciuta al database, non semplificata. " +
                            $"grpId={model.GrpId} dove='{HolderPath(__instance)}'");
                    return;
                }

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
        /// A5) Rimette alle TERRE i dettagli di cornice che la semplificazione
        /// butta via.
        ///
        /// IL BUG — un Tempio (terra bicolore) risultava grigio, senza colori.
        /// Dentro <c>CardSimplifier.ClearRecord</c>:
        ///
        /// <code>
        /// string rawFrameDetail = (baseRecord.RawFrameDetail.Contains("hybrid")
        ///                          ? "hybrid" : string.Empty);
        /// </code>
        ///
        /// cioe' del dettaglio di cornice sopravvive **solo** la parola
        /// "hybrid". Leggendo il database delle carte di Arena
        /// (<c>Raw_CardDatabase_*.mtga</c>, e' SQLite):
        ///
        ///   Temple of Enlightenment, stampa THB: RawFrameDetail = "WU land"
        ///   Temple of Enlightenment, stampa FDN: AdditionalFrameDetails = "hybrid"
        ///
        /// La seconda sopravvive, la prima viene azzerata — ed e' per questo che
        /// la stessa carta si comportava in due modi secondo la stampa.
        ///
        /// PERCHE' PROPRIO LE TERRE — la cornice viene scelta dall'asset lookup
        /// tree, che valuta <c>Printing.RawFrameDetails</c> e
        /// <c>AdditionalFrameDetails</c> (vedi gli evaluator
        /// <c>CardData_RawFrameDetails</c> e
        /// <c>CardData_Printing_AdditionalFrameDetails</c> in Core). Per una
        /// magia il colore si recupera comunque da <c>Colors</c>; per una terra
        /// <c>Colors</c> e' **vuoto**, quindi persa quella stringa non resta
        /// niente e l'albero ripiega sulla cornice neutra. E' la risposta alla
        /// domanda "perche' Arena l'ha scritta cosi'": la sua semplificazione e'
        /// pensata per carte il cui colore sopravvive altrove.
        ///
        /// DOVE STA DAVVERO IL COLORE — non in <c>RawFrameDetail</c>. Sulle
        /// terre moderne quel campo e' vuoto gia' nel database: Rumble Arena e
        /// Hidden Grotto hanno <c>RawFrameDetail = ""</c> e
        /// <c>AdditionalFrameDetails = "gold"</c>. Il colore sta li'.
        ///
        /// E li' <c>ClearRecord</c> tiene solo "hybrid": "gold" lo butta. Da
        /// cui il difetto — una terra a due colori (hybrid) restava giusta, una
        /// a tre o piu' (gold) perdeva ogni dato di cornice e usciva grigia.
        /// Questo spiega anche perche' sembrava dipendere dal posto: in mano
        /// c'erano terre hybrid, nel pannello Scry una gold.
        ///
        /// LE DUE FAMIGLIE — le terre tengono il colore in due posti diversi,
        /// e la ripulitura ne azzera uno per ciascuna:
        ///
        ///   Seachrome Coast   RawFrameDetail = "WU land"   Additional = ""
        ///   Sinister Hideout  RawFrameDetail = ""          Additional = "hybrid"
        ///   Hidden Grotto     RawFrameDetail = ""          Additional = "gold"
        ///
        /// Per questo rimettiamo **entrambi** i campi, non uno solo: negli
        /// Additional le sole parole di tinta ("gold", "hybrid" — tutto il
        /// resto e' stile e resta tolto), e nel grezzo una forma corta
        /// ricostruita da <c>FrameColors</c>.
        ///
        /// Tenerli insieme non e' una forzatura nostra: nel database di Arena
        /// 209 terre hanno entrambi i campi valorizzati, e la coppia
        /// <c>"UB land"</c> + <c>"hybrid"</c> compare tal quale sei volte.
        /// [VERIFIED]
        ///
        /// Gli altri tipi di carta non vengono toccati: per loro il colore
        /// sopravvive in <c>Colors</c> e azzerare e' giusto. Lo stile delle
        /// terre illustrate sta in <c>ArtSize</c>, che <c>ClearRecord</c>
        /// continua a normalizzare: quello lo semplifichiamo ancora.
        /// </summary>
        private static void ClearRecord_Postfix(CardPrintingRecord baseRecord, ref CardPrintingRecord __result)
        {
            using var _fc = Diagnostics.FrameCost.Measure("ForceSimplified.LandFrame");
            try
            {
                if (!ModSettings.Instance.ForceSimplifiedCards) return;
                // CardPrintingRecord e' una struct: niente controlli contro null.
                if (!IsLand(baseRecord)) return;

                // I due campi vanno rimessi ENTRAMBI, perche' le terre si
                // dividono in due famiglie e Arena ne azzera uno per ciascuna.
                var record = __result;
                var fatto = string.Empty;

                // 1) La tinta negli Additional: Arena tiene "hybrid" e butta
                //    "gold". Se non e' rimasto niente, la rimettiamo noi.
                if (record.AdditionalFrameDetails == null || record.AdditionalFrameDetails.Count == 0)
                {
                    var tinta = ColourFrameDetails(baseRecord);
                    if (tinta != null)
                    {
                        record = new CardPrintingRecord(record, additionalFrameDetails: tinta);
                        fatto += "additional=" + string.Join(",", tinta) + " ";
                    }
                }

                // 2) Il dettaglio grezzo, che la ripulitura azzera sempre (a
                //    meno che contenesse "hybrid"). E' l'unico posto dove le
                //    terre piu' vecchie tengono il colore.
                if (string.IsNullOrEmpty(record.RawFrameDetail))
                {
                    var cornice = LandFrameDetail(record);
                    if (cornice != null)
                    {
                        record = new CardPrintingRecord(record, rawFrameDetail: cornice);
                        fatto += "raw=" + cornice;
                    }
                }

                if (fatto.Length == 0) return;

                __result = record;
            }
            catch (Exception ex)
            {
                // Nel dubbio si tiene il record semplificato di Arena: una terra
                // grigia e' un difetto estetico, un'eccezione qui romperebbe il
                // disegno di ogni carta.
                if (!_warnedLandFrame)
                {
                    _warnedLandFrame = true;
                    Plugin.Log.LogWarning($"ForceSimplified: cornice della terra non ricostruita: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Un dettaglio di cornice **pulito, di solo colore**, ricavato da
        /// <c>FrameColors</c> — che la ripulitura non tocca. Null se non c'e'
        /// niente da dire.
        ///
        /// Non rimettiamo la stringa originale: fra le terre ci sono anche
        /// <c>"alt WU land"</c>, <c>"Z land borderless"</c>,
        /// <c>"W land, no text box"</c> — li' dentro lo stile c'e' eccome, ed
        /// e' proprio quello che la semplificazione deve togliere. La
        /// ricostruiamo invece nella forma piu' corta, che nel database esiste
        /// sempre: <c>"WU land"</c>, <c>"C land"</c> (incolore),
        /// <c>"Z land"</c> (tre o piu' colori), <c>"U basic land"</c>.
        /// </summary>
        private static string LandFrameDetail(CardPrintingRecord record)
        {
            var colors = record.FrameColors;
            if (colors == null) return null;

            var letters = string.Empty;
            foreach (var c in colors)
            {
                switch (c)
                {
                    case Wotc.Mtgo.Gre.External.Messaging.CardColor.White: letters += "W"; break;
                    case Wotc.Mtgo.Gre.External.Messaging.CardColor.Blue:  letters += "U"; break;
                    case Wotc.Mtgo.Gre.External.Messaging.CardColor.Black: letters += "B"; break;
                    case Wotc.Mtgo.Gre.External.Messaging.CardColor.Red:   letters += "R"; break;
                    case Wotc.Mtgo.Gre.External.Messaging.CardColor.Green: letters += "G"; break;
                }
            }

            // "Z" e' la tinta oro delle terre a tre o piu' colori, "C" quella
            // incolore: due valori che nel database compaiono da soli.
            if (letters.Length >= 3) letters = "Z";
            else if (letters.Length == 0) letters = "C";

            return IsBasic(record) ? letters + " basic land" : letters + " land";
        }

        private static bool IsBasic(CardPrintingRecord record)
        {
            var supertypes = record.Supertypes;
            if (supertypes == null) return false;
            for (int i = 0; i < supertypes.Count; i++)
                if (supertypes[i] == Wotc.Mtgo.Gre.External.Messaging.SuperType.Basic) return true;
            return false;
        }

        /// <summary>
        /// I soli descrittori di **tinta** contenuti negli
        /// <c>AdditionalFrameDetails</c> del record originale, o null se non ce
        /// n'e' nessuno.
        ///
        /// PERCHE' SERVE: sulle terre moderne il colore della cornice **non**
        /// sta in <c>RawFrameDetail</c> — nel database quel campo e' gia' vuoto
        /// di suo (Rumble Arena, Hidden Grotto: <c>RawFrameDetail = ""</c>,
        /// <c>AdditionalFrameDetails = "gold"</c>). Sta qui dentro. E
        /// <c>ClearRecord</c> tiene solo "hybrid" e butta via tutto il resto,
        /// "gold" compreso: da li' la terra a tre o piu' colori restava senza
        /// alcun dato di cornice, e l'albero degli asset ripiegava sulla
        /// cornice neutra — grigia, come una terra incolore.
        ///
        /// Il vocabolario del campo sulle terre e' corto e verificato su tutto
        /// il database di Arena: "hybrid", "gold", "beyond", "Pre 8E", "snow",
        /// "phyrexianlanguage", "phyrexian", "adventure", "story spotlight".
        /// Solo i primi due dicono di che colore e' la cornice; gli altri sono
        /// stile, ed e' giusto che la semplificazione se li porti via.
        /// </summary>
        private static System.Collections.Generic.IReadOnlyCollection<string> ColourFrameDetails(
            CardPrintingRecord record)
        {
            var details = record.AdditionalFrameDetails;
            if (details == null) return null;

            System.Collections.Generic.List<string> kept = null;
            foreach (var detail in details)
            {
                if (detail == null) continue;
                if (!detail.Equals("gold", StringComparison.OrdinalIgnoreCase) &&
                    !detail.Equals("hybrid", StringComparison.OrdinalIgnoreCase)) continue;

                if (kept == null) kept = new System.Collections.Generic.List<string>(2);
                kept.Add(detail);
            }
            return kept;
        }

        /// <summary>
        /// Le carte gia' segnalate dalla guardia 5, per non ripetere la stessa
        /// riga a ogni disegno. Il log resta utile — dice quali carte la
        /// semplificazione sta saltando — senza diventare rumore.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<uint> _unknownDiagSeen =
            new System.Collections.Generic.HashSet<uint>();

        /// <summary>Dove si trova questa carta nella gerarchia Unity.</summary>
        private static string HolderPath(DuelScene_CDC cdc)
        {
            var path = string.Empty;
            var t = cdc != null ? cdc.transform : null;
            for (int i = 0; i < 4 && t != null; i++)
            {
                path = t.name + (path.Length > 0 ? "/" + path : "");
                t = t.parent;
            }
            return path;
        }

        private static bool IsLand(CardPrintingRecord record)
        {
            var types = record.Types;
            if (types == null) return false;
            for (int i = 0; i < types.Count; i++)
                if (types[i] == Wotc.Mtgo.Gre.External.Messaging.CardType.Land) return true;
            return false;
        }

        /// <summary>
        /// A2/A3) Azzera l override quando il CDC torna nel pool o cambia carta.
        /// Se i visual verranno aggiornati, CheckSimplifyOverride lo ricalcola
        /// subito dopo; altrimenti resta nullo e VisualModel ricade sul modello
        /// reale, che e' esattamente il comportamento originale di Arena.
        /// </summary>
        private static void ClearOverride_Postfix(DuelScene_CDC __instance)
        {
            using var _fc = Diagnostics.FrameCost.Measure("ForceSimplified.ClearOverride");
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
            using var _fc = Diagnostics.FrameCost.Measure("ForceSimplified.VisualModel");
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
            using var _fc = Diagnostics.FrameCost.Measure("ForceSimplified.ExamineState");
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
