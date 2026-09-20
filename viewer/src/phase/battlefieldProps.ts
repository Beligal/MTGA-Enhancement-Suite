/*
 * Derived from phase.rs - https://github.com/phase-rs/phase
 * Commit 7fee171ca673030afc3c7349cbbad4fd232e96be
 * Copyright (c) 2024-2026 phase.rs contributors
 * Licensed MIT OR Apache-2.0; used here under MIT. See third_party/phase/.
 *
 * Origin: client/src/viewmodel/battlefieldProps.ts
 * Changes: kept `partitionByType` and `groupByName`; dropped the combat
 * helpers (`groupAttackersByTarget` and friends), which read the engine's
 * `CombatState`.
 *
 * `groupKey` was TRIMMED rather than fed empty values. Upstream it hashes
 * counters, class level, ring-bearer status, token preset art and commander
 * flags; an Arena replay carries none of those, and a key that hashes fields
 * nobody sets is a key that quietly stops telling two permanents apart. What
 * remains is exactly what MTGA+ can observe.
 *
 * `partitionByType` keeps upstream's ordering of the tests, including the
 * `attached_to`-is-an-Aura/Equipment exception - except that a replay has no
 * attachment relationships, so that branch is unreachable today and was left
 * out rather than left dead.
 */

import type { GameObject, ObjectId } from "./types";
import { publicName, toCardProps } from "./cardProps";
import type { CardViewProps } from "./cardProps";

function canGroup(obj: GameObject): boolean {
  return obj.attachments.length === 0 && !obj.face_down;
}

function groupKey(obj: GameObject): string {
  const colors = [...obj.color].sort().join("");
  return [
    publicName(obj),
    obj.tapped,
    obj.face_down,
    obj.flipped,
    obj.transformed,
    obj.power,
    obj.toughness,
    obj.loyalty,
    obj.damage_marked,
    obj.has_summoning_sickness,
    colors,
    obj.is_token ?? false,
    // MTGA+ additions. Upstream keeps combat in a separate `CombatState`, so
    // its key has no reason to mention it; ours lives on the object, and a key
    // that ignores it would hide one attacker behind an identical creature
    // that is not attacking — the badge would then be a lie about both.
    obj.is_attacking ?? false,
    obj.is_blocking ?? false,
    obj.is_hidden ?? false,
  ].join("|");
}

export interface BattlefieldPartition {
  creatures: ObjectId[];
  lands: ObjectId[];
  support: ObjectId[];
  planeswalkers: ObjectId[];
  other: ObjectId[];
}

export interface GroupedPermanent {
  name: string;
  ids: ObjectId[];
  count: number;
  representative: CardViewProps;
}

export function partitionByType(objects: GameObject[]): BattlefieldPartition {
  const creatures: ObjectId[] = [];
  const lands: ObjectId[] = [];
  const support: ObjectId[] = [];
  const planeswalkers: ObjectId[] = [];
  const other: ObjectId[] = [];

  for (const obj of objects) {
    const coreTypes = obj.card_types.core_types;

    if (coreTypes.includes("Creature")) {
      creatures.push(obj.id);
    } else if (coreTypes.includes("Land")) {
      lands.push(obj.id);
    } else if (coreTypes.includes("Planeswalker")) {
      planeswalkers.push(obj.id);
    } else if (coreTypes.includes("Artifact") || coreTypes.includes("Enchantment")) {
      support.push(obj.id);
    } else {
      other.push(obj.id);
    }
  }

  return { creatures, lands, support, planeswalkers, other };
}

export function groupByName(objects: GameObject[]): GroupedPermanent[] {
  const groups = new Map<string, GameObject[]>();

  for (const obj of objects) {
    if (!canGroup(obj)) {
      groups.set(`__solo_${obj.id}`, [obj]);
      continue;
    }

    const key = groupKey(obj);
    const existing = groups.get(key);
    if (existing) {
      existing.push(obj);
    } else {
      groups.set(key, [obj]);
    }
  }

  const result: GroupedPermanent[] = [];

  for (const members of groups.values()) {
    result.push({
      name: publicName(members[0]),
      ids: members.map((m) => m.id),
      count: members.length,
      representative: toCardProps(members[0]),
    });
  }

  return result;
}
