using System;
using System.Collections.Generic;
using Wotc.Mtgo.Gre.External.Messaging;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Trasforma la sequenza grezza di messaggi di un replay in una lista di
    /// **passi gia' pronti da disegnare**.
    ///
    /// Perche' qui e non nel browser: sul replay vero del 2026-09-20 ci sono
    /// **165 messaggi di stato su 244**, e quasi tutti sono *differenze* —
    /// il flusso non porta lo stato del campo a ogni istante, porta una
    /// fotografia iniziale e poi solo cio' che cambia. Chi vuole mostrare il
    /// campo al passo N deve applicare tutte le differenze fino a N. Farlo in
    /// JavaScript vorrebbe dire portarsi nel browser un lettore protobuf piu'
    /// lo schema di Arena; qui il lettore c'e' gia' e c'e' il database delle
    /// carte, quindi i nomi si risolvono una volta sola.
    ///
    /// **Questo file non tocca Unity ne' il log di BepInEx**, di proposito: e'
    /// pura trasformazione di dati, cosi' si puo' provare su un file vero fuori
    /// dal gioco. Gli avvisi finiscono in <see cref="ReplayDocument.Warnings"/>,
    /// non in una chiamata di log.
    /// </summary>
    internal static class ReplayReconstructor
    {
        // ---------------- modello del risultato ----------------

        internal sealed class CardInfo
        {
            public uint GrpId;
            public string Name;      // risolto da fuori; null se non risolvibile
            public string Colors;    // "WG", "" per incolore

            // La riga dei tipi arriva **gia' divisa in tre**, non come
            // "Basic Land - Forest". Chi disegna il campo separa creature,
            // terre, incantesimi e planeswalker guardando i tipi principali:
            // se ricevesse una stringa sola dovrebbe rismontarla, e una
            // sottostringa "Land" dentro "Landfall" basterebbe a sbagliare.
            public string[] SuperTypes = Empty;
            public string[] CoreTypes = Empty;
            public string[] Subtypes = Empty;

            private static readonly string[] Empty = new string[0];
        }

        internal sealed class SeatState
        {
            public uint Seat;
            public int Life;
            public int Hand;
            public int Graveyard;
            public int Exile;
            public int Library;
        }

        /// <summary>
        /// Dove sta un oggetto in quel passo.
        ///
        /// **La libreria non c'e'**, di proposito: il flusso elenca i suoi
        /// identificativi ma non manda gli oggetti, quindi sarebbe una fila di
        /// cinquanta dorsi tutti uguali. Il numero dice la stessa cosa in un
        /// carattere.
        /// </summary>
        internal enum BoardZone
        {
            Battlefield = 0,
            Stack = 1,
            Hand = 2,
            Graveyard = 3,
            Exile = 4,
        }

        /// <summary>Un oggetto in una zona visibile, com'era in quel passo.</summary>
        internal sealed class BoardEntry
        {
            public uint InstanceId;
            /// <summary>0 quando la carta c'e' ma non sappiamo quale: una mano altrui.</summary>
            public uint GrpId;
            public BoardZone Zone;
            public uint Seat;         // chi lo controlla
            public bool Tapped;
            public int Power;
            public int Toughness;
            public int Damage;
            public bool IsAbility;    // un'abilita' sullo stack, non una carta
            public bool IsToken;
            public bool SummoningSick;
            public bool Attacking;
            public bool Blocking;
        }

        internal sealed class ReplayStep
        {
            public int Index;
            public uint GameNumber;
            public uint Turn;
            public uint ActiveSeat;
            public string Phase;
            public string Step;
            public List<string> Events = new List<string>();
            public List<SeatState> Seats = new List<SeatState>();
            public List<BoardEntry> Board = new List<BoardEntry>();
        }

        internal sealed class ReplayDocument
        {
            public uint LocalSeat;
            public Dictionary<uint, CardInfo> Cards = new Dictionary<uint, CardInfo>();
            public List<ReplayStep> Steps = new List<ReplayStep>();
            public List<string> Warnings = new List<string>();

            /// <summary>Quanti messaggi di stato sono stati applicati.</summary>
            public int StateMessages;
            public int FullStates;
        }

        /// <summary>
        /// Come si chiama un giocatore. Il replay conosce solo i **posti**
        /// (1 e 2): i nomi veri stanno nel record del match, non nel flusso.
        /// </summary>
        internal delegate string SeatNamer(uint seat);

        /// <summary>
        /// Come si chiama una carta. Passata da fuori perche' il database delle
        /// carte vive nel Pantry di Arena: tenerlo fuori da qui e' cio' che
        /// permette di provare la ricostruzione su un file vero senza il gioco.
        /// Puo' restituire null: allora si mostra il numero.
        /// </summary>
        internal delegate string CardNamer(uint grpId);

        // ---------------- ricostruzione ----------------

        /// <summary>
        /// <paramref name="seatName"/> puo' essere null: in quel caso i
        /// giocatori si chiamano "Player 1" e "Player 2".
        /// </summary>
        public static ReplayDocument Build(IList<GREToClientMessage> messages,
                                           SeatNamer seatName = null,
                                           CardNamer cardName = null)
        {
            var doc = new ReplayDocument();
            if (messages == null || messages.Count == 0)
            {
                doc.Warnings.Add("nessun messaggio nel file");
                return doc;
            }

            // Stato corrente, ricostruito applicando le differenze.
            var objects = new Dictionary<uint, GameObjectInfo>();
            var zones = new Dictionary<uint, ZoneInfo>();
            var players = new Dictionary<uint, PlayerInfo>();
            var turn = new TurnInfo();
            uint gameNumber = 0;

            // Chi e' chi, per tutto il file: un oggetto puo' essere nominato in
            // un'annotazione DOPO essere sparito dal campo (una creatura
            // distrutta), quindi questa tabella non si svuota mai.
            var grpOfInstance = new Dictionary<uint, uint>();
            var typeOfInstance = new Dictionary<uint, GameObjectType>();
            var sourceOfInstance = new Dictionary<uint, uint>();   // abilita' -> carta che la genera
            var seatOfInstance = new Dictionary<uint, uint>();     // chi lo controlla
            var attacking = new HashSet<uint>();
            var blocking = new HashSet<uint>();

            var pending = new List<string>();   // eventi in attesa del prossimo passo

            foreach (var msg in messages)
            {
                if (msg == null) continue;

                // Il nostro posto: e' il solo modo che il file ha di dirlo.
                // Stessa lettura che fa MatchRecorder in diretta.
                if (doc.LocalSeat == 0 && msg.SystemSeatIds != null && msg.SystemSeatIds.Count == 1)
                    doc.LocalSeat = msg.SystemSeatIds[0];

                var gsm = msg.GameStateMessage;
                if (gsm == null) continue;

                doc.StateMessages++;

                // Una fotografia completa ricomincia da zero. Senza questo, gli
                // oggetti della partita precedente resterebbero sul campo della
                // successiva: in un match al meglio di tre il file e' uno solo.
                if (gsm.Type == GameStateType.Full)
                {
                    doc.FullStates++;
                    objects.Clear();
                    zones.Clear();
                    players.Clear();
                    attacking.Clear();
                    blocking.Clear();
                    turn = new TurnInfo();
                }

                if (gsm.GameInfo != null && gsm.GameInfo.GameNumber != 0)
                    gameNumber = gsm.GameInfo.GameNumber;

                // --- applicazione della differenza ---

                foreach (var z in gsm.Zones)
                    if (z != null) zones[z.ZoneId] = z;

                foreach (var p in gsm.Players)
                    if (p != null) players[p.SystemSeatNumber] = p;

                foreach (var o in gsm.GameObjects)
                {
                    if (o == null) continue;
                    objects[o.InstanceId] = o;
                    grpOfInstance[o.InstanceId] = o.GrpId;
                    typeOfInstance[o.InstanceId] = o.Type;
                    if (o.ObjectSourceGrpId != 0) sourceOfInstance[o.InstanceId] = o.ObjectSourceGrpId;

                    uint seat = o.ControllerSeatId != 0 ? o.ControllerSeatId : o.OwnerSeatId;
                    if (seat != 0) seatOfInstance[o.InstanceId] = seat;

                    // Attacchi e blocchi non hanno un'annotazione che li
                    // racconti: stanno nello stato dell'oggetto. Si guarda il
                    // passaggio, non il valore, altrimenti la stessa creatura
                    // "attacca" a ogni messaggio finche' dura il combattimento.
                    if (o.AttackState != AttackState.None)
                    {
                        if (attacking.Add(o.InstanceId))
                        {
                            string who = Label(o.InstanceId, grpOfInstance, typeOfInstance, sourceOfInstance, doc);
                            uint target = o.AttackInfo != null ? o.AttackInfo.TargetId : 0;
                            string at = target != 0 && !players.ContainsKey(target)
                                ? " " + Label(target, grpOfInstance, typeOfInstance, sourceOfInstance, doc)
                                : "";
                            pending.Add($"{who} attacks{at}");
                        }
                    }
                    else attacking.Remove(o.InstanceId);

                    // **Blocked e Unblocked descrivono l'ATTACCANTE**, non un
                    // bloccante: sono lo stato di chi e' stato bloccato o e'
                    // passato. Sul replay vero la stessa creatura usciva prima
                    // come "attacca" e subito dopo come "blocca", che in Magic
                    // non puo' succedere.
                    if (o.BlockState == BlockState.Blocking || o.BlockState == BlockState.Declared)
                    {
                        if (blocking.Add(o.InstanceId))
                        {
                            string who = Label(o.InstanceId, grpOfInstance, typeOfInstance, sourceOfInstance, doc);
                            string what = "";
                            if (o.BlockInfo != null && o.BlockInfo.AttackerIds.Count > 0)
                                what = " " + Label(o.BlockInfo.AttackerIds[0], grpOfInstance, typeOfInstance, sourceOfInstance, doc);
                            pending.Add($"{who} blocks{what}");
                        }
                    }
                    else blocking.Remove(o.InstanceId);

                    Remember(doc, o, cardName);
                }

                foreach (var gone in gsm.DiffDeletedInstanceIds)
                {
                    objects.Remove(gone);
                    attacking.Remove(gone);
                    blocking.Remove(gone);
                }

                // **TurnInfo si fonde, non si sostituisce.** Verificato sul log
                // del 2026-09-20: in una differenza arriva parziale, per esempio
                // { activePlayer, decisionPlayer } senza fase ne' numero di
                // turno. Sostituirlo azzererebbe il turno a ogni passaggio di
                // priorita'. MergeFrom di proto3 copia solo i campi valorizzati,
                // che e' esattamente la semantica di una differenza.
                if (gsm.TurnInfo != null)
                {
                    // Ma lo **step** va azzerato quando arriva una fase nuova.
                    // Main1 non ha step: il messaggio porta Phase=Main1 e
                    // Step=None, e la fusione (che ignora i campi a zero)
                    // lascerebbe appiccicato lo step precedente. Sul replay del
                    // 2026-09-20 usciva "Main1/Upkeep", che non esiste.
                    bool newPhase = gsm.TurnInfo.Phase != Phase.None && gsm.TurnInfo.Phase != turn.Phase;
                    turn.MergeFrom(gsm.TurnInfo);
                    if (newPhase) turn.Step = gsm.TurnInfo.Step;
                }

                // --- lettura delle annotazioni ---

                foreach (var ann in gsm.Annotations)
                {
                    if (ann == null) continue;
                    foreach (var type in ann.Type)
                    {
                        // La rinumerazione va seguita SEMPRE, anche quando non
                        // si racconta: una carta che cambia zona cambia
                        // identificativo, e senza questo la stessa carta sembra
                        // sparire e ricomparire come un'altra.
                        if (type == AnnotationType.ObjectIdChanged)
                        {
                            uint orig = (uint)Detail(ann, "orig_id");
                            uint neu = (uint)Detail(ann, "new_id");
                            if (orig != 0 && neu != 0)
                            {
                                uint grp;
                                if (grpOfInstance.TryGetValue(orig, out grp) && !grpOfInstance.ContainsKey(neu))
                                    grpOfInstance[neu] = grp;
                                GameObjectType t;
                                if (typeOfInstance.TryGetValue(orig, out t) && !typeOfInstance.ContainsKey(neu))
                                    typeOfInstance[neu] = t;
                            }
                            continue;
                        }

                        string line = Narrate(type, ann, grpOfInstance, typeOfInstance,
                                              sourceOfInstance, seatOfInstance, players, doc, seatName);
                        if (line != null) pending.Add(line);
                    }
                }

                // --- si emette un passo solo se c'e' qualcosa da dire ---
                //
                // Un passo per messaggio darebbe 165 fotografie quasi tutte
                // identiche per una partita di due minuti: il cursore
                // scorrerebbe su schermate uguali. Il passo nasce dagli eventi.
                if (pending.Count == 0) continue;

                var step = new ReplayStep
                {
                    Index = doc.Steps.Count,
                    GameNumber = gameNumber,
                    Turn = turn.TurnNumber,
                    ActiveSeat = turn.ActivePlayer,
                    Phase = Pretty(turn.Phase.ToString()),
                    Step = Pretty(turn.Step.ToString()),
                };
                step.Events.AddRange(pending);
                pending.Clear();

                Snapshot(step, objects, zones, players, typeOfInstance);
                doc.Steps.Add(step);
            }

            // Gli eventi rimasti dopo l'ultimo stato — la fine della partita
            // arriva spesso in coda — non vanno persi.
            if (pending.Count > 0)
            {
                var step = new ReplayStep
                {
                    Index = doc.Steps.Count,
                    GameNumber = gameNumber,
                    Turn = turn.TurnNumber,
                    ActiveSeat = turn.ActivePlayer,
                    Phase = Pretty(turn.Phase.ToString()),
                    Step = Pretty(turn.Step.ToString()),
                };
                step.Events.AddRange(pending);
                Snapshot(step, objects, zones, players, typeOfInstance);
                doc.Steps.Add(step);
            }

            if (doc.Steps.Count == 0)
                doc.Warnings.Add($"{doc.StateMessages} messaggi di stato ma nessun evento riconosciuto");

            return doc;
        }

        // ---------------- fotografia del campo ----------------

        private static void Snapshot(ReplayStep step,
                                     Dictionary<uint, GameObjectInfo> objects,
                                     Dictionary<uint, ZoneInfo> zones,
                                     Dictionary<uint, PlayerInfo> players,
                                     Dictionary<uint, GameObjectType> typeOfInstance)
        {
            // Il campo si legge dalle **zone**, non dagli oggetti. Un oggetto
            // uscito di scena puo' restare nella tabella (serve per dargli un
            // nome nelle annotazioni successive) ma non deve piu' comparire
            // sul campo: la zona e' l'unica a sapere chi c'e' davvero adesso.
            var seats = new Dictionary<uint, SeatState>();

            foreach (var kv in players)
            {
                var p = kv.Value;
                seats[kv.Key] = new SeatState { Seat = kv.Key, Life = p != null ? p.LifeTotal : 0 };
            }

            foreach (var zone in zones.Values)
            {
                if (zone == null) continue;
                int count = zone.ObjectInstanceIds.Count;

                // Lo stack e il limbo non hanno proprietario: senza questo
                // controllo nasceva un "giocatore 0" con zero punti vita
                // accanto ai due veri. L'esilio condiviso resta perche' i suoi
                // oggetti dicono da se' di chi sono.
                bool shared = zone.OwnerSeatId == 0;
                if (shared
                    && zone.Type != ZoneType.Battlefield
                    && zone.Type != ZoneType.Stack
                    && zone.Type != ZoneType.Exile) continue;

                switch (zone.Type)
                {
                    case ZoneType.Hand:
                        Seat(seats, zone.OwnerSeatId).Hand = count;
                        // **La nostra mano la conosciamo, quella altrui no**, e
                        // la differenza la dice il flusso, non una regola
                        // nostra: per le carte che possiamo vedere arriva un
                        // GameObject col suo GrpId, per le altre arriva solo
                        // l'identificativo. Misurato sul replay del
                        // 2026-09-20: 7 carte su 7 note nella nostra mano, 0 su
                        // 4 in quella dell'avversario. Quelle ignote entrano lo
                        // stesso, con GrpId 0: sapere QUANTE carte aveva in
                        // mano in quel momento e' meta' della storia.
                        AddZone(step, objects, typeOfInstance, zone, BoardZone.Hand, zone.OwnerSeatId);
                        break;

                    case ZoneType.Graveyard:
                        Seat(seats, zone.OwnerSeatId).Graveyard = count;
                        AddZone(step, objects, typeOfInstance, zone, BoardZone.Graveyard, zone.OwnerSeatId);
                        break;

                    case ZoneType.Exile:
                        if (!shared) Seat(seats, zone.OwnerSeatId).Exile = count;
                        AddZone(step, objects, typeOfInstance, zone, BoardZone.Exile, zone.OwnerSeatId);
                        break;

                    case ZoneType.Library:
                        Seat(seats, zone.OwnerSeatId).Library = count;
                        break;

                    case ZoneType.Battlefield:
                        AddZone(step, objects, typeOfInstance, zone, BoardZone.Battlefield, 0);
                        break;

                    case ZoneType.Stack:
                        AddZone(step, objects, typeOfInstance, zone, BoardZone.Stack, 0);
                        break;
                }
            }

            foreach (var s in seats.Values) step.Seats.Add(s);
            step.Seats.Sort((a, b) => a.Seat.CompareTo(b.Seat));
        }

        /// <summary>
        /// Mette in <paramref name="step"/> gli oggetti di una zona.
        ///
        /// <paramref name="fallbackSeat"/> vale per le zone che appartengono a
        /// un giocatore (mano, cimitero): li' l'oggetto puo' mancare del tutto
        /// — una carta coperta — e senza il proprietario della zona finirebbe
        /// nel posto sbagliato. Sul campo e sullo stack e' 0, perche' li'
        /// conta chi controlla, non chi possiede.
        /// </summary>
        private static void AddZone(ReplayStep step,
                                    Dictionary<uint, GameObjectInfo> objects,
                                    Dictionary<uint, GameObjectType> typeOfInstance,
                                    ZoneInfo zone,
                                    BoardZone kind,
                                    uint fallbackSeat)
        {
            foreach (uint id in zone.ObjectInstanceIds)
            {
                GameObjectInfo o;
                if (!objects.TryGetValue(id, out o) || o == null)
                {
                    // Una carta di cui conosciamo l'esistenza e non l'identita'.
                    // Sul campo e sullo stack non capita, sono zone pubbliche:
                    // se capitasse non sapremmo nemmeno di chi e', e una carta
                    // senza posto e' peggio di una carta in meno.
                    if (fallbackSeat == 0) continue;
                    step.Board.Add(new BoardEntry
                    {
                        InstanceId = id,
                        GrpId = 0,
                        Zone = kind,
                        Seat = fallbackSeat,
                    });
                    continue;
                }

                GameObjectType t;
                if (!typeOfInstance.TryGetValue(id, out t)) t = o.Type;

                uint seat = o.ControllerSeatId != 0 ? o.ControllerSeatId : o.OwnerSeatId;
                if (seat == 0) seat = fallbackSeat;

                step.Board.Add(new BoardEntry
                {
                    InstanceId = id,
                    GrpId = o.GrpId,
                    Zone = kind,
                    Seat = seat,
                    Tapped = o.IsTapped,
                    Power = o.Power != null ? o.Power.Value : 0,
                    Toughness = o.Toughness != null ? o.Toughness.Value : 0,
                    Damage = (int)o.Damage,
                    IsAbility = IsAbility(t),
                    IsToken = t == GameObjectType.Token,
                    SummoningSick = o.HasSummoningSickness,
                    Attacking = o.AttackState != AttackState.None,
                    Blocking = o.BlockState == BlockState.Blocking || o.BlockState == BlockState.Declared,
                });
            }
        }

        private static SeatState Seat(Dictionary<uint, SeatState> seats, uint id)
        {
            SeatState s;
            if (!seats.TryGetValue(id, out s))
            {
                s = new SeatState { Seat = id };
                seats[id] = s;
            }
            return s;
        }

        // ---------------- racconto ----------------

        private static string Narrate(AnnotationType type,
                                      AnnotationInfo ann,
                                      Dictionary<uint, uint> grpOfInstance,
                                      Dictionary<uint, GameObjectType> typeOfInstance,
                                      Dictionary<uint, uint> sourceOfInstance,
                                      Dictionary<uint, uint> seatOfInstance,
                                      Dictionary<uint, PlayerInfo> players,
                                      ReplayDocument doc,
                                      SeatNamer seatName)
        {
            switch (type)
            {
                case AnnotationType.NewTurnStarted:
                {
                    uint seat = ann.AffectedIds.Count > 0 ? ann.AffectedIds[0] : ann.AffectorId;
                    return "@turn " + Player(seat, seatName);
                }

                case AnnotationType.ZoneTransfer:
                {
                    // Le chiavi sono quelle viste nel log vero, non dedotte:
                    // zone_src / zone_dest (int32) e category (stringa).
                    string category = DetailString(ann, "category");
                    if (string.IsNullOrEmpty(category)) return null;

                    uint id = ann.AffectedIds.Count > 0 ? ann.AffectedIds[0] : 0;
                    string what = Label(id, grpOfInstance, typeOfInstance, sourceOfInstance, doc);

                    // **AffectorId non e' il giocatore.** A volte porta il posto,
                    // spesso e' assente (0) o e' l'oggetto che ha causato la
                    // cosa: sul replay vero usciva "Someone casts ..." quasi
                    // sempre. Chi ha giocato la carta lo dice la carta stessa,
                    // col posto di chi la controlla.
                    uint actor;
                    if (!seatOfInstance.TryGetValue(id, out actor))
                        actor = players.ContainsKey(ann.AffectorId) ? ann.AffectorId : 0;
                    string who = Player(actor, seatName);

                    switch (category)
                    {
                        case "CastSpell":  return $"{who} casts {what}";
                        case "PlayLand":   return $"{who} plays {what}";
                        case "Draw":       return null;   // una pescata per turno per ciascuno: rumore
                        case "Resolve":    return null;   // gia' detto da ResolutionStart
                        case "Exile":      return $"{what} is exiled";
                        case "Discard":    return $"{who} discards {what}";
                        case "SBA_Damage": return $"{what} dies";
                        case "Destroy":    return $"{what} is destroyed";
                        default:
                            if (category.StartsWith("SBA_")) return $"{what} leaves the battlefield ({category.Substring(4)})";
                            return null;
                    }
                }

                case AnnotationType.ResolutionStart:
                {
                    uint id = ann.AffectedIds.Count > 0 ? ann.AffectedIds[0] : ann.AffectorId;
                    string what = Label(id, grpOfInstance, typeOfInstance, sourceOfInstance, doc);
                    return what == null ? null : $"{what} resolves";
                }

                case AnnotationType.ModifiedLife:
                {
                    int delta = Detail(ann, "life");
                    if (delta == 0) return null;
                    uint seat = ann.AffectedIds.Count > 0 ? ann.AffectedIds[0] : 0;
                    PlayerInfo p;
                    string now = players.TryGetValue(seat, out p) && p != null ? $" ({p.LifeTotal})" : "";
                    string sign = delta > 0 ? "+" : "";
                    return $"{Player(seat, seatName)} {sign}{delta} life{now}";
                }

                case AnnotationType.DamageDealt:
                {
                    int dmg = Detail(ann, "damage");
                    if (dmg == 0) return null;
                    uint target = ann.AffectedIds.Count > 0 ? ann.AffectedIds[0] : 0;
                    // Un bersaglio che e' un posto e non un oggetto: il danno al
                    // giocatore lo racconta gia' ModifiedLife, qui si tace.
                    if (players.ContainsKey(target)) return null;
                    string src = Label(ann.AffectorId, grpOfInstance, typeOfInstance, sourceOfInstance, doc);
                    string dst = Label(target, grpOfInstance, typeOfInstance, sourceOfInstance, doc);
                    return $"{src} deals {dmg} to {dst}";
                }

                default:
                    return null;
            }
        }

        private static string Player(uint seat, SeatNamer seatName)
        {
            if (seat == 0) return "Someone";
            if (seatName != null)
            {
                string n = seatName(seat);
                if (!string.IsNullOrEmpty(n)) return n;
            }
            return "Player " + seat;
        }

        /// <summary>
        /// Come si chiama un oggetto in una riga di racconto.
        ///
        /// **Un'abilita' non e' una carta.** Il suo GrpId vive nello stesso
        /// spazio numerico delle carte e cercarlo nel database ne trova una a
        /// caso: e' l'errore che faceva registrare avversari del colore
        /// sbagliato (MISTAKES.md #33). Quindi il GrpId di un'abilita' non
        /// entra mai nel dizionario delle carte; si nomina la carta che la
        /// genera, se la conosciamo.
        /// </summary>
        private static string Label(uint instanceId,
                                    Dictionary<uint, uint> grpOfInstance,
                                    Dictionary<uint, GameObjectType> typeOfInstance,
                                    Dictionary<uint, uint> sourceOfInstance,
                                    ReplayDocument doc)
        {
            if (instanceId == 0) return "something";

            GameObjectType type;
            bool knownType = typeOfInstance.TryGetValue(instanceId, out type);

            if (knownType && IsAbility(type))
            {
                uint src;
                if (sourceOfInstance.TryGetValue(instanceId, out src) && src != 0)
                    return NameOf(doc, src) + "'s ability";
                return "an ability";
            }

            uint grp;
            if (grpOfInstance.TryGetValue(instanceId, out grp) && grp != 0)
                return NameOf(doc, grp);

            return "something";
        }

        /// <summary>
        /// Il nome gia' risolto, se la carta e' passata di qui. Una carta
        /// nominata in un'annotazione ma mai vista come oggetto non ha nome: si
        /// mostra il numero invece di inventarne uno.
        /// </summary>
        private static string NameOf(ReplayDocument doc, uint grpId)
        {
            CardInfo info;
            if (doc.Cards.TryGetValue(grpId, out info) && info != null && !string.IsNullOrEmpty(info.Name))
                return info.Name;
            return "#" + grpId;
        }

        private static bool IsAbility(GameObjectType type)
        {
            return type == GameObjectType.Ability
                || type == GameObjectType.Emblem
                || type == GameObjectType.TriggerHolder;
        }

        /// <summary>
        /// Mette la carta nel dizionario dei nomi. Una sola volta per GrpId, e
        /// **solo se l'oggetto e' una carta**: vedi <see cref="Label"/>.
        /// </summary>
        private static void Remember(ReplayDocument doc, GameObjectInfo o, CardNamer cardName)
        {
            if (o.GrpId == 0 || IsAbility(o.Type)) return;
            if (doc.Cards.ContainsKey(o.GrpId)) return;

            string name = null;
            if (cardName != null)
            {
                try { name = cardName(o.GrpId); }
                catch { /* un nome mancante e' un fastidio, non un motivo per perdere il replay */ }
            }

            doc.Cards[o.GrpId] = new CardInfo
            {
                GrpId = o.GrpId,
                Name = string.IsNullOrEmpty(name) ? "#" + o.GrpId : name,
                SuperTypes = Names(o.SuperTypes),
                CoreTypes = Names(o.CardTypes),
                Subtypes = Names(o.Subtypes),
                Colors = ColorLetters(o),
            };
        }

        /// <summary>
        /// I nomi di una lista di enum, ripuliti del prefisso
        /// (<c>CardType_Creature</c> -> <c>Creature</c>).
        /// </summary>
        private static string[] Names<T>(Google.Protobuf.Collections.RepeatedField<T> values)
        {
            if (values == null || values.Count == 0) return new string[0];
            var list = new List<string>(values.Count);
            foreach (var v in values)
            {
                string s = Pretty(v.ToString());
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list.ToArray();
        }

        /// <summary>
        /// I colori dichiarati dall'oggetto stesso. Land e Artifact non sono
        /// colori: <c>CardColor</c> li mette nella stessa lista, ma un artefatto
        /// incolore non e' di nessun colore.
        /// </summary>
        private static string ColorLetters(GameObjectInfo o)
        {
            string s = "";
            foreach (var c in o.Color)
            {
                switch (c)
                {
                    case CardColor.White: if (!s.Contains("W")) s += "W"; break;
                    case CardColor.Blue:  if (!s.Contains("U")) s += "U"; break;
                    case CardColor.Black: if (!s.Contains("B")) s += "B"; break;
                    case CardColor.Red:   if (!s.Contains("R")) s += "R"; break;
                    case CardColor.Green: if (!s.Contains("G")) s += "G"; break;
                }
            }
            return s;
        }

        // ---------------- dettagli delle annotazioni ----------------

        private static int Detail(AnnotationInfo ann, string key)
        {
            foreach (var d in ann.Details)
            {
                if (d == null || d.Key != key) continue;
                if (d.ValueInt32.Count > 0) return d.ValueInt32[0];
                if (d.ValueUint32.Count > 0) return (int)d.ValueUint32[0];
            }
            return 0;
        }

        private static string DetailString(AnnotationInfo ann, string key)
        {
            foreach (var d in ann.Details)
            {
                if (d == null || d.Key != key) continue;
                if (d.ValueString.Count > 0) return d.ValueString[0];
            }
            return null;
        }

        /// <summary>
        /// I nomi degli enum di Arena arrivano come <c>Main1</c>,
        /// <c>DeclareAttack</c>, <c>CardType_Creature</c>: si tolgono i prefissi
        /// e si separano le parole attaccate.
        /// </summary>
        private static string Pretty(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            int us = raw.IndexOf('_');
            if (us >= 0 && us + 1 < raw.Length) raw = raw.Substring(us + 1);
            if (raw == "None") return "";

            var sb = new System.Text.StringBuilder(raw.Length + 4);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(raw[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
