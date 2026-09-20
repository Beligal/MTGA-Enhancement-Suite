/**
 * The ReplayAdapter: MTGA+ replay step → objects shaped like phase.rs's
 * `GameObject`, so phase.rs's viewmodel can be used unchanged.
 *
 * **Why a pure function and not an `EngineAdapter`.** phase.rs's adapter
 * interface is almost entirely *actions* — `submitAction`, `getLegalActions`,
 * `previewManaPayment`, `submitAiActionProposal`. A replay has no actions, and
 * phase.rs's own `ReplayAdapter` implements those methods only so it can sit in
 * the same slot, making each one throw. The boundary that actually carries
 * meaning is not the adapter, it is the *state*: what the board renders from.
 * So this is the boundary, and it is a function from a step to that state.
 *
 * It also means the boundary can be tested with an `expect()` — no worker, no
 * WASM, no store. That is the same reason `ReplayReconstructor` on the C# side
 * contains no Unity.
 *
 * **What the shape cannot carry, and why that is fine.** phase.rs's engine
 * knows base power/toughness, counters, attachments and layer attribution.
 * Arena's stream sends the *current* characteristics and nothing about how they
 * got there. So `base_power` / `base_toughness` are null here rather than
 * copied from `power` / `toughness`: copying would make `computePTDisplay`
 * declare "not buffed" as a fact, when the truth is that we do not know. Null
 * makes it say nothing, which is what we can honestly say.
 */

import type { GameObject, ManaColor } from "../phase/types";
import type { BoardRow, CardEntry, ReplayStep, SeatRow } from "./replayFile";
import { FLAG, ZONE } from "./replayFile";

const COLOR_OF_LETTER: Record<string, ManaColor> = {
  W: "White",
  U: "Blue",
  B: "Black",
  R: "Red",
  G: "Green",
};

export function lettersToColors(letters: string): ManaColor[] {
  const out: ManaColor[] = [];
  for (const ch of letters) {
    const color = COLOR_OF_LETTER[ch];
    if (color && !out.includes(color)) out.push(color);
  }
  return out;
}

export interface SeatView {
  seat: number;
  life: number;
  hand: number;
  graveyard: number;
  exile: number;
  library: number;
}

export function toSeats(step: ReplayStep): SeatView[] {
  return step.p.map(([seat, life, hand, graveyard, exile, library]: SeatRow) => ({
    seat,
    life,
    hand,
    graveyard,
    exile,
    library,
  }));
}

const NO_TYPES = { super_types: [], core_types: [], subtypes: [] };

/** A card whose presence we know and whose identity we do not. */
export const HIDDEN_CARD_NAME = "Hidden card";

/**
 * One board row → one phase.rs-shaped object.
 *
 * An ability on the stack gets `core_types: []` deliberately, so
 * `partitionByType` files it under "other" instead of pretending it is a
 * permanent: its GrpId identifies an *ability*, and looking that id up among
 * cards finds an unrelated card (see MISTAKES.md #33). The C# side already
 * keeps ability ids out of the card table; this keeps them out of the frame,
 * the image and the type row as well.
 */
export function toPhaseObject(row: BoardRow, cards: Record<string, CardEntry>): GameObject {
  const [instance, grpId, , seat, tapped, power, toughness, damage, flags] = row;

  const card: CardEntry | undefined = grpId ? cards[String(grpId)] : undefined;
  const isAbility = (flags & FLAG.Ability) !== 0;
  const isHidden = grpId === 0;

  const name = isHidden
    ? HIDDEN_CARD_NAME
    : isAbility
      ? (card?.n ? `${card.n} — ability` : "Ability")
      : (card?.n ?? `#${grpId}`);

  const isCreature = !isAbility && !isHidden && !!card && card.ct.includes("Creature");

  return {
    id: instance,
    owner: seat,
    controller: seat,
    tapped: tapped === 1,
    // phase.rs's `shouldRenderCardBack` reads this, and a card we cannot name
    // is exactly what it means: draw the back.
    face_down: isHidden,
    flipped: false,
    transformed: false,
    damage_marked: damage,
    attachments: [],
    name,
    // A power/toughness of 0/0 on a non-creature is Arena's default, not a
    // real 0/0 — a land is not a 0/0 permanent. Only creatures keep the pair.
    power: isCreature ? power : null,
    toughness: isCreature ? toughness : null,
    loyalty: null,
    card_types: isAbility || isHidden
      ? NO_TYPES
      : {
          super_types: card?.sup ?? [],
          core_types: card?.ct ?? [],
          subtypes: card?.st ?? [],
        },
    color: lettersToColors(card?.c ?? ""),
    base_power: null,
    base_toughness: null,
    has_summoning_sickness: (flags & FLAG.SummoningSick) !== 0,
    is_token: (flags & FLAG.Token) !== 0,
    is_ability: isAbility,
    is_attacking: (flags & FLAG.Attacking) !== 0,
    is_blocking: (flags & FLAG.Blocking) !== 0,
    is_hidden: isHidden,
    grp_id: grpId || undefined,
  };
}

/** One seat's objects, by zone. */
export interface SeatBoard {
  battlefield: GameObject[];
  stack: GameObject[];
  hand: GameObject[];
  graveyard: GameObject[];
  exile: GameObject[];
}

function emptyBoard(): SeatBoard {
  return { battlefield: [], stack: [], hand: [], graveyard: [], exile: [] };
}

export function toPhaseObjects(
  step: ReplayStep,
  cards: Record<string, CardEntry>,
  seat: number,
): SeatBoard {
  const board = emptyBoard();

  for (const row of step.b) {
    if (row[3] !== seat) continue;
    const obj = toPhaseObject(row, cards);
    switch (row[2]) {
      case ZONE.Battlefield: board.battlefield.push(obj); break;
      case ZONE.Stack:       board.stack.push(obj); break;
      case ZONE.Hand:        board.hand.push(obj); break;
      case ZONE.Graveyard:   board.graveyard.push(obj); break;
      case ZONE.Exile:       board.exile.push(obj); break;
    }
  }

  return board;
}

/** Every seat present in this step, opponent first and the viewer last — as at a table. */
export function seatOrder(step: ReplayStep, viewerSeat: number): number[] {
  const seats = step.p.map((p) => p[0]);
  if (seats.length === 0) return [1, 2];
  return [...seats].sort((a, b) => (a === viewerSeat ? 1 : 0) - (b === viewerSeat ? 1 : 0));
}
