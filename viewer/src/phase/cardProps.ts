/*
 * Derived from phase.rs - https://github.com/phase-rs/phase
 * Commit 7fee171ca673030afc3c7349cbbad4fd232e96be
 * Copyright (c) 2024-2026 phase.rs contributors
 * Licensed MIT OR Apache-2.0; used here under MIT. See third_party/phase/.
 *
 * Origin: client/src/viewmodel/cardProps.ts
 * Changes: reduced to `publicName`, `toCardProps` and `computePTDisplay`.
 * Dropped: counters (colours, icon classes, formatting), class levels and
 * Roman numerals, changeling keyword handling, attachment targets - none of
 * which an Arena replay carries. `shouldRenderCardBack` keeps its shape but
 * loses `display_visible_to_viewer`, since a replay is the recording player's
 * own view and has no second viewer to mask against.
 */

import type { GameObject, ManaColor, ObjectId } from "./types";

export const FACE_DOWN_CARD_NAME = "Face-down card";

export function shouldRenderCardBack(obj: Pick<GameObject, "face_down"> | undefined): boolean {
  return obj?.face_down === true;
}

/** MTGA+ addition: what a card we cannot name is called. */
export const HIDDEN_CARD_NAME = "Hidden card";

export interface CardViewProps {
  id: ObjectId;
  name: string;
  tapped: boolean;
  power: number | null;
  toughness: number | null;
  basePower: number | null;
  baseToughness: number | null;
  damageMarked: number;
  effectiveToughness: number | null;
  isPowerBuffed: boolean;
  isPowerDebuffed: boolean;
  isToughnessBuffed: boolean;
  isToughnessDebuffed: boolean;
  isCreature: boolean;
  isLand: boolean;
  attachmentIds: ObjectId[];
  colorIdentity: ManaColor[];
  /** MTGA+: kept out of the frame and off the card image. */
  isAbility: boolean;
  isHidden: boolean;
  isAttacking: boolean;
  isBlocking: boolean;
  subtypes: string[];
}

export type PTColor = "white" | "green" | "red";

export interface PTDisplay {
  power: number;
  toughness: number;
  powerColor: PTColor;
  toughnessColor: PTColor;
}

export function publicName(obj: GameObject): string {
  // MTGA+: `face_down` here means "we never learned what it is", so the name
  // the object already carries (`Hidden card`) is the honest one; upstream
  // substitutes its own constant because its engine knows and is masking.
  if (obj.is_hidden === true) return obj.name;
  return shouldRenderCardBack(obj) ? FACE_DOWN_CARD_NAME : obj.name;
}

export function toCardProps(obj: GameObject): CardViewProps {
  const isPowerBuffed = obj.power != null && obj.base_power != null && obj.power > obj.base_power;
  const isPowerDebuffed =
    obj.power != null && obj.base_power != null && obj.power < obj.base_power;
  const isToughnessBuffed =
    obj.toughness != null && obj.base_toughness != null && obj.toughness > obj.base_toughness;
  const isToughnessDebuffed =
    (obj.toughness != null &&
      obj.base_toughness != null &&
      obj.toughness < obj.base_toughness) ||
    obj.damage_marked > 0;

  return {
    id: obj.id,
    name: publicName(obj),
    tapped: obj.tapped,
    power: obj.power,
    toughness: obj.toughness,
    basePower: obj.base_power,
    baseToughness: obj.base_toughness,
    damageMarked: obj.damage_marked,
    effectiveToughness: obj.toughness != null ? obj.toughness - obj.damage_marked : null,
    isPowerBuffed,
    isPowerDebuffed,
    isToughnessBuffed,
    isToughnessDebuffed,
    isCreature: obj.card_types.core_types.includes("Creature"),
    isLand: obj.card_types.core_types.includes("Land"),
    attachmentIds: obj.attachments,
    colorIdentity: obj.color,
    isAbility: obj.is_ability === true,
    isHidden: obj.is_hidden === true,
    isAttacking: obj.is_attacking === true,
    isBlocking: obj.is_blocking === true,
    subtypes: obj.card_types.subtypes,
  };
}

export function computePTDisplay(obj: GameObject): PTDisplay | null {
  if (obj.power == null || obj.toughness == null) return null;

  const powerColor: PTColor =
    obj.base_power != null && obj.power > obj.base_power
      ? "green"
      : obj.base_power != null && obj.power < obj.base_power
        ? "red"
        : "white";

  const toughnessColor: PTColor =
    obj.damage_marked > 0
      ? "red"
      : obj.base_toughness != null && obj.toughness > obj.base_toughness
        ? "green"
        : obj.base_toughness != null && obj.toughness < obj.base_toughness
          ? "red"
          : "white";

  return {
    power: obj.power,
    toughness: obj.damage_marked > 0 ? obj.toughness - obj.damage_marked : obj.toughness,
    powerColor,
    toughnessColor,
  };
}
