export const ZOOM_MIN = 0.4;
export const ZOOM_MAX = 2.4;
export const ZOOM_STEP = 0.2;

/**
 * Cards start at 60%.
 *
 * The fitting maths sizes a card to the width it has, which on a wide window
 * makes a single permanent very large and pushes both battlefields past the
 * bottom of the screen. 60% is where both players' boards fit at once on a
 * normal window, which is what a replay is usually read for; anyone who wants
 * to study one card turns it up, and the choice is remembered.
 */
export const ZOOM_DEFAULT = 0.6;

export interface ZoomControlProps {
  zoom: number;
  onZoom: (zoom: number) => void;
}

/** Snap to the step grid so the label never reads "119%". */
export function clampZoom(value: number): number {
  const stepped = Math.round(value / ZOOM_STEP) * ZOOM_STEP;
  return Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, Number(stepped.toFixed(2))));
}

/**
 * How big the cards are drawn.
 *
 * The board already sizes cards to fill the width it has; this is the reader
 * saying they want them bigger than that anyway — past the fit the rows simply
 * wrap. Which is why zoom is a CSS multiplier (`--zoom`) rather than an input
 * to the fitting maths: the layout keeps deciding what fits, and this decides
 * what is comfortable.
 */
export function ZoomControl({ zoom, onZoom }: ZoomControlProps) {
  return (
    <div className="flex items-center gap-1 text-slate-400">
      <span className="mr-1">Cards</span>
      <button
        type="button"
        onClick={() => onZoom(clampZoom(zoom - ZOOM_STEP))}
        disabled={zoom <= ZOOM_MIN}
        className="rounded bg-white/10 px-2 py-1 leading-none hover:bg-white/20 disabled:opacity-40"
        aria-label="Smaller cards"
        title="Smaller cards"
      >
        −
      </button>
      <button
        type="button"
        onClick={() => onZoom(ZOOM_DEFAULT)}
        className="w-12 rounded px-1 py-1 text-center tabular-nums hover:bg-white/10"
        aria-label="Reset card size"
        title="Reset card size"
      >
        {Math.round(zoom * 100)}%
      </button>
      <button
        type="button"
        onClick={() => onZoom(clampZoom(zoom + ZOOM_STEP))}
        disabled={zoom >= ZOOM_MAX}
        className="rounded bg-white/10 px-2 py-1 leading-none hover:bg-white/20 disabled:opacity-40"
        aria-label="Bigger cards"
        title="Bigger cards"
      >
        +
      </button>
    </div>
  );
}
