import { useRef } from "react";
import type { CSSProperties } from "react";

import { getCardSize } from "../phase/boardSizing";
import { groupByName, partitionByType } from "../phase/battlefieldProps";
import type { GroupedPermanent } from "../phase/battlefieldProps";
import { computePTDisplay } from "../phase/cardProps";
import type { CardViewProps } from "../phase/cardProps";
import type { GameObject, ObjectId } from "../phase/types";
import { CardTile } from "./CardTile";
import { useElementWidth } from "./useElementWidth";

export interface BattlefieldProps {
  objects: GameObject[];
  /** Creatures on top for the seat at the bottom of the screen, as at a table. */
  creaturesFirst: boolean;
  onHover: (card: CardViewProps, grpId: number | undefined) => void;
  onLeave: () => void;
}

/**
 * How wide a card may get, per row.
 *
 * These are `getCardSize`'s `baseWidth`, and in that function the base width is
 * also the **cap**: it multiplies by `Math.min(1, fit)`, so a card never grows
 * past its base however much room there is.
 *
 * phase.rs's numbers (92 / 74 / 62) are right for phase.rs, whose board is one
 * fixed-height screen where the binding constraint is vertical. Ours is a
 * column that scrolls, so those caps left most of a wide window empty and the
 * cards too small to read — which is the complaint that produced these numbers.
 * Raising the cap is the whole fix; the shrink-to-fit behaviour below the cap
 * is phase.rs's and is untouched.
 */
const ROW_BASE_WIDTH = {
  creatures: 150,
  others: 130,
  lands: 110,
} as const;

/**
 * One player's battlefield: creatures, then the other permanents, then lands.
 *
 * The split is phase.rs's `partitionByType` and the stacking of identical
 * permanents is its `groupByName` — both used as they are. What is written
 * here is only the arrangement, because phase.rs's own `BattlefieldRow` (298
 * lines) reads three Zustand stores and a board-interaction context to decide
 * what a click means, and a replay has no clicks.
 */
export function Battlefield({ objects, creaturesFirst, onHover, onLeave }: BattlefieldProps) {
  const ref = useRef<HTMLDivElement | null>(null);
  const width = useElementWidth(ref, 720);

  const byId = new Map<ObjectId, GameObject>(objects.map((o) => [o.id, o]));
  const partition = partitionByType(objects);

  const pick = (ids: ObjectId[]) =>
    groupByName(ids.map((id) => byId.get(id)).filter((o): o is GameObject => o !== undefined));

  const creatures = pick(partition.creatures);
  const others = pick([...partition.planeswalkers, ...partition.support, ...partition.other]);
  const lands = pick(partition.lands);

  const rows = [
    { key: "creatures", groups: creatures, base: ROW_BASE_WIDTH.creatures },
    { key: "others", groups: others, base: ROW_BASE_WIDTH.others },
    { key: "lands", groups: lands, base: ROW_BASE_WIDTH.lands },
  ];
  if (!creaturesFirst) rows.reverse();

  const empty = creatures.length + others.length + lands.length === 0;

  return (
    <div ref={ref} className="flex flex-col gap-1.5">
      {empty ? (
        <p className="px-1 py-6 text-xs text-slate-500">nothing on the battlefield</p>
      ) : (
        rows.map((row) =>
          row.groups.length === 0 ? null : (
            <Row
              key={row.key}
              groups={row.groups}
              width={width}
              baseWidth={row.base}
              byId={byId}
              onHover={onHover}
              onLeave={onLeave}
            />
          ),
        )
      )}
    </div>
  );
}

function Row({
  groups,
  width,
  baseWidth,
  byId,
  onHover,
  onLeave,
}: {
  groups: GroupedPermanent[];
  width: number;
  baseWidth: number;
  byId: Map<ObjectId, GameObject>;
  onHover: (card: CardViewProps, grpId: number | undefined) => void;
  onLeave: () => void;
}) {
  const size = getCardSize(width, groups.length, { baseWidth, minWidth: 40, gap: 8 });

  // The row publishes the card size as custom properties and every child sizes
  // itself from them — phase.rs's convention, and what keeps the P/T box and
  // the fallback text legible at any scale without measuring anything in JS.
  // A tapped card reserves its own turned footprint (see `CardTile`), so the
  // row needs no compensation of its own.
  //
  // `--zoom` is the reader's own multiplier, and it is applied HERE rather than
  // folded into the number above on purpose: the JS decides what fits, the CSS
  // applies what the reader asked for. Zooming past the fit makes the row wrap,
  // which is the right thing to happen and takes no code to arrange.
  const style = {
    "--card-w": `calc(${size.width}px * var(--zoom, 1))`,
    "--card-h": `calc(${size.height}px * var(--zoom, 1))`,
    gap: `calc(${size.gap}px * var(--zoom, 1))`,
  } as CSSProperties;

  return (
    <div className="flex flex-wrap items-center" style={style}>
      {groups.map((group) => {
        const representative = byId.get(group.ids[0]);
        return (
          <CardTile
            key={group.ids[0]}
            card={group.representative}
            grpId={representative?.grp_id}
            ptDisplay={representative ? computePTDisplay(representative) : null}
            count={group.count}
            summoningSick={representative?.has_summoning_sickness === true}
            onHover={onHover}
            onLeave={onLeave}
          />
        );
      })}
    </div>
  );
}
