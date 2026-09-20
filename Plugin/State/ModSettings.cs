using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace MTGAEnhancementSuite.State
{
    /// <summary>
    /// Persistent mod settings stored in settings.json alongside the plugin DLL.
    /// </summary>
    internal class ModSettings
    {
        private static ModSettings _instance;
        private static string _settingsPath;

        [JsonProperty("disableCompanions")]
        public bool DisableCompanions { get; set; } = false;

        [JsonProperty("disableCardVFX")]
        public bool DisableCardVFX { get; set; } = false;

        /// <summary>
        /// Forza la vista semplificata delle carte: rimuove i trattamenti
        /// grafici speciali (showcase, borderless, alt-art) mantenendo
        /// l illustrazione. Vale sia per le carte in partita sia per la
        /// finestra di esame. Vedi Patches/ForceSimplifiedPatch.cs.
        /// </summary>
        [JsonProperty("forceSimplifiedCards")]
        public bool ForceSimplifiedCards { get; set; } = false;

        /// <summary>
        /// Nasconde dalla lista delle carte del deck editor quelle di cui il
        /// mazzo aperto ha gia' il massimo consentito dal formato (4, oppure 1
        /// in Brawl; le terre base e le carte "quante ne vuoi" non spariscono
        /// mai). Vedi Patches/HideDeckCardsPatch.cs.
        /// </summary>
        [JsonProperty("hideCardsInDeck")]
        public bool HideCardsInDeck { get; set; } = false;

        /// <summary>
        /// Cartellino coi totali di forza e costituzione durante il
        /// combattimento: quanto sta attaccando, quanto puo' ancora bloccare
        /// chi difende. Si accende e si spegne con F8 (non c'e' ancora una
        /// riga nel pannello Impostazioni). Vedi Features/CombatTotals.cs.
        /// </summary>
        [JsonProperty("combatTotals")]
        public bool CombatTotals { get; set; } = true;

        /// <summary>
        /// Salvataggio del flusso grezzo delle partite, per un futuro
        /// visualizzatore di replay. **Spento di default, di proposito:** la
        /// registrazione occupa spazio su disco e il costo reale del formato
        /// non e' ancora misurato (vedi Features/ReplayRecorder.cs). Le
        /// statistiche dei mazzi NON dipendono da questo: quelle si registrano
        /// sempre e pesano una riga di testo a partita.
        /// </summary>
        [JsonProperty("recordReplays")]
        public bool RecordReplays { get; set; } = false;



        /// <summary>
        /// LEGACY: pre-v0.19 user-defined deck folders + root-order, stored as
        /// a single shared blob. Kept for backwards compat — on first access
        /// for an account, <see cref="Features.DeckOrganizationManager"/>
        /// migrates whatever's in here into that account's slot in
        /// <see cref="DeckOrganizationByAccount"/>, then clears it. Folder data
        /// is account-specific because folder DeckIds reference cloud decks,
        /// which differ per MTGA account.
        /// </summary>
        [JsonProperty("deckOrganization")]
        public DeckOrganization DeckOrganization { get; set; } = new DeckOrganization();

        /// <summary>
        /// User-defined deck folders + root-level ordering, keyed by MTGA
        /// PersonaID. A user with multiple accounts gets independent folder
        /// structures per account — switching accounts doesn't disturb the
        /// other's setup. Stored on-disk in this settings file only — never in
        /// deck names, never in MTGA's deck state, never on the wire.
        /// </summary>
        [JsonProperty("deckOrganizationByAccount")]
        public Dictionary<string, DeckOrganization> DeckOrganizationByAccount { get; set; }
            = new Dictionary<string, DeckOrganization>();

        /// <summary>
        /// Preset di carte riutilizzabili, inseribili in blocco in un mazzo
        /// aperto nel deck editor. A differenza delle cartelle NON sono divisi
        /// per account: un preset e' una semplice lista di grpId, quindi
        /// riutilizzabile su qualunque account (fermo restando che il deck
        /// builder rifiutera' le carte non possedute, come farebbe comunque).
        /// </summary>
        [JsonProperty("cardPresets")]
        public List<CardPreset> CardPresets { get; set; } = new List<CardPreset>();

        public static ModSettings Instance
        {
            get
            {
                if (_instance == null)
                    _instance = Load();
                return _instance;
            }
        }

        private static string GetSettingsPath()
        {
            if (_settingsPath == null)
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                _settingsPath = Path.Combine(pluginDir, "settings.json");
            }
            return _settingsPath;
        }

        private static ModSettings Load()
        {
            try
            {
                var path = GetSettingsPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var settings = JsonConvert.DeserializeObject<ModSettings>(json);
                    // Older settings files won't have a deckOrganization field;
                    // the property initializer + Json.NET default handling
                    // leaves it as an empty DeckOrganization, but be defensive.
                    bool needsRewrite = false;
                    if (settings.DeckOrganization == null)
                    {
                        settings.DeckOrganization = new DeckOrganization();
                        needsRewrite = true;
                    }
                    if (settings.DeckOrganizationByAccount == null)
                    {
                        settings.DeckOrganizationByAccount = new Dictionary<string, DeckOrganization>();
                        needsRewrite = true;
                    }
                    // If the on-disk JSON doesn't already include the
                    // deckOrganization key, save once so the field becomes
                    // editable by hand. Cheap and self-healing.
                    if (!json.Contains("\"deckOrganization\"")) needsRewrite = true;

                    var folderCount = settings.DeckOrganization.Folders?.Count ?? 0;
                    var rootOrderCount = settings.DeckOrganization.RootOrder?.Count ?? 0;
                    Plugin.Log.LogInfo($"Settings loaded: companions={!settings.DisableCompanions}, cardVFX={!settings.DisableCardVFX}, folders={folderCount}, rootOrder={rootOrderCount}");

                    // Keep a once-per-launch backup of the file as loaded, BEFORE
                    // any runtime mutation. If something ever clobbers the folder
                    // data mid-session, the user (or we) can restore the launch
                    // state from settings.json.startup.bak.
                    try { File.Copy(path, path + ".startup.bak", true); }
                    catch (Exception bex) { Plugin.Log.LogWarning($"Settings backup failed: {bex.Message}"); }

                    if (needsRewrite) { _instance = settings; settings.Save(); }
                    return settings;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not load settings: {ex.Message}");
            }
            return new ModSettings();
        }

        public void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(GetSettingsPath(), json);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not save settings: {ex.Message}");
            }
        }
    }
}
