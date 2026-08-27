using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.Firebase;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using MTGAEnhancementSuite.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Intercepts SetDeckForChallenge on PVPChallengeController to validate
    /// the selected deck against the format's legal cards list from Firebase
    /// (sourced from Scryfall) before allowing it to be set.
    /// </summary>
    internal static class DeckValidationPatch
    {
        private static bool _bypassValidation = false;

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
                            targetMethod = AccessTools.Method(type, "SetDeckForChallenge");
                            if (targetMethod != null)
                            {
                                Plugin.Log.LogInfo($"Found PVPChallengeController.SetDeckForChallenge in {asm.GetName().Name}");
                                break;
                            }
                        }
                    }
                    if (targetMethod != null) break;
                }

                if (targetMethod == null)
                {
                    Plugin.Log.LogWarning("DeckValidationPatch: Could not find PVPChallengeController.SetDeckForChallenge");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(DeckValidationPatch), nameof(Prefix));
                harmony.Patch(targetMethod, prefix: prefix);
                Plugin.Log.LogInfo("DeckValidationPatch applied");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DeckValidationPatch.Apply failed: {ex}");
            }
        }

        static bool Prefix(object __instance, Guid challengeId, Guid deckId)
        {
            try
            {
                if (_bypassValidation)
                {
                    _bypassValidation = false;
                    return true;
                }

                if (!ChallengeFormatState.HasFormat)
                    return true;

                if (deckId == Guid.Empty)
                    return true; // Clearing deck selection, allow

                Plugin.Log.LogInfo($"Intercepting deck selection for format validation (deck: {deckId})");
                Toast.Info("Checking deck legality...");

                // Start async validation
                FirebaseClient.Instance.StartCoroutine(
                    ValidateAndSetDeck(__instance, challengeId, deckId));

                return false; // Block until validation completes
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DeckValidationPatch.Prefix failed: {ex}");
                return true;
            }
        }

        private static IEnumerator ValidateAndSetDeck(object controller, Guid challengeId, Guid deckId)
        {
            // Step 0: Ensure authenticated — prompt retry if needed
            if (!FirebaseClient.Instance.IsAuthenticated)
            {
                bool? authResult = null;
                AuthPatch.RetryAuth(success => authResult = success);

                float authTimeout = 15f;
                while (authResult == null && authTimeout > 0f)
                {
                    yield return new WaitForSeconds(0.2f);
                    authTimeout -= 0.2f;
                }

                if (authResult != true)
                {
                    Toast.Error("Cannot validate deck without MTGA+ connection.");
                    yield break;
                }
            }

            string format = ChallengeFormatState.SelectedFormat;

            // Step 1: Extract the deck's card list locally
            List<DeckCard> deckCards = null;
            string error = null;

            try
            {
                deckCards = ExtractDeckCardsByDeckId(deckId);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Plugin.Log.LogError($"ExtractDeckCardsByDeckId failed: {ex}");
            }

            if (deckCards == null)
            {
                Toast.Warning(error ?? "Could not read deck contents, allowing selection");
                _bypassValidation = true;
                var m = AccessTools.Method(controller.GetType(), "SetDeckForChallenge");
                m.Invoke(controller, new object[] { challengeId, deckId });
                yield break;
            }

            // Step 2: POST decklist to validateDeck Cloud Function
            var decklistJson = new JArray();
            foreach (var card in deckCards)
            {
                var cardObj = new JObject
                {
                    ["grpId"] = card.ArenaId,
                    ["name"] = card.Name,
                    ["quantity"] = card.Quantity,
                    ["rarityValue"] = card.RarityValue,
                    ["isSideboard"] = card.IsSideboard,
                };
                decklistJson.Add(cardObj);
            }

            var payload = new JObject
            {
                ["challengeId"] = ChallengeFormatState.ActiveChallengeId.ToString(),
                ["gameModeId"] = format,
                ["format"] = format, // legacy alias for older Cloud Function builds
                ["decklist"] = decklistJson,
            };

            Plugin.Log.LogInfo($"Sending {deckCards.Count} cards to validateDeck for mode {format}");

            bool requestDone = false;
            JObject responseData = null;
            string requestError = null;

            FirebaseClient.Instance.CallCloudFunction("validateDeck", payload.ToString(),
                (success, responseBody) =>
                {
                    if (success && !string.IsNullOrEmpty(responseBody))
                    {
                        try { responseData = JObject.Parse(responseBody); }
                        catch { requestError = "Invalid response from server"; }
                    }
                    else
                    {
                        requestError = responseBody ?? "Request failed";
                    }
                    requestDone = true;
                });

            float timeout = 20f;
            while (!requestDone && timeout > 0f)
            {
                yield return new WaitForSeconds(0.1f);
                timeout -= 0.1f;
            }

            if (!requestDone)
            {
                Toast.Warning("Validation timed out, allowing selection");
                _bypassValidation = true;
                var m = AccessTools.Method(controller.GetType(), "SetDeckForChallenge");
                m.Invoke(controller, new object[] { challengeId, deckId });
                yield break;
            }

            if (requestError != null)
            {
                Plugin.Log.LogError($"validateDeck error: {requestError}");
                Toast.Warning("Could not validate deck, allowing selection");
                _bypassValidation = true;
                var m = AccessTools.Method(controller.GetType(), "SetDeckForChallenge");
                m.Invoke(controller, new object[] { challengeId, deckId });
                yield break;
            }

            // Step 3: Process response
            bool valid = responseData["valid"]?.Value<bool>() ?? true;

            if (valid)
            {
                Plugin.Log.LogInfo("Deck validation passed (server)");
                Toast.Success("Deck is legal!");

                _bypassValidation = true;
                var m = AccessTools.Method(controller.GetType(), "SetDeckForChallenge");
                m.Invoke(controller, new object[] { challengeId, deckId });
            }
            else
            {
                // Resolve display name: prefer the GameMode entry's friendly name.
                var modeEntry = ChallengeFormatState.GetGameMode(format);
                string formatName = modeEntry?.DisplayName
                    ?? responseData["gameModeId"]?.ToString()
                    ?? responseData["format"]?.ToString()
                    ?? format;

                // Build a single message body from all violations
                var violations = responseData["violations"] as JArray;
                var legacyIllegal = responseData["illegalCards"] as JArray;
                string message = $"Deck not legal in {formatName}.\n";

                if (violations != null && violations.Count > 0)
                {
                    foreach (var v in violations)
                    {
                        var msg = v["message"]?.ToString() ?? "Violation";
                        message += $"\n• {msg}";
                        var cards = v["cards"] as JArray;
                        if (cards != null && cards.Count > 0)
                        {
                            int shown = 0;
                            foreach (var c in cards)
                            {
                                var name = c["name"]?.ToString() ?? $"ID: {c["grpId"]}";
                                var qty = c["quantity"]?.Value<int>() ?? 1;
                                message += $"\n   - {name} x{qty}";
                                shown++;
                                if (shown >= 10 && cards.Count > 12)
                                {
                                    message += $"\n   ... and {cards.Count - shown} more";
                                    break;
                                }
                            }
                        }
                    }
                }
                else if (legacyIllegal != null)
                {
                    // Legacy response shape (older Cloud Function build)
                    int shown = 0;
                    int total = legacyIllegal.Count;
                    message += "\nIllegal cards:";
                    foreach (var card in legacyIllegal)
                    {
                        var name = card["name"]?.ToString() ?? $"ID: {card["grpId"]}";
                        var qty = card["quantity"]?.Value<int>() ?? 1;
                        message += $"\n  - {name} x{qty}";
                        shown++;
                        if (shown >= 15) { message += $"\n  ... and {total - shown} more"; break; }
                    }
                }

                Plugin.Log.LogWarning(message);
                Toast.Error($"Deck not legal in {formatName}");
                ShowErrorDialog("Deck Not Legal", message);
            }
        }

        /// <summary>
        /// Gets card list directly by deck ID without needing the challenge controller.
        /// </summary>
        private static List<DeckCard> ExtractDeckCardsByDeckId(Guid deckId)
        {
            // Use DecksManager (global namespace, concrete class) to get the deck
            var pantryType = GameReflection.TypeByName("Pantry");
            if (pantryType == null) throw new Exception("Pantry type not found");

            // Try DecksManager first (global namespace in Core.dll)
            Type decksManagerType = typeof(DecksManager);
            var getMethod = pantryType.GetMethod("Get").MakeGenericMethod(decksManagerType);
            var decksManager = getMethod.Invoke(null, null);

            if (decksManager == null) throw new Exception("DecksManager is null from Pantry");

            var getDeckMethod = AccessTools.Method(decksManagerType, "GetDeck", new[] { typeof(Guid) });
            if (getDeckMethod == null) throw new Exception("DecksManager.GetDeck(Guid) not found");

            var deck = getDeckMethod.Invoke(decksManager, new object[] { deckId });
            if (deck == null)
                throw new Exception($"Deck {deckId} not found");

            var contentsProperty = AccessTools.Property(deck.GetType(), "Contents");
            var contents = contentsProperty.GetValue(deck);
            var pilesField = AccessTools.Field(contents.GetType(), "Piles");
            var piles = pilesField.GetValue(contents) as IDictionary;

            // Get card title provider for name lookup
            object cardTitleProvider = null;
            MethodInfo getCardTitleMethod = null;

            try
            {
                // CardDatabase (Wotc.Mtga.Cards.Database) has a CardTitleProvider property
                Type cardDbType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == "CardDatabase" && !t.IsAbstract && !t.IsInterface)
                        {
                            cardDbType = t;
                            break;
                        }
                    }
                    if (cardDbType != null) break;
                }

                if (cardDbType == null) throw new Exception("CardDatabase type not found");

                var getCardDb = pantryType.GetMethod("Get").MakeGenericMethod(cardDbType);
                var cardDb = getCardDb.Invoke(null, null);
                if (cardDb != null)
                {
                    var titleProviderProp = AccessTools.Property(cardDbType, "CardTitleProvider");
                    if (titleProviderProp != null)
                    {
                        cardTitleProvider = titleProviderProp.GetValue(cardDb);
                        if (cardTitleProvider != null)
                        {
                            getCardTitleMethod = AccessTools.Method(cardTitleProvider.GetType(),
                                "GetCardTitle", new[] { typeof(uint), typeof(bool), typeof(string) });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not get CardTitleProvider: {ex.Message}");
            }

            // Get card data provider for rarity lookup
            object cardDataProvider = null;
            MethodInfo getCardRecordByIdMethod = null;
            FieldInfo rarityField = null;
            try
            {
                Type cardDbType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == "CardDatabase" && !t.IsAbstract && !t.IsInterface)
                        { cardDbType = t; break; }
                    }
                    if (cardDbType != null) break;
                }
                if (cardDbType != null)
                {
                    var cardDb2 = pantryType.GetMethod("Get").MakeGenericMethod(cardDbType).Invoke(null, null);
                    // CardDatabase exposes a CardDataProvider (or similar) via a property or field;
                    // GetCardRecordById exists on AltPrintingLayeredCardDataProvider and others.
                    // We can call GetCardRecordById on the database itself if it implements it,
                    // or via the Provider field.
                    getCardRecordByIdMethod = AccessTools.Method(cardDbType, "GetCardRecordById",
                        new[] { typeof(uint), typeof(string) });
                    if (getCardRecordByIdMethod != null) cardDataProvider = cardDb2;
                    else
                    {
                        // Try to find a Provider field
                        foreach (var f in cardDbType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (f.FieldType.Name.Contains("CardDataProvider") || f.Name.IndexOf("Provider", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var prov = f.GetValue(cardDb2);
                                if (prov == null) continue;
                                var m = AccessTools.Method(prov.GetType(), "GetCardRecordById",
                                    new[] { typeof(uint), typeof(string) });
                                if (m != null)
                                {
                                    cardDataProvider = prov;
                                    getCardRecordByIdMethod = m;
                                    break;
                                }
                            }
                        }
                    }
                    if (getCardRecordByIdMethod != null)
                    {
                        var recordType = getCardRecordByIdMethod.ReturnType;
                        rarityField = AccessTools.Field(recordType, "Rarity");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not get CardDataProvider for rarity: {ex.Message}");
            }

            var result = new List<DeckCard>();

            foreach (System.Collections.DictionaryEntry pile in piles)
            {
                var cards = pile.Value as IList;
                if (cards == null) continue;

                // Detect sideboard pile by key name (string or enum.ToString())
                var pileKey = pile.Key?.ToString() ?? "";
                bool isSideboard = pileKey.IndexOf("sideboard", StringComparison.OrdinalIgnoreCase) >= 0;

                foreach (var card in cards)
                {
                    var idField = AccessTools.Field(card.GetType(), "Id");
                    var qtyField = AccessTools.Field(card.GetType(), "Quantity");
                    var grpId = (uint)idField.GetValue(card);
                    var qty = (uint)qtyField.GetValue(card);

                    // Rarity lookup via CardDataProvider; defaults to 0 (Unknown) if unavailable.
                    int rarityValue = 0;
                    if (cardDataProvider != null && getCardRecordByIdMethod != null && rarityField != null)
                    {
                        try
                        {
                            var record = getCardRecordByIdMethod.Invoke(cardDataProvider, new object[] { grpId, null });
                            if (record != null)
                            {
                                var rarityEnum = rarityField.GetValue(record);
                                if (rarityEnum != null) rarityValue = (int)rarityEnum;
                            }
                        }
                        catch { }
                    }

                    string cardName = $"Card#{grpId}";

                    if (cardTitleProvider != null && getCardTitleMethod != null)
                    {
                        // Try English first, then client locale, then give up
                        string resolvedName = null;
                        foreach (var langCode in new[] { "en-US", null })
                        {
                            try
                            {
                                var name = getCardTitleMethod.Invoke(cardTitleProvider,
                                    new object[] { grpId, false, langCode }) as string;
                                if (!string.IsNullOrEmpty(name) && !name.StartsWith("Unknown Card Title"))
                                {
                                    resolvedName = System.Text.RegularExpressions.Regex.Replace(name, "<[^>]+>", "");
                                    break;
                                }
                            }
                            catch { }
                        }

                        if (resolvedName != null)
                        {
                            cardName = resolvedName;
                        }
                        else
                        {
                            Plugin.Log.LogWarning($"Could not resolve name for grpId={grpId}, sending as Card#{grpId}");
                        }
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"CardTitleProvider not available, using Card#{grpId}");
                    }

                    result.Add(new DeckCard
                    {
                        ArenaId = grpId,
                        Name = cardName,
                        Quantity = qty,
                        RarityValue = rarityValue,
                        IsSideboard = isSideboard,
                    });
                }
            }

            return result;
        }

        private static void ShowErrorDialog(string title, string message)
        {
            try
            {
                var smType = GameReflection.TypeByName("SystemMessageManager");
                var instanceProp = AccessTools.Property(smType, "Instance");
                var manager = instanceProp.GetValue(null);

                if (manager != null)
                {
                    var showOkMethod = AccessTools.Method(smType, "ShowOk",
                        new[] { typeof(string), typeof(string), typeof(Action),
                                typeof(string), smType.GetNestedType("SystemMessagePriority"),
                                typeof(string) });

                    showOkMethod.Invoke(manager, new object[] {
                        title, message, null, null, 0, null
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not show native dialog: {ex.Message}");
            }

            Plugin.Log.LogWarning($"[{title}] {message}");
        }

        private struct DeckCard
        {
            public uint ArenaId;
            public string Name;
            public uint Quantity;
            public int RarityValue;   // 1=Basic Land, 2=Common, 3=Uncommon, 4=Rare, 5=Mythic
            public bool IsSideboard;
        }
    }
}
