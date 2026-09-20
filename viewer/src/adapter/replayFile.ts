/**
 * The JSON that `ReplayHtml.Json` writes, as TypeScript.
 *
 * Keys are short on purpose: a long match is hundreds of steps of a few dozen
 * objects each, and this text is embedded in the file the user opens. The
 * comments here ARE the schema documentation - the C# side has no types the
 * browser can read, so this file and `Plugin/Features/ReplayHtml.cs` have to be
 * changed together, and that is why the field order below matches the arrays
 * built there line for line.
 */

/**
 * Where an object was. Matches `ReplayReconstructor.BoardZone`.
 *
 * The library is missing on purpose: the stream lists its ids but never sends
 * the objects, so it would be a row of fifty identical card backs. The count
 * says the same thing in one character.
 */
export const ZONE = {
  Battlefield: 0,
  Stack: 1,
  Hand: 2,
  Graveyard: 3,
  Exile: 4,
} as const;

/**
 * Flags packed into one number, because it is six digits for EVERY object of
 * EVERY step and this text rides inside the file the user opens.
 */
export const FLAG = {
  Ability: 1,
  Token: 2,
  SummoningSick: 4,
  Attacking: 8,
  Blocking: 16,
} as const;

/**
 * Board row: `[instance, grpId, zone, seat, tapped, power, toughness, damage, flags]`
 *
 * `grpId` is **0 when the card is there but we do not know which card it is** -
 * the opponent's hand. That is not missing data, it is the truth: Arena never
 * sent it.
 */
export type BoardRow = [
  number, number, number, number, 0 | 1, number, number, number, number,
];

/** Seat row: [seat, life, hand, graveyard, exile, library] */
export type SeatRow = [number, number, number, number, number, number];

export interface CardEntry {
  /** English name from Arena's own card database. */
  n: string;
  /** Supertypes, e.g. ["Basic", "Legendary"]. */
  sup: string[];
  /** Core types, e.g. ["Creature"]. */
  ct: string[];
  /** Subtypes, e.g. ["Elf", "Druid"]. */
  st: string[];
  /** Colours as letters, e.g. "WG"; empty for colourless. */
  c: string;
}

export interface ReplayStep {
  /** Game number within the match; 0 before the first game is announced. */
  g: number;
  t: number;
  /** Active seat. */
  ap: number;
  ph: string;
  st: string;
  ev: string[];
  p: SeatRow[];
  b: BoardRow[];
}

export interface ReplayMeta {
  deck: string;
  opp: string;
  event: string;
  format: string;
  ranked: boolean;
  date: string;
  /** 1 win, 0 loss, -1 draw or unknown. */
  won: number;
  /** The recording player's seat. 0 when the file never revealed it. */
  seat: number;
  /** The recording stopped mid-match. */
  truncated: boolean;
}

export interface ReplayFile {
  meta: ReplayMeta;
  cards: Record<string, CardEntry>;
  steps: ReplayStep[];
}
