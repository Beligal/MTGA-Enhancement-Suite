using System;
using System.Collections.Generic;

namespace MTGAEnhancementSuite.Features.CollectionExport
{
    // Il modello comune dell'export della collezione.
    //
    // **Niente Unity e niente tipi di Arena in questa cartella** (regola 44 di
    // MISTAKES.md): tutto cio' che trasforma i dati si prova da
    // tests/CollectionExport.Tests, fuori dal gioco. Il lato Arena
    // (CollectionExporter.cs) legge il database delle carte e riempie
    // ArenaCard; da li' in poi niente sa piu' di essere dentro MTGA.

    /// <summary>
    /// Una voce dell'inventario di Arena, cosi' come la legge il gioco: un
    /// GrpId con le sue copie, piu' quello che il database delle carte dice
    /// di quella stampa. Nessun campo qui viene da Scryfall.
    /// </summary>
    internal sealed class ArenaCard
    {
        /// <summary>L'id della stampa in Arena. E' lo stesso numero che Scryfall chiama <c>arena_id</c>.</summary>
        public uint GrpId;
        public int Count;

        /// <summary>
        /// Il nome inglese come lo scrive Arena, gia' ripulito dai tag di
        /// TextMeshPro. Puo' essere null se il database non lo conosce.
        /// Attenzione: per le split card Arena usa " /// ", non " // ".
        /// </summary>
        public string Name;

        /// <summary>Il codice set di Arena (es. "DAR"), che NON e' sempre quello di Scryfall ("dom").</summary>
        public string ArenaSetCode;
        public string CollectorNumber;
        public bool IsToken;
        public bool IsDigitalOnly;

        /// <summary>Carta Alchemy ribilanciata: per Scryfall si chiama "A-Nome".</summary>
        public bool IsRebalanced;

        /// <summary>False se Arena non ha trovato la stampa nel suo database.</summary>
        public bool InArenaDatabase = true;
    }

    /// <summary>Quanto e' sicura l'identificazione della stampa di una riga.</summary>
    internal enum MatchPrecision
    {
        /// <summary>Scryfall ha esattamente questa stampa con questo <c>arena_id</c>.</summary>
        ArenaId,

        /// <summary>
        /// Scryfall non conosce l'<c>arena_id</c>, ma ha una e una sola stampa
        /// arena con lo stesso codice set e numero, e lo stesso nome.
        /// </summary>
        SetAndNumber,

        /// <summary>Solo il nome: set e numero non sono stati attribuiti.</summary>
        NameOnly,
    }

    /// <summary>
    /// Una riga del modello intermedio: UNA stampa, con le copie possedute.
    /// I profili CSV leggono solo da qui.
    /// </summary>
    internal sealed class ExportRow
    {
        public int Quantity;

        /// <summary>
        /// Il nome canonico: quello di Scryfall se la stampa e' identificata
        /// (multifaccia con " // "), altrimenti quello di Arena normalizzato.
        /// </summary>
        public string Name;

        /// <summary>Layout di Scryfall ("split", "transform", ...), null se ignoto.</summary>
        public string Layout;

        // Identificazione secondo Scryfall. Tutti null per le righe NameOnly.
        public string SetCode;          // codice Scryfall, minuscolo
        public string SetName;
        public string MtgoSetCode;      // codice MTGO del set, se Scryfall lo dichiara
        public string CollectorNumber;
        public string ScryfallId;       // id della STAMPA
        public string OracleId;         // id della carta astratta: non identifica una stampa

        // Identificazione secondo Arena, sempre presente.
        public readonly List<uint> ArenaIds = new List<uint>();
        public string ArenaSetCode;
        public string ArenaCollectorNumber;

        public MatchPrecision Precision;
        public bool DigitalOnly;
        public bool Rebalanced;

        /// <summary>Perche' la riga e' NameOnly, per il riepilogo. Null altrimenti.</summary>
        public string Note;

        /// <summary>
        /// Il nome della faccia anteriore, per i siti che scrivono solo quella.
        /// Le split e le aftermath restano intere: le due meta' stanno sulla
        /// stessa faccia.
        /// </summary>
        public string FrontFaceName
        {
            get
            {
                if (string.IsNullOrEmpty(Name)) return Name;
                int sep = Name.IndexOf(" // ", StringComparison.Ordinal);
                if (sep < 0) return Name;
                if (Layout == "split" || Layout == "aftermath") return Name;
                // Layout ignoto (riga NameOnly): non sappiamo se e' split, si
                // lascia il nome intero invece di indovinare.
                if (Layout == null) return Name;
                return Name.Substring(0, sep);
            }
        }
    }

    /// <summary>Una voce dell'inventario che non e' entrata nel modello, col motivo.</summary>
    internal sealed class SkippedCard
    {
        public uint GrpId;
        public int Count;
        public string Name;
        public string Reason;
    }

    /// <summary>Il modello completo: le righe, piu' tutto cio' che non e' diventato una riga.</summary>
    internal sealed class CollectionExportModel
    {
        public readonly List<ExportRow> Rows = new List<ExportRow>();
        public readonly List<SkippedCard> Skipped = new List<SkippedCard>();

        /// <summary>Voci con copie a zero o negative: non sono carte possedute, si contano e basta.</summary>
        public int NonPositiveEntries;

        public bool HadScryfallIndex;

        public int CopiesIn;        // copie lette dall'inventario (solo quantita' positive)

        public int CopiesExported
        {
            get { int n = 0; foreach (var r in Rows) n += r.Quantity; return n; }
        }

        public int CopiesSkipped
        {
            get { int n = 0; foreach (var s in Skipped) n += s.Count; return n; }
        }

        public int CopiesWith(MatchPrecision p)
        {
            int n = 0;
            foreach (var r in Rows) if (r.Precision == p) n += r.Quantity;
            return n;
        }

        public int CopiesDigitalOnly
        {
            get { int n = 0; foreach (var r in Rows) if (r.DigitalOnly) n += r.Quantity; return n; }
        }
    }
}
