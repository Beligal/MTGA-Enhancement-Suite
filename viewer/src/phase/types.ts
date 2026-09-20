/*
 * Field names mirror phase.rs - https://github.com/phase-rs/phase
 * Commit 7fee171ca673030afc3c7349cbbad4fd232e96be
 * Copyright (c) 2024-2026 phase.rs contributors
 * Licensed MIT OR Apache-2.0; used here under MIT. See third_party/phase/.
 *
 * Origin: the subset of `client/src/adapter/types.ts` that the vendored
 * viewmodel and presentation files actually read.
 * Changes: 5,270 lines reduced to ~40. phase.rs's `GameObject` mirrors its
 * Rust engine's serde types field for field - dungeons, commander tax, EDH
 * bracket, layer attribution. A replay has none of that. What survives is the
 * shape the vendored `cardProps` / `battlefieldProps` read, kept under the
 * SAME names so those files are the upstream code and not a rewrite.
 */

export type ObjectId = number;
export type PlayerId = number;

export type ManaColor = "White" | "Blue" | "Black" | "Red" | "Green";

/** phase.rs's `CardType`: printed type line, already split. */
export interface CardTypeLine {
  super_types: string[];
  core_types: string[];
  subtypes: string[];
}

/**
 * The subset of phase.rs's `GameObject` that the vendored code reads.
 *
 * Optional there and absent here: counters, class level, ring-bearer, unbounded
 * piles, tokens' preset art, commander flags. `groupKey` in `battlefieldProps`
 * was trimmed to match rather than fed empty values - a grouping key that
 * hashes fields nobody sets is a key that silently stops grouping.
 */
export interface GameObject {
  id: ObjectId;
  owner: PlayerId;
  controller: PlayerId;
  tapped: boolean;
  face_down: boolean;
  flipped: boolean;
  transformed: boolean;
  damage_marked: number;
  attachments: ObjectId[];
  name: string;
  power: number | null;
  toughness: number | null;
  loyalty: number | null;
  card_types: CardTypeLine;
  color: ManaColor[];
  base_power: number | null;
  base_toughness: number | null;
  has_summoning_sickness?: boolean;
  is_token?: boolean;
  /** MTGA+ addition: an ability or emblem on the stack, which is not a card. */
  is_ability?: boolean;
  /** MTGA+ addition: Arena's card id, which is what Scryfall indexes art by. */
  grp_id?: number;
  /**
   * MTGA+ addition: the card is there and we do not know which card it is.
   * phase.rs has `display_visible_to_viewer` for the same idea, but its
   * engine masks a shared state per viewer, while ours simply never received
   * the identity.
   */
  is_hidden?: boolean;
  /**
   * MTGA+ additions. phase.rs keeps attack and block assignments in a separate
   * `CombatState` the engine maintains; Arena puts them on the object itself
   * (`AttackState` / `BlockState`), so that is where they stay here.
   */
  is_attacking?: boolean;
  is_blocking?: boolean;
}
