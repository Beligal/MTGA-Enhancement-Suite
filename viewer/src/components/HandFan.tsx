import { useRef } from "react";
import type { CSSProperties } from "react";

import { fanGeometry, spreadFactor } from "../phase/fanGeometry";
import { toCardProps } from "../phase/cardProps";
import type { CardViewProps } from "../phase/cardProps";
import type { GameObject } from "../phase/types";
import { CardTile } from "./CardTile";
import { useElementWidth } from "./useElementWidth";

export interface HandFanProps {
  cards: GameObject[];
  onHover: (card: CardViewProps, grpId: number | undefined) => void;
  onLeave: () => void;
}

/** A hand card may get this wide before the fan stops growing. */
const MAX_CARD_WIDTH = 132;

/**
 * A player's hand, fanned.
 *
 * **This is the part of a replay people actually come back for** — what was in
 * your hand when you made that decision. The stream carries it: measured on the
 * 2026-09-20 recording, 7 of 7 cards known in the recording player's hand and
 * 0 of 4 in the opponent's. So one hand shows faces and the other shows backs,
 * and the difference is what Arena sent, not a rule of ours.
 *
 * The overlap, tilt and arc are phase.rs's `fanGeometry` — the part worth not
 * reinventing. What is written here is only the sizing: phase.rs's own
 * `handFanPresentation` budgets the fan against `92vw` and a hand-specific
 * scale variable, which belong to its full-screen game layout, not to a panel
 * inside a scrolling page.
 */
export function HandFan({ cards, onHover, onLeave }: HandFanProps) {
  const ref = useRef<HTMLDivElement | null>(null);
  const width = useElementWidth(ref, 720);

  // Size the cards so the WHOLE fan fits the width it was given. `spreadFactor`
  // is the fan's total width in units of one card, and it is derived from the
  // same overlap the cards render with — which is the point of taking it from
  // phase.rs rather than guessing an overlap and a width separately.
  const spread = spreadFactor(cards.length, "wide");
  const cardWidth = Math.max(38, Math.min(MAX_CARD_WIDTH, Math.floor(width / spread)));
  const cardHeight = Math.round(cardWidth / (63 / 88));
  const geometry = fanGeometry(cards.length, "--card-w", "wide");

  // The arc pushes the outer cards down; the row has to be tall enough for it
  // or they clip against whatever is underneath.
  const maxArc = Math.max(geometry.arc(0), geometry.arc(cards.length - 1));

  const style = {
    "--card-w": `calc(${cardWidth}px * var(--zoom, 1))`,
    "--card-h": `calc(${cardHeight}px * var(--zoom, 1))`,
    height: `calc(${cardHeight + maxArc + 8}px * var(--zoom, 1))`,
  } as CSSProperties;

  // The measuring box is always rendered, even with no cards in hand: a ref
  // that only exists sometimes gives the observer nothing to watch, and the
  // fan would come back at its fallback width the first time a card arrives.
  return (
    <div ref={ref} className="flex items-start justify-center overflow-hidden" style={style}>
      {cards.length === 0 && <span className="sr-only">no cards in hand</span>}
      {cards.map((card, i) => (
        <div
          key={card.id}
          className="transition-transform duration-200 hover:z-10 hover:-translate-y-2"
          style={{
            marginLeft: i === 0 ? 0 : geometry.overlap,
            transform: `translateY(${geometry.arc(i)}px) rotate(${geometry.rotation(i)}deg)`,
            transformOrigin: "bottom center",
          }}
        >
          <CardTile
            card={toCardProps(card)}
            grpId={card.grp_id}
            ptDisplay={null}
            count={1}
            summoningSick={false}
            onHover={onHover}
            onLeave={onLeave}
          />
        </div>
      ))}
    </div>
  );
}
