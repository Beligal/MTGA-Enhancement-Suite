using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using MTGAEnhancementSuite.Firebase;
using MTGAEnhancementSuite.UI;
using MTGAEnhancementSuite.Helpers;
using UnityEngine;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Exports the player's MTGA collection to a text file in MTGA's deck-list
    /// format: "<count> <card name> (<SET>) <collector#>".
    ///
    /// Data sources (all resolved at export time, no caching):
    ///   - Pantry.Get&lt;IInventoryServiceWrapper&gt;().Cards — Dictionary&lt;uint,int&gt;
    ///     of grpId → owned-count.
    ///   - Pantry.Get&lt;CardDatabase&gt;().GetCardRecordById(grpId, null) — returns
    ///     CardPrintingRecord with TitleId / ExpansionCode / CollectorNumber / Rarity.
    ///   - CardDatabase.CardTitleProvider.GetCardTitle(titleId, ...) — English name.
    ///
    /// File save uses native Win32 GetSaveFileNameW (comdlg32) so we don't need
    /// System.Windows.Forms or Microsoft.Win32 references — both are missing
    /// from BepInEx's stripped Mono profile.
    /// </summary>
    internal static class CollectionExporter
    {
        // -------- Public entry point --------

        /// <summary>
        /// Public entry point. If the inventory cache is already populated we
        /// export synchronously. Otherwise we fire GetPlayerCards(0) to fetch
        /// from MTGA's server, poll the returned Promise, then export.
        /// </summary>
        public static void Export()
        {
            try
            {
                var cards = ResolveOwnedCards();
                if (cards != null && cards.Count > 0)
                {
                    DoExport();
                    return;
                }

                // No local cache — pull from server first.
                Plugin.Log.LogInfo("CollectionExporter: no cached cards, fetching from server");
                Toast.Info("Fetching collection from server…");
                FirebaseClient.Instance.StartCoroutine(FetchThenExport());
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
        private static IEnumerator FetchThenExport()
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

            DoExportWithCards(cards);
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
        /// Captures the (now-loaded) collection, shows the save dialog, writes
        /// the file. Called from both the fast path (cache hit) and the
        /// post-fetch path. The save dialog blocks the main thread for its
        /// duration — that's normal Windows OFN behavior.
        /// </summary>
        private static void DoExport()
        {
            DoExportWithCards(null);
        }

        private static void DoExportWithCards(IDictionary<uint, int> overrideCards)
        {
            try
            {
                var lines = BuildExportLines(overrideCards, out int totalCards, out int distinctCards);
                if (lines == null || lines.Count == 0)
                {
                    Toast.Warning("No cards returned — your collection appears empty");
                    Plugin.Log.LogWarning("CollectionExporter: BuildExportLines returned empty");
                    return;
                }

                var defaultName = $"mtga_collection_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                var defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                if (!ShowSaveDialog(defaultName, defaultDir, out string path))
                {
                    Plugin.Log.LogInfo("CollectionExporter: user cancelled save dialog");
                    return;
                }

                File.WriteAllText(path, string.Join("\n", lines) + "\n");
                Toast.Success($"Exported {distinctCards} cards ({totalCards} copies) to {Path.GetFileName(path)}");
                Plugin.Log.LogInfo($"CollectionExporter: wrote {path} ({distinctCards} cards / {totalCards} copies)");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CollectionExporter.DoExport failed: {ex}");
                Toast.Error($"Export failed: {ex.Message}");
            }
        }

        // -------- Data extraction --------

        /// <summary>
        /// Builds the deck-list lines for every owned card. Returns null on
        /// fatal lookup failure (so we can show a warning instead of saving
        /// an empty file).
        /// </summary>
        private static List<string> BuildExportLines(IDictionary<uint, int> overrideCards,
            out int totalCards, out int distinctCards)
        {
            totalCards = 0;
            distinctCards = 0;

            var cards = overrideCards ?? ResolveOwnedCards();
            if (cards == null || cards.Count == 0)
            {
                Plugin.Log.LogWarning("CollectionExporter: IInventoryServiceWrapper.Cards is null/empty");
                return null;
            }

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
            // We force overrideLanguageCode = "enUS" so the export is always
            // English regardless of the user's MTGA locale.
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

            // Aggregate counts by cleaned-name, summing across printings. The
            // user-facing format is "<count> <name>" — set/collector# get
            // stripped because most deckbuilders just want a name list.
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int unresolved = 0;
            foreach (var kv in cards)
            {
                uint grpId = kv.Key;
                int count = kv.Value;
                if (count <= 0) continue;

                object printing;
                try { printing = getCardPrinting.Invoke(cardDataProvider, new object[] { grpId, null }); }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"CollectionExporter: GetCardPrintingById({grpId}) threw: {ex.Message}");
                    unresolved++;
                    continue;
                }
                if (printing == null) { unresolved++; continue; }
                var printingType = printing.GetType();

                // Skip tokens — they show up in the inventory dictionary but
                // aren't deck-listable.
                var isTokenProp = AccessTools.Property(printingType, "IsToken");
                if (isTokenProp != null && (bool)isTokenProp.GetValue(printing)) continue;

                string name = "Unknown";
                var titleIdProp = AccessTools.Property(printingType, "TitleId");

                // Try CardTitleProvider.GetCardTitle(grpId, formatted=true, lang=null).
                // We pass null for lang — that uses Languages.CurrentLanguage,
                // which is "en-US" by default. Passing the literal "enUS"
                // throws KeyNotFoundException inside GreLocProvider's
                // ShortLangCodes lookup (the keys are dashed: "en-US").
                if (getCardTitle != null && titleProvider != null)
                {
                    try
                    {
                        var resolved = getCardTitle.Invoke(titleProvider,
                            new object[] { grpId, /*formatted*/ true, /*lang*/ "en-US" }) as string;
                        if (!string.IsNullOrEmpty(resolved) &&
                            !resolved.StartsWith("Unknown Card Title", StringComparison.Ordinal))
                        {
                            name = resolved;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"CollectionExporter: GetCardTitle({grpId}) threw: {ex.Message}");
                    }
                }

                // Fallback: pull the title text directly via GreLocProvider on the
                // printing's TitleId. Catches cards CardTitleProvider can't resolve
                // (e.g. brand-new printings without a CardPrintingRecord match).
                if (name == "Unknown" && titleIdProp != null && getLocalizedText != null && locProvider != null)
                {
                    uint titleId = (uint)titleIdProp.GetValue(printing);
                    if (titleId != 0)
                    {
                        try
                        {
                            var resolved = getLocalizedText.Invoke(locProvider,
                                new object[] { titleId, /*overrideLang*/ "en-US", /*formatted*/ true }) as string;
                            if (!string.IsNullOrEmpty(resolved) &&
                                !resolved.StartsWith("Unknown Card Title", StringComparison.Ordinal))
                            {
                                name = resolved;
                            }
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"CollectionExporter: GetLocalizedText({titleId}) threw: {ex.Message}");
                        }
                    }
                }

                // Strip TMP rich-text markup. MTGA's localizations embed:
                //   <nobr>...</nobr>             — non-breaking spans on
                //                                  hyphenated names like
                //                                  "Agate-Blade Assassin".
                //   <sprite="..." name="...">    — Alchemy "A" badge / etc.
                //   <color=...>...</color>, <i>, <b>, etc.
                // None of these belong in a deckbuilder paste, so strip
                // every angle-bracket tag and collapse the whitespace.
                name = StripRichText(name);
                if (string.IsNullOrEmpty(name) || name == "Unknown") continue;

                if (byName.TryGetValue(name, out var existing))
                    byName[name] = existing + count;
                else
                    byName[name] = count;
                totalCards += count;
            }

            distinctCards = byName.Count;

            if (unresolved > 0)
                Plugin.Log.LogInfo($"CollectionExporter: {unresolved} grpIds had no CardPrintingRecord — skipped");

            // Sort alphabetically — most deckbuilder paste boxes accept any
            // order, but alphabetical reads better when the user opens the file.
            var lines = byName
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Value} {kv.Key}")
                .ToList();
            return lines;
        }

        // Matches every <...> tag (TMP rich text + sprite + color + nobr).
        private static readonly Regex RichTextTagRx = new Regex(
            @"<[^>]*>", RegexOptions.Compiled);
        // Compress runs of whitespace to a single space.
        private static readonly Regex WhitespaceRunRx = new Regex(
            @"\s+", RegexOptions.Compiled);

        private static string StripRichText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var stripped = RichTextTagRx.Replace(s, "");
            stripped = WhitespaceRunRx.Replace(stripped, " ").Trim();
            return stripped;
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

        private static bool ShowSaveDialog(string defaultName, string initialDir, out string chosenPath)
        {
            chosenPath = null;
            // The OS writes back into ofn.lpstrFile, so we need a buffer
            // pre-sized to fit. Pre-fill with the default name + padding.
            var buf = new string('\0', 260);
            // Inject the default name at the start of the buffer
            buf = defaultName.PadRight(260, '\0').Substring(0, 260);

            var ofn = new OPENFILENAME
            {
                lpstrFilter = "Text files (*.txt)\0*.txt\0All files (*.*)\0*.*\0\0",
                lpstrFile = buf,
                nMaxFile = 260,
                lpstrInitialDir = initialDir,
                lpstrTitle = "Export MTGA Collection",
                lpstrDefExt = "txt",
                Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_EXPLORER,
            };

            bool ok = GetSaveFileName(ofn);
            if (!ok) return false;

            // ofn.lpstrFile was overwritten with the chosen full path,
            // null-terminated. Trim padding.
            chosenPath = (ofn.lpstrFile ?? "").TrimEnd('\0');
            if (string.IsNullOrEmpty(chosenPath)) return false;
            // Force .txt if user typed no extension and OFN_DEFEXT didn't append
            if (!chosenPath.Contains(".")) chosenPath += ".txt";
            return true;
        }
    }
}
