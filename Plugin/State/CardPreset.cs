using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MTGAEnhancementSuite.State
{
    /// <summary>
    /// Una carta dentro un preset. Identificata dal <c>grpId</c>, cioe' la
    /// stampa specifica: e' la stessa chiave che usa il deck builder di Arena
    /// (<c>DeckBuilderModel.AddCardToMainDeck(uint grpId, uint count)</c>),
    /// quindi non serve alcuna conversione al momento di applicare.
    /// </summary>
    internal class PresetCard
    {
        [JsonProperty("grpId")]
        public uint GrpId { get; set; }

        [JsonProperty("count")]
        public uint Count { get; set; }

        /// <summary>
        /// Nome leggibile, salvato solo per poter mostrare il contenuto del
        /// preset senza interrogare il database carte. Non e' autoritativo:
        /// l inserimento avviene sempre per grpId.
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        public PresetCard() { }

        public PresetCard(uint grpId, uint count, string name)
        {
            GrpId = grpId;
            Count = count;
            Name = name;
        }
    }

    /// <summary>
    /// Un gruppo di carte riutilizzabile, da inserire in blocco in un mazzo
    /// aperto nel deck editor. Vive solo nel settings.json del mod: non tocca
    /// i mazzi di Arena ne' viene inviato al server.
    /// </summary>
    internal class CardPreset
    {
        [JsonProperty("id")]
        public Guid Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("cards")]
        public List<PresetCard> Cards { get; set; } = new List<PresetCard>();

        [JsonProperty("createdAt")]
        public long CreatedAt { get; set; }

        public CardPreset() { }

        public CardPreset(string name)
        {
            Id = Guid.NewGuid();
            Name = string.IsNullOrEmpty(name) ? "Preset" : name;
            Cards = new List<PresetCard>();
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>Numero totale di copie, non di voci distinte.</summary>
        [JsonIgnore]
        public uint TotalCards
        {
            get
            {
                uint n = 0;
                if (Cards != null)
                    foreach (var c in Cards) { if (c != null) n += c.Count; }
                return n;
            }
        }
    }
}
