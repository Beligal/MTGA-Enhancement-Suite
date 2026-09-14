using System;
using System.Collections.Generic;
using GreClient.Rules;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Wotc.Mtgo.Gre.External.Messaging;
// Wotc.Mtgo.Gre.External.Messaging ha un suo Color (i colori di mana):
// senza questo alias ogni new Color(...) dell'interfaccia e' ambiguo.
using Color = UnityEngine.Color;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Totali di attacco e difesa durante il combattimento.
    ///
    /// Mostra, mentre si dichiarano gli attaccanti, quanta forza e quanta
    /// costituzione stanno attaccando, e quanta ne ha ancora disponibile chi
    /// difende. Si aggiorna a ogni creatura che si clicca.
    ///
    /// PERCHE' FUNZIONA — i numeri non li calcoliamo noi:
    ///
    /// 1. <c>MtgCardInstance.Power</c> e <c>.Toughness</c> sono i valori
    ///    CORRENTI decisi dal server, potenziamenti e segnalini gia' dentro.
    ///    (Verificato su partite reali: la stessa creatura passa da 1/1 a 9/9
    ///    a 17/17 nello stesso log.) Non serve leggere il testo delle carte.
    ///
    /// 2. La selezione degli attaccanti NON resta nel client: ogni click manda
    ///    un <c>DeclareAttackersResp</c> e il server risponde con uno stato in
    ///    cui quella creatura ha <c>AttackState.Declared</c>. Quindi ci basta
    ///    leggere lo stato di gioco: <c>Declared</c> = selezionata ma non
    ///    ancora confermata, <c>Attacking</c> = dopo il pulsante di conferma.
    ///    Il prezzo e' il tempo di rete fra il click e l'aggiornamento.
    ///
    /// 3. Il campo di battaglia non e' redatto: forza, costituzione e stato di
    ///    tap delle creature avversarie sono esatti.
    ///
    /// Dettagli e conteggi in <c>research/arena-internals/combat-state.md</c>.
    ///
    /// COSA SA E COSA NON SA — le prime due righe sono somme grezze: "quanta
    /// ciccia c'e' da una parte e dall'altra", niente di piu'. L'ultima riga
    /// (stopped / through) invece tiene conto di **travolgere** e **contatto
    /// letale**, letti dalle abilita' della carta: sono i due che cambiano
    /// davvero il conto. Il riconoscimento e l'aritmetica sono stati verificati
    /// in partita il 2026-09-11 — un 4 con travolgere bloccato da un
    /// assorbimento di 1 manda 3 al giocatore.
    ///
    /// Restano fuori: attacco improvviso e doppio attacco (cambiano *quando* il
    /// danno arriva, non quanto), e l'evasione — volare, minacciare — che qui
    /// non serve, perche' il conto parte dai blocchi gia' assegnati e non da
    /// quelli possibili.
    /// </summary>
    internal static class CombatTotals
    {
        // Ogni quanto ricercare GameManager quando non ne abbiamo uno valido.
        // FindObjectOfType costa, e Tick() gira a ogni frame anche fuori dal
        // duello: senza questo intervallo pagheremmo la ricerca 60 volte al
        // secondo nel menu principale, dove non la troverebbe mai.
        private const float GameManagerRescanInterval = 1f;

        private static GameManager _gameManager;
        private static float _nextGameManagerScan;
        private static bool _gameOver;

        private static GameObject _canvasRoot;
        private static RectTransform _panelRect;
        private static CanvasGroup _group;
        private static RectTransform _ruleRect;
        private static TextMeshProUGUI _label;
        private static string _lastText;
        private static bool _locked;

        private const float PanelWidth = 236f;
        private const float PanelPaddingY = 18f;

        // TAVOLOZZA — non e' quella del deck builder.
        //
        // Il pannello dei mazzi vive su fondo blu notte e li' il blu acciaio
        // ci sta. Sopra il tavolo da gioco no: l'interfaccia di Arena in
        // duello e' fatta di neutri caldi e scuri, e un accento azzurro
        // saltava fuori come un adesivo. Qui si resta sui grigi caldi, e
        // l'unico colore e' l'arancio del danno che passa — il numero per cui
        // il cartellino esiste.
        private static readonly Color PanelColor  = new Color(0.055f, 0.05f, 0.045f, 1f);
        private static readonly Color BorderColor = new Color(0.62f, 0.56f, 0.44f, 0.30f);
        private static readonly Color RuleColor   = new Color(0.62f, 0.56f, 0.44f, 0.22f);
        private static readonly Color TextColor   = new Color(0.93f, 0.91f, 0.86f, 1f);

        // Le stesse, in esadecimale: dentro al testo servono i tag rich text.
        private const string HexAccent = "C9B182";   // ottone tenue, per l'intestazione
        private const string HexDim    = "8C8478";   // etichette
        private const string HexDanger = "E08A5A";   // il danno che passa

        /// <summary>
        /// Chiamata a ogni frame da <c>PluginBehaviour.Update</c>. Fuori dal
        /// combattimento nasconde tutto e non fa altro.
        /// </summary>
        public static void Tick()
        {
            if (!ModSettings.Instance.CombatTotals) { Hide(); return; }

            var gm = GameManagerOrNull();
            if (gm == null) { Hide(); return; }

            // DUE STATI, DUE MESTIERI.
            //
            // `latest` e' dove sta il server: e' li' che i dati di
            // combattimento esistono, e li' calcoliamo.
            //
            // `shown` e' dove e' arrivata l'animazione, cioe' cosa ha davanti
            // agli occhi chi gioca: e' li' che decidiamo se il cartellino si
            // vede. Senza questa distinzione il pannello spariva sul piu'
            // bello: misurato sul log del 2026-09-11, fra l'ultimo stato di
            // DeclareBlock e l'uscita da Phase.Combat passano **0,0 secondi**
            // su quattro combattimenti su cinque — il server chiude danno e
            // fine combattimento nella stessa raffica, mentre a schermo le
            // creature si stanno ancora scontrando.
            var latest = gm.LatestGameState;
            var shown = gm.CurrentGameState ?? latest;
            if (latest == null || shown == null) { Hide(); return; }

            // Partita finita: si sparisce, e non si torna piu'.
            //
            // Serve un controllo a parte perche' una partita che finisce **con
            // il danno da combattimento** non esce mai da Phase.Combat: lo
            // stato si ferma li', arriva la schermata di vittoria o sconfitta,
            // e il cartellino resterebbe appeso sopra per sempre.
            //
            // Il flag e' appiccicoso invece di essere ricontrollato a ogni
            // frame: `GameInfo` viene sostituito per intero come tutto il resto
            // (vedi il commento sul congelamento), quindi un messaggio
            // successivo senza `stage` riporterebbe il valore a None e il
            // pannello tornerebbe a farsi vedere per un attimo.
            //
            // A spegnerlo e' solo l'inizio di una partita nuova, NON un
            // GameManager nuovo: fra gara 1 e gara 2 di un match al meglio dei
            // tre il GameManager puo' sopravvivere, e legarci l'azzeramento
            // avrebbe tenuto il cartellino spento per tutta la gara dopo.
            if (latest.Stage == GameStage.GameOver) _gameOver = true;
            else if (latest.Stage == GameStage.Play || latest.Stage == GameStage.Start) _gameOver = false;

            if (_gameOver) { Unfreeze(); Hide(); return; }

            if (shown.CurrentPhase != Phase.Combat) { Unfreeze(); Hide(); return; }

            var text = CurrentText(shown, latest);
            if (text == null) { Hide(); return; }

            Show(text);
        }

        // ------------------------------------------------------------------
        // Il congelamento allo step del danno
        // ------------------------------------------------------------------
        //
        // PERCHE' ESISTE — dallo step del danno in poi i numeri NON sono piu'
        // ricalcolabili, perche' il dato sparisce dallo stato.
        //
        // Il server manda diff, e il client li fonde con
        // GreInterface.MergeRepeatedFields, che per ogni oggetto fa
        // `left[i] = val`: **sostituisce l'oggetto intero**, non fonde i campi.
        // Allo step del danno l'attaccante viene rimandato con i soli
        // `isTapped` e `damage` — senza `attackState` e senza `attackInfo` — e
        // quindi nello stato del client smette di risultare un attaccante.
        // Poco dopo tocca al bloccante perdere `blockInfo`.
        //
        // Verificato sul log della partita del 2026-09-11 rifacendo la fusione
        // fuori dal gioco: allo stato 334 (DeclareBlock) gli attaccanti sono
        // [(349, forza 3)] e i blocchi {364 -> 349}; allo stato 335
        // (CombatDamage) la lista degli attaccanti e' vuota mentre il blocco
        // c'e' ancora. Quale dei due sparisca per primo cambia da partita a
        // partita: percio' il sintomo poteva essere sia "tutto a zero" sia
        // "come se non ci fossero stati blocchi".
        //
        // Quindi: si calcola finche' si dichiara, e da li' in poi si mostra
        // l'ultimo conto buono, dichiarandolo. Meglio un numero fermo e vero
        // che uno vivo e falso.
        private static string _frozenText;
        private static uint _frozenTurn;

        private static string CurrentText(MtgGameState shown, MtgGameState latest)
        {
            // Si dichiara finche' lo dice lo stato VERO: e' li' che i dati
            // esistono, e un'animazione in ritardo non deve farci perdere il
            // momento in cui si puo' ancora calcolare.
            var step = latest.CurrentStep;
            bool declaring = step == Step.BeginCombat
                          || step == Step.DeclareAttack
                          || step == Step.DeclareBlock;

            if (declaring)
            {
                var text = Build(latest);
                if (text != null)
                {
                    _frozenText = text;
                    _frozenTurn = latest.GameWideTurn;
                }
                _locked = false;
                return text;
            }

            // Il turno fa da scadenza: un conto congelato non deve sopravvivere
            // al combattimento che lo ha prodotto. Il confronto e' con il turno
            // MOSTRATO, non con quello del server: e' quello il combattimento
            // che chi gioca sta guardando.
            if (_frozenText == null || _frozenTurn != shown.GameWideTurn) return null;

            // Che i numeri siano fermi si dice smorzando il pannello, non
            // scrivendolo: una riga di testo in piu' sopra il tavolo da gioco
            // e' rumore, e quella scritta sembrava una spia di debug.
            _locked = true;
            return _frozenText;
        }

        private static void Unfreeze()
        {
            _frozenText = null;
            _frozenTurn = 0;
        }

        /// <summary>F8: accende e spegne la funzione, e ricorda la scelta.</summary>
        public static void Toggle()
        {
            var on = !ModSettings.Instance.CombatTotals;
            ModSettings.Instance.CombatTotals = on;
            ModSettings.Instance.Save();
            if (!on) Hide();
            Toast.Info(on ? "Combat totals on" : "Combat totals off");
        }

        /// <summary>
        /// Nasconde subito il cartellino, lasciando in piedi l'overlay. Serve
        /// all'interruttore del pannello Impostazioni: spegnendolo con una
        /// partita aperta dietro, deve sparire adesso e non al prossimo
        /// combattimento.
        /// </summary>
        public static void HideNow()
        {
            Unfreeze();
            Hide();
        }

        /// <summary>Uscita sempre-pulita, da <c>PluginBehaviour.OnDestroy</c>.</summary>
        public static void ForceHide()
        {
            Unfreeze();
            if (_canvasRoot == null) return;
            UnityEngine.Object.Destroy(_canvasRoot);
            _canvasRoot = null;
            _panelRect = null;
            _group = null;
            _ruleRect = null;
            _label = null;
            _lastText = null;
        }

        // ------------------------------------------------------------------
        // Stato di gioco
        // ------------------------------------------------------------------

        // Il GameManager espone i due stati che ci servono: LatestGameState
        // (dove sta il server) e CurrentGameState (dove e' arrivata
        // l'animazione). Vedi Tick() per chi fa cosa.
        private static GameManager GameManagerOrNull()
        {
            // Il confronto con null su un MonoBehaviour distrutto e' true anche
            // se il riferimento C# esiste ancora: e' cosi' che ci accorgiamo
            // che il duello e' finito e ne ricerchiamo uno nuovo.
            if (_gameManager == null)
            {
                if (Time.unscaledTime < _nextGameManagerScan) return null;
                _nextGameManagerScan = Time.unscaledTime + GameManagerRescanInterval;
                _gameManager = UnityEngine.Object.FindObjectOfType<GameManager>();
                if (_gameManager == null) return null;

                // GameManager nuovo = duello nuovo: ricomincia tutto da capo.
                _gameOver = false;
                Unfreeze();
            }

            return _gameManager;
        }

        private sealed class Totals
        {
            public int Count;
            public int Power;
            public int Toughness;
            // Creature con forza o costituzione variabile (le "*"): il server
            // manda un valore non numerico e DefinedValue resta null. Le
            // contiamo a parte invece di sommare uno zero silenzioso.
            public int Variable;
        }

        private static string Build(MtgGameState state)
        {
            var active = state.ActivePlayer;
            if (active == null || active.InstanceId == 0) return null;

            // LocalPlayer e Opponent non tornano mai null: quando non trovano
            // il giocatore restituiscono un MtgPlayer vuoto con InstanceId 0,
            // e allora le liste del campo escono vuote. Quindi niente
            // NullReference, ma nemmeno un "non lo so": fuori da un duello
            // vero i conti sono tutti zero, ed e' per questo che sopra
            // filtriamo sulla fase.
            var local = state.LocalPlayer;
            bool localIsActive = local != null && local.InstanceId == active.InstanceId;

            var localCards = state.LocalPlayerBattlefieldCards;
            var opponentCards = state.OpponentBattlefieldCards;

            // Gli attaccanti possono stare solo dalla parte di chi e' di turno,
            // ma li sommiamo da entrambe le liste: cosi' il conto regge anche
            // se una creatura cambia controllore a meta' combattimento.
            var attackers = new Totals();
            Accumulate(localCards, attackers, IsAttacking);
            Accumulate(opponentCards, attackers, IsAttacking);

            // Chi difende e' l'altro. "Puo' ancora bloccare" = creatura,
            // stappata, non gia' impegnata ad attaccare.
            var blockers = new Totals();
            Accumulate(localIsActive ? opponentCards : localCards, blockers, CanStillBlock);

            if (attackers.Count == 0 && blockers.Count == 0) return null;

            // Tre colonne allineate con <pos>, che TMP misura in percentuale
            // della larghezza del testo: etichetta a sinistra, numero di
            // creature al centro, forza/costituzione a destra. Senza, i numeri
            // ballano da una riga all'altra secondo la lunghezza delle parole.
            var side = localIsActive ? "you attack" : "you defend";
            var text =
                "<size=74%><color=#" + HexAccent + "><b>COMBAT</b></color>" +
                "<color=#" + HexDim + ">   " + side + "</color></size>\n" +
                Row("Attackers", attackers) + "\n" +
                Row("Blockers", blockers);

            // Ultima riga, solo se qualcuno sta davvero attaccando: quanto
            // danno e' gia' fermato dai blocchi assegnati e quanto arriva
            // ancora in faccia a chi difende. E' il numero che conta davvero,
            // quindi e' l'unico colorato.
            if (attackers.Count > 0)
            {
                int stopped, through;
                SplitDamage(localCards, opponentCards, out stopped, out through);
                text +=
                    "\n<size=86%><color=#" + HexDim + ">stopped " + stopped +
                    "</color></size><pos=52%><color=#" + HexDanger + "><b>" + through +
                    "</b></color><size=74%><color=#" + HexDim + "> through</color></size>";
            }

            return text;
        }

        // Valori di Wotc.Mtgo.Gre.External.Messaging.AbilityType. Sono i numeri
        // che il database delle abilita' usa come BaseId: Arena stessa scrive
        // `ability.BaseId == 160` per riconoscere l'imbalsamare, e
        // AbilityType.Embalm vale 160. Da li' la corrispondenza.
        private const uint AbilityDeathtouch = 1;   // contatto letale
        private const uint AbilityTrample = 14;     // travolgere

        // Tutte le creature in campo, dalle due parti, in una lista sola: per
        // ogni attaccante dobbiamo cercare chi lo sta bloccando, e il legame
        // sta scritto sul bloccante.
        private static readonly List<MtgCardInstance> _creatures = new List<MtgCardInstance>();

        /// <summary>
        /// Quanto danno e' fermato dai blocchi e quanto arriva comunque al
        /// giocatore che difende.
        ///
        /// L'AGGANCIO E' IL BLOCCANTE, NON L'ATTACCANTE. Verrebbe naturale
        /// guardare <c>BlockedByIds</c> sull'attaccante, ma quello si popola da
        /// <c>AttackInfo.OrderedBlockers</c>, che il server manda solo DOPO il
        /// pulsante di conferma: durante l'assegnazione resta vuoto e il
        /// contatore starebbe fermo su zero proprio nel momento che ci
        /// interessa. <c>BlockingIds</c> sul bloccante arriva invece da
        /// <c>BlockInfo.AttackerIds</c>, che c'e' gia' al primo click.
        /// Verificato sul log del 2026-09-11 (cinque DeclareBlockersResp e una
        /// sola SubmitBlockersReq).
        /// </summary>
        private static void SplitDamage(
            IReadOnlyList<MtgCardInstance> localCards,
            IReadOnlyList<MtgCardInstance> opponentCards,
            out int stopped,
            out int through)
        {
            _creatures.Clear();
            CollectCreatures(localCards);
            CollectCreatures(opponentCards);

            stopped = 0;
            through = 0;

            for (int i = 0; i < _creatures.Count; i++)
            {
                var attacker = _creatures[i];
                if (!IsAttacking(attacker) || !attacker.Power.DefinedValue.HasValue) continue;

                int power = attacker.Power.Value;
                int soak = SoakedBy(attacker);
                bool trample = HasAbility(attacker, AbilityTrample);
                int spill;

                if (soak < 0)
                {
                    spill = power;                                  // nessuno davanti
                }
                else if (!trample)
                {
                    // Senza travolgere il blocco ferma tutto, anche se il
                    // bloccante e' un 1/1 davanti a un 7/7: al giocatore non
                    // arriva niente, ed e' questo che stiamo contando.
                    spill = 0;
                }
                else
                {
                    // Con travolgere passa l'eccedenza: chi attacca deve
                    // assegnare danno letale ai bloccanti e puo' mandare il
                    // resto al giocatore.
                    spill = Math.Max(0, power - soak);
                }

                stopped += power - spill;
                through += spill;
            }
        }

        /// <summary>
        /// Quanto danno devono assorbire i bloccanti di questo attaccante
        /// prima che il resto passi. <c>-1</c> se non e' bloccato da nessuno —
        /// diverso da 0, che vuol dire "bloccato da creature che non
        /// assorbono niente".
        /// </summary>
        private static int SoakedBy(MtgCardInstance attacker)
        {
            // Col contatto letale un solo danno e' gia' letale per ogni
            // bloccante: e' la regola che rende famoso l'abbinamento con
            // travolgere, e senza questa riga il conto sarebbe molto sbagliato
            // proprio nel caso che decide le partite.
            bool deathtouch = HasAbility(attacker, AbilityDeathtouch);

            int soak = 0;
            bool blocked = false;

            for (int i = 0; i < _creatures.Count; i++)
            {
                var blocker = _creatures[i];
                var blocking = blocker.BlockingIds;
                if (blocking == null || !blocking.Contains(attacker.InstanceId)) continue;

                blocked = true;
                if (deathtouch) { soak += 1; continue; }

                // Costituzione variabile (le "*"): non sappiamo quanto
                // assorbe, e non inventiamo un numero. Assorbe 0, quindi il
                // danno che diamo per passante e' al massimo sovrastimato —
                // meglio di un falso "e' tutto fermato".
                if (!blocker.Toughness.DefinedValue.HasValue) continue;
                soak += Math.Max(0, blocker.Toughness.Value - (int)blocker.Damage);
            }

            return blocked ? soak : -1;
        }

        private static bool HasAbility(MtgCardInstance card, uint abilityType)
        {
            var abilities = card.Abilities;
            if (abilities == null) return false;

            for (int i = 0; i < abilities.Count; i++)
            {
                var ability = abilities[i];
                if (ability == null) continue;
                // Id e' il grpId dell'abilita' stampata, BaseId la famiglia a
                // cui appartiene. Per una parola chiave secca coincidono, ma
                // controlliamo entrambi: le varianti con un numerale
                // (travolgere concesso da un'altra carta, per esempio) tengono
                // la famiglia in BaseId.
                if (ability.Id == abilityType || ability.BaseId == abilityType) return true;
            }
            return false;
        }

        private static void CollectCreatures(IReadOnlyList<MtgCardInstance> cards)
        {
            if (cards == null) return;
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (card != null && IsCreature(card)) _creatures.Add(card);
            }
        }

        private static string Row(string label, Totals t)
        {
            var variable = t.Variable > 0
                ? "<size=74%><color=#" + HexDim + "> +" + t.Variable + "*</color></size>"
                : "";

            return "<color=#" + HexDim + ">" + label + "</color>" +
                   "<pos=52%><b>" + t.Count + "</b>" +
                   "<pos=68%>" + t.Power +
                   "<color=#" + HexDim + "> / </color>" + t.Toughness + variable;
        }

        private static void Accumulate(
            IReadOnlyList<MtgCardInstance> cards,
            Totals into,
            Func<MtgCardInstance, bool> predicate)
        {
            if (cards == null) return;

            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (card == null || !IsCreature(card)) continue;
                if (!predicate(card)) continue;

                into.Count++;

                if (!card.Power.DefinedValue.HasValue || !card.Toughness.DefinedValue.HasValue)
                {
                    into.Variable++;
                    continue;
                }

                into.Power += card.Power.Value;
                // La costituzione che conta e' quella che resta: il danno gia'
                // segnato questo turno non la difende piu'.
                into.Toughness += Math.Max(0, card.Toughness.Value - (int)card.Damage);
            }
        }

        private static bool IsCreature(MtgCardInstance card)
        {
            var types = card.CardTypes;
            if (types == null) return false;
            for (int i = 0; i < types.Count; i++)
                if (types[i] == CardType.Creature) return true;
            return false;
        }

        // Declared = cliccata ma non ancora confermata (e' il caso che ci
        // interessa, quello "in tempo reale"); Attacking = dopo la conferma.
        private static bool IsAttacking(MtgCardInstance card)
        {
            return card.AttackState == AttackState.Declared
                || card.AttackState == AttackState.Attacking;
        }

        private static bool CanStillBlock(MtgCardInstance card)
        {
            return !card.IsTapped && card.AttackState == AttackState.None;
        }

        // ------------------------------------------------------------------
        // Overlay
        // ------------------------------------------------------------------

        private static void Show(string text)
        {
            EnsureOverlay();
            if (_label == null) return;

            if (!_canvasRoot.activeSelf) _canvasRoot.SetActive(true);

            // Numeri fermi = pannello smorzato. Costa niente e si aggiorna
            // anche quando il testo non cambia, che e' proprio il caso del
            // congelamento.
            //
            // 0.85 e non meno: lo smorzamento deve *segnalare*, non rendere
            // faticoso leggere il numero. A 0.62 il cartellino spariva contro
            // il tavolo proprio nel momento in cui si guarda il danno.
            if (_group != null) _group.alpha = _locked ? 0.85f : 1f;

            // TMP rifa' la mesh a ogni assegnazione di .text, anche se la
            // stringa e' identica: a 60 fps per tutta la fase di combattimento
            // sarebbe lavoro buttato.
            if (text == _lastText) return;
            _lastText = text;
            _label.text = text;

            // Il cartellino ha tre righe quando nessuno attacca e quattro
            // quando si assegnano i blocchi: l'altezza la prende dal testo
            // invece che da una costante, cosi' non taglia l'ultima riga.
            if (_panelRect != null)
            {
                _label.ForceMeshUpdate();
                _panelRect.sizeDelta = new Vector2(
                    PanelWidth, _label.preferredHeight + PanelPaddingY);

                PlaceRuleUnderHeader();
            }
        }

        /// <summary>
        /// Mette il filetto appena sotto la prima riga, usando l'altezza che
        /// TMP ha appena misurato. Se per qualsiasi motivo le righe non sono
        /// ancora state calcolate, il filetto resta dov'e': meglio fermo che
        /// in mezzo alle parole.
        /// </summary>
        private static void PlaceRuleUnderHeader()
        {
            if (_ruleRect == null || _label == null) return;

            var info = _label.textInfo;
            if (info == null || info.lineCount < 1) return;

            var headerHeight = info.lineInfo[0].lineHeight;
            if (headerHeight <= 0f) return;

            // 8 e' il margine alto del testo dentro al pannello, 3 il respiro
            // fra la riga e il filetto.
            _ruleRect.anchoredPosition = new Vector2(0f, -(8f + headerHeight + 3f));
        }

        private static void Hide()
        {
            if (_canvasRoot == null) return;
            if (_canvasRoot.activeSelf) _canvasRoot.SetActive(false);
            _lastText = null;
        }

        private static RectTransform NewChild(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void EnsureOverlay()
        {
            if (_canvasRoot != null) return;

            _canvasRoot = new GameObject("MTGAES_CombatTotals");
            _canvasRoot.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_canvasRoot);

            var canvas = _canvasRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 150; // sotto i toast (200), sopra il gioco

            var scaler = _canvasRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // Niente GraphicRaycaster: e' un cartellino che si guarda e basta,
            // e senza raycaster non ruba click al tavolo da gioco sotto.

            var panel = new GameObject("Panel");
            panel.transform.SetParent(_canvasRoot.transform, false);
            var panelRect = panel.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 0.5f);
            panelRect.anchorMax = new Vector2(0f, 0.5f);
            panelRect.pivot = new Vector2(0f, 0.5f);
            panelRect.anchoredPosition = new Vector2(24f, 0f);
            panelRect.sizeDelta = new Vector2(PanelWidth, 86f); // ricalcolata in Show()
            _panelRect = panelRect;
            _group = panel.AddComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;

            // Due lastre arrotondate, una dentro l'altra: quella sotto fa da
            // bordo sporgendo di un pixel. Niente barra d'accento azzurra sul
            // fianco: era il dettaglio che piu' diceva "pannello di una mod"
            // invece che interfaccia del gioco.
            var rounded = RoundedRect.Get(10);

            var border = NewChild(panel.transform, "Border");
            border.anchorMin = Vector2.zero; border.anchorMax = Vector2.one;
            border.offsetMin = new Vector2(-1f, -1f); border.offsetMax = new Vector2(1f, 1f);
            var borderImage = border.gameObject.AddComponent<Image>();
            borderImage.sprite = rounded;
            borderImage.type = Image.Type.Sliced;
            borderImage.color = BorderColor;
            borderImage.raycastTarget = false;
            border.SetAsFirstSibling();

            var bg = panel.AddComponent<Image>();
            bg.sprite = rounded;
            bg.type = Image.Type.Sliced;
            bg.color = PanelColor;
            bg.raycastTarget = false;

            // Filetto sotto all'intestazione. La sua altezza NON e' un numero
            // scelto a occhio: viene dall'altezza vera della prima riga,
            // misurata da TMP in Show(). Con un valore fisso basterebbe un
            // cambio di font di Arena per vederlo tagliare il testo.
            var rule = NewChild(panel.transform, "Rule");
            rule.anchorMin = new Vector2(0f, 1f);
            rule.anchorMax = new Vector2(1f, 1f);
            rule.pivot = new Vector2(0.5f, 1f);
            rule.offsetMin = new Vector2(12f, 0f);
            rule.offsetMax = new Vector2(-12f, 0f);
            rule.sizeDelta = new Vector2(-24f, 1f);
            rule.anchoredPosition = new Vector2(0f, -24f); // ricalcolata in Show()
            _ruleRect = rule;
            var ruleImage = rule.gameObject.AddComponent<Image>();
            ruleImage.color = RuleColor;
            ruleImage.raycastTarget = false;

            var textObj = new GameObject("Text");
            textObj.transform.SetParent(panel.transform, false);
            var textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(14f, 10f);
            textRect.offsetMax = new Vector2(-14f, -9f);

            _label = textObj.AddComponent<TextMeshProUGUI>();
            // Font e materiale presi dai testi che Arena sta gia' mostrando,
            // cosi' il cartellino non stona con il resto dello schermo. Senza
            // un font asset esplicito TMP creato a runtime non disegna niente,
            // in silenzio. Vedi UI/ArenaFont.
            ArenaFont.Apply(_label);
            _label.fontSize = 15f;
            _label.characterSpacing = 2f;
            _label.lineSpacing = 16f;
            _label.color = TextColor;
            _label.alignment = TextAlignmentOptions.TopLeft;
            _label.raycastTarget = false;
            _label.richText = true;
        }
    }
}
