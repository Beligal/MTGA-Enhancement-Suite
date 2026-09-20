import type { CardViewProps } from "../phase/cardProps";
import { computePTDisplay, toCardProps } from "../phase/cardProps";
import type { SeatBoard, SeatView } from "../adapter/replayAdapter";
import { Battlefield } from "./Battlefield";
import { CardTile } from "./CardTile";
import { HandFan } from "./HandFan";
import { ZonePile } from "./ZonePile";

export interface SeatPanelProps {
  name: string;
  isViewer: boolean;
  isActive: boolean;
  seat: SeatView | undefined;
  board: SeatBoard;
  onHover: (card: CardViewProps, grpId: number | undefined) => void;
  onLeave: () => void;
}

/**
 * One player: name, life, hand, stack, battlefield, and the piles.
 *
 * The reading order mirrors a table. For the viewer (bottom of the screen) the
 * hand sits under the battlefield, as it would in front of them; for the
 * opponent (top) it sits above, across from them.
 *
 * **The library stays a number.** Arena lists its card ids but never sends the
 * cards, so a fan there would be fifty identical backs pretending to be
 * information.
 */
export function SeatPanel({
  name,
  isViewer,
  isActive,
  seat,
  board,
  onHover,
  onLeave,
}: SeatPanelProps) {
  const hand = <HandFan cards={board.hand} onHover={onHover} onLeave={onLeave} />;

  return (
    <section
      className={`rounded-lg border px-3 py-2 ${
        isActive ? "border-amber-400/40 bg-amber-400/5" : "border-white/10 bg-black/20"
      }`}
    >
      <header className="mb-2 flex items-baseline justify-between gap-3">
        <h2
          className={`truncate text-sm font-semibold ${
            isViewer ? "text-sky-300" : "text-slate-200"
          }`}
        >
          {name}
          {isActive && (
            <span className="ml-2 text-[10px] font-normal uppercase tracking-wide text-amber-400">
              active
            </span>
          )}
        </h2>
        <span className="shrink-0 text-xl font-bold tabular-nums text-slate-100">
          {seat ? seat.life : "?"}
        </span>
      </header>

      {!isViewer && hand}

      {board.stack.length > 0 && (
        <div
          className="my-2 flex flex-wrap items-center gap-2 rounded border border-indigo-400/30 bg-indigo-500/10 p-1.5"
          style={{
            ["--card-w" as string]: "calc(78px * var(--zoom, 1))",
            ["--card-h" as string]: "calc(109px * var(--zoom, 1))",
          }}
        >
          <span className="pl-1 text-[10px] uppercase tracking-wide text-indigo-300">stack</span>
          {board.stack.map((obj) => (
            <CardTile
              key={obj.id}
              card={toCardProps(obj)}
              grpId={obj.grp_id}
              ptDisplay={computePTDisplay(obj)}
              count={1}
              summoningSick={false}
              onHover={onHover}
              onLeave={onLeave}
            />
          ))}
        </div>
      )}

      <Battlefield
        objects={board.battlefield}
        creaturesFirst={isViewer}
        onHover={onHover}
        onLeave={onLeave}
      />

      {isViewer && hand}

      <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1">
        <ZonePile
          label="graveyard"
          cards={board.graveyard}
          count={seat?.graveyard ?? board.graveyard.length}
          onHover={onHover}
          onLeave={onLeave}
        />
        <ZonePile
          label="exile"
          cards={board.exile}
          count={seat?.exile ?? board.exile.length}
          onHover={onHover}
          onLeave={onLeave}
        />
        <span className="text-[11px] tabular-nums text-slate-500">
          library {seat ? seat.library : "?"}
        </span>
      </div>
    </section>
  );
}
