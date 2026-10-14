using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using MTGAEnhancementSuite.Features.CollectionExport;
using MTGAEnhancementSuite.UI;
using MTGAEnhancementSuite.Helpers;
using UnityEngine;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Exports the player's MTGA collection in one of the formats of
    /// <see cref="ExportProfile"/> (Moxfield/Archidekt, ManaBox, MTGGoldfish,
    /// generic CSV, plain name list).
    ///
    /// Data sources (all resolved at export time, no caching):
    ///   - Pantry.Get&lt;IInventoryServiceWrapper&gt;().Cards — Dictionary&lt;uint,int&gt;
    ///     of grpId → owned-count.
    ///   - CardDatabase.CardDataProvider.GetCardPrintingById(grpId, null) —
    ///     CardPrintingData with TitleId / ExpansionCode / CollectorNumber /
    ///     IsToken / IsDigitalOnly / IsRebalanced.
    ///   - CardDatabase.CardTitleProvider.GetCardTitle(grpId, ...) — English name.
    ///
    /// Questo file fa solo il lato Arena: legge l'inventario e il database
    /// delle carte e li consegna come <see cref="ArenaCard"/>. Identificazione
    /// delle stampe (indice Scryfall), modello comune e formati stanno in
    /// Features/CollectionExport, senza Unity, e si provano da
    /// tests/CollectionExport.Tests.
    ///
    /// **Riscritto il 2026-10-14.** Prima il file era "copie nome", sommato
    /// per NOME: set e numero letti e buttati, le stampe diverse fuse in una
    /// riga, le split card scritte col separatore di Arena (" /// ") che
    /// nessun sito riconosce, le carte Alchemy fuse con la loro versione
    /// cartacea (lo sprite "A" veniva tolto insieme ai tag), e le carte senza
    /// nome scartate con una riga di log e nient'altro. Quel formato resta
    /// come "Name list", per chi lo usava per incollare nomi.
    ///
    /// File save uses native Win32 GetSaveFileNameW (comdlg32) so we don't need
    /// System.Windows.Forms or Microsoft.Win32 references — both are missing
    /// from BepInEx's stripped Mono profile.
    /// </summary>
    internal static class CollectionExporter
    {
        /// <summary>Cosa e' stato scritto, per il riepilogo della finestra.</summary>
        internal sealed class Result
        {
            public string Path;
            public ExportProfile Profile;
            public CollectionExportModel Model;
            public ProfileOutput Output;
        }

        // -------- Public entry point --------

        /// <summary>Il pulsante del pannello: apre la finestra, dove si sceglie il formato.</summary>
        public static void Export()
        {
            UI.CollectionExportWindow.Show();
        }

        /// <summary>
        /// Esporta nel formato scelto. If the inventory cache is already
        /// populated we export synchronously. Otherwise we fire
        /// GetPlayerCards(0) to fetch from MTGA's server, poll the returned
        /// Promise, then export. <paramref name="done"/> arriva solo a file
        /// scritto; gli errori li dice un Toast, come prima.
        /// </summary>
        public static void Run(ExportProfile profile, ScryfallArenaIndex index, Action<Result> done)
        {
            try
            {
                var cards = ResolveOwnedCards();
                if (cards != null && cards.Count > 0)
                {
                    Finish(cards, profile, index, done);
                    return;
                }

                // No local cache — pull from server first.
                //
                // I dati arrivano da Arena (InventoryManager.RefreshCards), non
                // da Firebase: qui serve solo un MonoBehaviour vivo su cui far
                // girare la coroutine. Usiamo quello persistente del plugin.
                Plugin.Log.LogInfo("CollectionExporter: no cached cards, fetching from server");
                var host = PluginBehaviour.Instance;
                if (host == null)
                {
                    Plugin.Log.LogError("CollectionExporter: PluginBehaviour non disponibile, impossibile avviare il fetch");
                    Toast.Error("Export failed: plugin not ready");
                    return;
                }
                Toast.Info("Fetching collection from server…");
                host.StartCoroutine(FetchThenExport(profile, index, done));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CollectionExporter.Export failed: {ex}");
                Toast.Error($"Export failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Coroutine: triggers WrapperController.Instance.InventoryManager.RefreshCards()
        /// — the same call MTGA's deck builder uses — and waits for it to
        /// resolve before exporting. RefreshCards() internally calls
        /// IInventoryServiceWrapper.GetPlayerCards(version) and, in a Then
        /// continuation, assigns the server result into Cards. So when the
        /// returned Promise.IsDone, Cards is populated.
        ///
        /// Falls back to reading Promise.Result.cards directly if Cards stays
        /// empty (e.g. cacheVersion didn't change).
        /// </summary>
        private static IEnumerator FetchThenExport(ExportProfile profile, ScryfallArenaIndex index, Action<Result> done)
        {
            object inventoryManager = ResolveInventoryManager();
            if (inventoryManager == null)
            {
                Toast.Error("Inventory not loaded yet — try again in a moment");
                Plugin.Log.LogWarning("CollectionExporter: WrapperController.InventoryManager not available");
                yield break;
            }

            var refreshCards = AccessTools.Method(inventoryManager.GetType(), "RefreshCards");
            if (refreshCards == null)
            {
                Toast.Error("Could not find RefreshCards");
                Plugin.Log.LogWarning("CollectionExporter: InventoryManager.RefreshCards not found");
                yield break;
            }

            object promise;
            try { promise = refreshCards.Invoke(inventoryManager, null); }
            catch (Exception ex)
            {
                Toast.Error($"RefreshCards threw: {ex.Message}");
                Plugin.Log.LogError($"CollectionExporter: RefreshCards: {ex}");
                yield break;
            }
            if (promise == null)
            {
                Toast.Error("RefreshCards returned null");
                yield break;
            }

            var isDoneProp = AccessTools.Property(promise.GetType(), "IsDone");
            var successfulProp = AccessTools.Property(promise.GetType(), "Successful");
            if (isDoneProp == null)
            {
                Toast.Error("Promise has no IsDone — can't poll");
                Plugin.Log.LogWarning($"CollectionExporter: Promise type {promise.GetType().FullName} unexpected");
                yield break;
            }

            bool finished = false;
            for (int i = 0; i < 60; i++)
            {
                yield return new WaitForSeconds(0.5f);
                if ((bool)isDoneProp.GetValue(promise)) { finished = true; break; }
            }
            if (!finished)
            {
                Toast.Error("Server didn't respond within 30s — try again");
                Plugin.Log.LogWarning("CollectionExporter: RefreshCards timeout");
                yield break;
            }

            bool ok = successfulProp != null && (bool)successfulProp.GetValue(promise);
            if (!ok)
            {
                Toast.Error("Server returned an error fetching collection");
                Plugin.Log.LogWarning("CollectionExporter: RefreshCards Successful=false");
                yield break;
            }

            // Wait one more frame so the .Then continuation that assigns
            // Cards has a chance to execute before we read.
            yield return null;

            var cards = ResolveOwnedCards();
            if (cards == null || cards.Count == 0)
            {
                // Fallback: pull directly from Promise.Result.cards
                cards = ReadCardsFromPromise(promise);
            }
            if (cards == null || cards.Count == 0)
            {
                Toast.Warning("Server returned an empty collection");
                Plugin.Log.LogWarning("CollectionExporter: post-fetch Cards still empty");
                yield break;
            }

            Finish(cards, profile, index, done);
        }

        /// <summary>Reflectively walks promise.Result.cards (a Dictionary&lt;uint,int&gt;).</summary>
        private static IDictionary<uint, int> ReadCardsFromPromise(object promise)
        {
            var resultProp = AccessTools.Property(promise.GetType(), "Result");
            var result = resultProp?.GetValue(promise);
            if (result == null) return null;
            // CardsAndCacheVersion has a `cards` field of type CardsAndQuantity
            // (which is `Dictionary<uint, int>`).
            var cardsField = AccessTools.Field(result.GetType(), "cards");
            return cardsField?.GetValue(result) as IDictionary<uint, int>;
        }

        /// <summary>
        /// Captures the (now-loaded) collection, builds the chosen format,
        /// shows the save dialog, writes the file. Called from both the fast
        /// path (cache hit) and the post-fetch path. The save dialog blocks the
        /// main thread for its duration — that's normal Windows OFN behavior.
        /// </summary>
        private static void Finish(IDictionary<uint, int> cards, ExportProfile profile,
                                   ScryfallArenaIndex index, Action<Result> done)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var arenaCards = ReadArenaCards(cards);
                if (arenaCards == null)
                {
                    Toast.Error("Export failed: card database not available — see log");
                    return;
                }

                var model = CollectionMapper.Build(arenaCards, index);
                if (model.CopiesIn == 0)
                {
                    Toast.Warning("No cards returned — your collection appears empty");
                    Plugin.Log.LogWarning("CollectionExporter: inventory has no positive counts");
                    return;
                }
                var output = profile.Write(model);
                sw.Stop();

                // Il log dice cosa e' stato esaminato, non solo l'esito
                // (regola 19): copie lette, dove sono finite, e quanto e' costato.
                Plugin.Log.LogInfo(
                    $"CollectionExporter: {profile.Id}: read {cards.Count} entries / {model.CopiesIn} copies; " +
                    $"rows {model.Rows.Count} (arena_id {model.CopiesWith(MatchPrecision.ArenaId)}, " +
                    $"set+number {model.CopiesWith(MatchPrecision.SetAndNumber)}, name only {model.CopiesWith(MatchPrecision.NameOnly)} copies); " +
                    $"skipped {model.CopiesSkipped}; not in format {output.Rejected.Sum(r => r.Quantity)}; " +
                    $"file {output.DataLines} lines / {output.CopiesWritten} copies; " +
                    $"Scryfall index {(index != null ? index.Count + " prints" : "none")}; {sw.ElapsedMilliseconds} ms");
                foreach (var s in model.Skipped)
                    Plugin.Log.LogInfo($"CollectionExporter:   skipped grpId {s.GrpId} x{s.Count} {s.Name}: {s.Reason}");

                var defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!ShowSaveDialog(profile.FileName, defaultDir, profile.FileFilter, profile.Extension, out string path))
                {
                    Plugin.Log.LogInfo("CollectionExporter: user cancelled save dialog");
                    return;
                }

                // UTF-8 senza BOM: vedi ExportProfile, il BOM rovina il nome
                // della prima colonna per chi lo confronta alla lettera.
                File.WriteAllText(path, output.Text, new UTF8Encoding(false));
                Toast.Success($"Exported {output.CopiesWritten} copies ({output.DataLines} lines) to {Path.GetFileName(path)}");
                Plugin.Log.LogInfo($"CollectionExporter: wrote {path}");

                done?.Invoke(new Result { Path = path, Profile = profile, Model = model, Output = output });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CollectionExporter.Finish failed: {ex}");
                Toast.Error($"Export failed: {ex.Message}");
            }
        }

        // -------- Data extraction --------

        /// <summary>
        /// Every inventory entry as an <see cref="ArenaCard"/>, including the
        /// ones Arena can't describe (InArenaDatabase = false): deciding what to
        /// drop is the mapper's job, and it reports it. Returns null on fatal
        /// lookup failure (so we can show an error instead of saving an empty
        /// file).
        /// </summary>
        private static List<ArenaCard> ReadArenaCards(IDictionary<uint, int> cards)
        {
            object cardDb = ResolveCardDatabase();
            if (cardDb == null)
            {
                Plugin.Log.LogWarning("CollectionExporter: CardDatabase not in Pantry");
                return null;
            }

            // The deck builder pulls printing data via:
            //   cardDatabase.CardDataProvider.GetCardPrintingById(grpId, skinCode)
            // (returns CardPrintingData, which exposes ExpansionCode /
            // CollectorNumber / TitleId / IsToken as PROPERTIES, not fields).
            var providerProp = AccessTools.Property(cardDb.GetType(), "CardDataProvider");
            var cardDataProvider = providerProp?.GetValue(cardDb);
            if (cardDataProvider == null)
            {
                Plugin.Log.LogWarning("CollectionExporter: CardDatabase.CardDataProvider is null");
                return null;
            }
            var getCardPrinting = AccessTools.Method(cardDataProvider.GetType(),
                "GetCardPrintingById", new[] { typeof(uint), typeof(string) });
            if (getCardPrinting == null)
            {
                Plugin.Log.LogWarning("CollectionExporter: GetCardPrintingById not found on CardDataProvider");
                return null;
            }

            // CardTitleProvider for the English name lookup.
            // Signature: GetCardTitle(uint grpId, bool formatted = true,
            //                         string overrideLanguageCode = null)
            // Note that the first arg is GRP ID, not title id — the provider
            // internally calls GetCardRecordById(grpId) and pulls TitleId out.
            // We force the language to "en-US" so the export is always English
            // regardless of the user's MTGA locale ("enUS" throws: the keys of
            // GreLocProvider's ShortLangCodes are dashed).
            var titleProviderProp = AccessTools.Property(cardDb.GetType(), "CardTitleProvider");
            object titleProvider = titleProviderProp?.GetValue(cardDb);
            MethodInfo getCardTitle = null;
            if (titleProvider != null)
            {
                getCardTitle = AccessTools.Method(titleProvider.GetType(), "GetCardTitle",
                    new[] { typeof(uint), typeof(bool), typeof(string) });
            }

            // Fallback: GreLocProvider.GetLocalizedText(titleId, overrideLang, formatted).
            // Used when CardTitleProvider returns "Unknown Card Title <id>" — that
            // happens for grpIds whose CardPrintingRecord didn't load (e.g. very
            // recent printings) but whose TitleId is still resolvable in the loc
            // table. The deck builder uses this exact path via GetOriginalCardTitle.
            var locProviderProp = AccessTools.Property(cardDb.GetType(), "GreLocProvider");
            object locProvider = locProviderProp?.GetValue(cardDb);
            MethodInfo getLocalizedText = null;
            if (locProvider != null)
            {
                getLocalizedText = AccessTools.Method(locProvider.GetType(), "GetLocalizedText",
                    new[] { typeof(uint), typeof(string), typeof(bool) });
            }

            // Le PropertyInfo si cercano una volta per tipo, non una per carta:
            // una collezione sono migliaia di voci.
            Type printingType = null;
            PropertyInfo pToken = null, pTitleId = null, pSet = null, pNumber = null, pDigital = null, pRebalanced = null;

            var result = new List<ArenaCard>(cards.Count);
            int notInDb = 0;
            foreach (var kv in cards)
            {
                uint grpId = kv.Key;
                var card = new ArenaCard { GrpId = grpId, Count = kv.Value };
                result.Add(card);
                if (kv.Value <= 0) continue;

                object printing = null;
                try { printing = getCardPrinting.Invoke(cardDataProvider, new object[] { grpId, null }); }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"CollectionExporter: GetCardPrintingById({grpId}) threw: {ex.Message}");
                }
                if (printing == null) { card.InArenaDatabase = false; notInDb++; continue; }

                if (printing.GetType() != printingType)
                {
                    printingType = printing.GetType();
                    pToken = AccessTools.Property(printingType, "IsToken");
                    pTitleId = AccessTools.Property(printingType, "TitleId");
                    pSet = AccessTools.Property(printingType, "ExpansionCode");
                    pNumber = AccessTools.Property(printingType, "CollectorNumber");
                    pDigital = AccessTools.Property(printingType, "IsDigitalOnly");
                    pRebalanced = AccessTools.Property(printingType, "IsRebalanced");
                }

                card.IsToken = pToken != null && (bool)pToken.GetValue(printing);
                card.ArenaSetCode = pSet?.GetValue(printing) as string;
                card.CollectorNumber = pNumber?.GetValue(printing) as string;
                card.IsDigitalOnly = pDigital != null && (bool)pDigital.GetValue(printing);
                card.IsRebalanced = pRebalanced != null && (bool)pRebalanced.GetValue(printing);

                // Il nome GREZZO, coi tag: e' il mapper a ripulirlo, perche'
                // lo sprite "A" delle carte Alchemy va riconosciuto (via
                // IsRebalanced) e non solo tolto, e " /// " va tradotto.
                if (getCardTitle != null && titleProvider != null)
                {
                    try
                    {
                        var resolved = getCardTitle.Invoke(titleProvider,
                            new object[] { grpId, /*formatted*/ true, /*lang*/ "en-US" }) as string;
                        if (!string.IsNullOrEmpty(resolved) &&
                            !resolved.StartsWith("Unknown Card Title", StringComparison.Ordinal))
                            card.Name = resolved;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"CollectionExporter: GetCardTitle({grpId}) threw: {ex.Message}");
                    }
                }

                if (card.Name == null && pTitleId != null && getLocalizedText != null && locProvider != null)
                {
                    uint titleId = (uint)pTitleId.GetValue(printing);
                    if (titleId != 0)
                    {
                        try
                        {
                            var resolved = getLocalizedText.Invoke(locProvider,
                                new object[] { titleId, /*overrideLang*/ "en-US", /*formatted*/ true }) as string;
                            if (!string.IsNullOrEmpty(resolved) &&
                                !resolved.StartsWith("Unknown Card Title", StringComparison.Ordinal))
                                card.Name = resolved;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"CollectionExporter: GetLocalizedText({titleId}) threw: {ex.Message}");
                        }
                    }
                }
            }

            if (notInDb > 0)
                Plugin.Log.LogInfo($"CollectionExporter: {notInDb} grpIds had no printing in the Arena card database");
            if (printingType != null && (pSet == null || pNumber == null || pRebalanced == null))
                Plugin.Log.LogWarning($"CollectionExporter: {printingType.FullName} is missing ExpansionCode/CollectorNumber/IsRebalanced — Arena changed?");
            return result;
        }

        // -------- Pantry lookups --------

        /// <summary>
        /// Returns the IInventoryServiceWrapper instance from Pantry plus its
        /// runtime Type (for reflection). Both null if Pantry isn't ready or
        /// the interface isn't registered.
        /// </summary>
        private static object ResolveInventoryWrapper(out Type wrapperType)
        {
            wrapperType = null;
            var pantryType = GameReflection.TypeByName("Pantry");
            if (pantryType == null) return null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.IsInterface && t.Name == "IInventoryServiceWrapper")
                    { wrapperType = t; break; }
                }
                if (wrapperType != null) break;
            }
            if (wrapperType == null) return null;

            try
            {
                var get = pantryType.GetMethod("Get").MakeGenericMethod(wrapperType);
                return get.Invoke(null, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CollectionExporter: Pantry.Get<IInventoryServiceWrapper>() threw: {ex.Message}");
                return null;
            }
        }

        private static IDictionary<uint, int> ResolveOwnedCards()
        {
            var wrapper = ResolveInventoryWrapper(out Type wrapperType);
            if (wrapper == null) return null;
            var cardsProp = AccessTools.Property(wrapperType, "Cards");
            return cardsProp?.GetValue(wrapper) as IDictionary<uint, int>;
        }

        /// <summary>
        /// Returns WrapperController.Instance.InventoryManager — the same
        /// path the deck builder takes. This is preferred over Pantry-based
        /// access for collection refresh because RefreshCards() lives here
        /// and includes the assigns-back-to-Cards continuation.
        /// </summary>
        private static object ResolveInventoryManager()
        {
            var wrapperControllerType = GameReflection.TypeByName("WrapperController");
            if (wrapperControllerType == null) return null;
            var instanceProp = AccessTools.Property(wrapperControllerType, "Instance");
            var wc = instanceProp?.GetValue(null);
            if (wc == null) return null;
            var imProp = AccessTools.Property(wrapperControllerType, "InventoryManager");
            return imProp?.GetValue(wc);
        }

        private static object ResolveCardDatabase()
        {
            var pantryType = GameReflection.TypeByName("Pantry");
            if (pantryType == null) return null;

            Type cardDbType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in asm.GetTypes())
                {
                    if (!t.IsAbstract && !t.IsInterface && t.Name == "CardDatabase")
                    { cardDbType = t; break; }
                }
                if (cardDbType != null) break;
            }
            if (cardDbType == null) return null;

            return pantryType.GetMethod("Get").MakeGenericMethod(cardDbType).Invoke(null, null);
        }

        // -------- Win32 save dialog (P/Invoke) --------
        // We use the legacy GetSaveFileNameW because it works fine on Unity's
        // MTA main thread. The newer IFileSaveDialog COM interface requires
        // STA which Unity isn't.

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class OPENFILENAME
        {
            public int lStructSize = Marshal.SizeOf(typeof(OPENFILENAME));
            public IntPtr hwndOwner = IntPtr.Zero;
            public IntPtr hInstance = IntPtr.Zero;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public string lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData = IntPtr.Zero;
            public IntPtr lpfnHook = IntPtr.Zero;
            public string lpTemplateName;
            public IntPtr pvReserved = IntPtr.Zero;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("Comdlg32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetSaveFileName([In, Out] OPENFILENAME ofn);

        private const int OFN_OVERWRITEPROMPT = 0x00000002;
        private const int OFN_PATHMUSTEXIST   = 0x00000800;
        private const int OFN_HIDEREADONLY    = 0x00000004;
        private const int OFN_EXPLORER        = 0x00080000;

        private static bool ShowSaveDialog(string defaultName, string initialDir, string filter, string extension,
                                           out string chosenPath)
        {
            chosenPath = null;
            // The OS writes back into ofn.lpstrFile, so we need a buffer
            // pre-sized to fit. Pre-fill with the default name + padding.
            var buf = new string('\0', 260);
            // Inject the default name at the start of the buffer
            buf = defaultName.PadRight(260, '\0').Substring(0, 260);

            var ofn = new OPENFILENAME
            {
                lpstrFilter = filter,
                lpstrFile = buf,
                nMaxFile = 260,
                lpstrInitialDir = initialDir,
                lpstrTitle = "Export MTGA Collection",
                lpstrDefExt = extension,
                Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_EXPLORER,
            };

            bool ok = GetSaveFileName(ofn);
            if (!ok) return false;

            // ofn.lpstrFile was overwritten with the chosen full path,
            // null-terminated. Trim padding.
            chosenPath = (ofn.lpstrFile ?? "").TrimEnd('\0');
            if (string.IsNullOrEmpty(chosenPath)) return false;
            // Force the extension if the user typed none and OFN_DEFEXT didn't
            // append it. Si guarda il NOME del file: un punto in una cartella
            // del percorso non e' un'estensione.
            if (!Path.GetFileName(chosenPath).Contains(".")) chosenPath += "." + extension;
            return true;
        }
    }
}
