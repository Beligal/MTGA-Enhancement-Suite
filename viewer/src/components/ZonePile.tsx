import { useState } from "react";
import type { CSSProperties } from "react";

import { toCardProps } from "../phase/cardProps";
import type { CardViewProps } from "../phase/cardProps";
import type { GameObject } from "../phase/types";
import { CardTile } from "./CardTile";

export interface ZonePileProps {
  label: string;
  cards: GameObject[];
  /** The count the stream reported, which can exceed what we can name. */
  count: number;
  onHover: (card: CardViewProps, grpId: number | undefined) => void;
  onLeave: () => void;
}

const PILE_SIZE = { "--card-w": "46px", "--card-h": "64px" } as CSSProperties;

/**
 * A graveyard or exile pile: collapsed to the last few cards, clickable to
 * open.
 *
 * Graveyards are **public** — measured on the 2026-09-20 recording, every card
 * in one had a known id — so unlike a hand this shows real cards for both
 * players. That makes it worth more than a number: which removal has already
 * been spent is most of what you want from a replay.
 *
 * Collapsed by default because a late-game graveyard is twenty cards and the
 * board is the thing being read.
 */
export function ZonePile({ label, cards, count, onHover, onLeave }: ZonePileProps) {
  const [open, setOpen] = useState(false);

  if (count === 0) return null;

  // With nothing to show, the number is the whole truth and a clickable pile
  // would promise cards that are not there.
  if (cards.length === 0) {
    return (
      <span className="text-[11px] tabular-nums text-slate-500">
        {label} {count}
      </span>
    );
  }

  const shown = open ? cards : cards.slice(-3);

  return (
    <div className="flex items-center gap-1.5">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="shrink-0 text-[11px] tabular-nums text-slate-400 hover:text-slate-200"
        title={open ? "Collapse" : "Show all"}
      >
        {label} {count} {open ? "▾" : "▸"}
      </button>
      <div className="flex flex-wrap items-center" style={PILE_SIZE}>
        {shown.map((card, i) => (
          <div key={card.id} style={{ marginLeft: i === 0 || open ? 0 : "-28px" }}>
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
    </div>
  );
}
