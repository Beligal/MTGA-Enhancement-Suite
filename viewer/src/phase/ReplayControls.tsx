/*
 * Derived from phase.rs - https://github.com/phase-rs/phase
 * Commit 7fee171ca673030afc3c7349cbbad4fd232e96be
 * Copyright (c) 2024-2026 phase.rs contributors
 * Licensed MIT OR Apache-2.0; used here under MIT. See third_party/phase/.
 *
 * Origin: client/src/components/replay/ReplayControls.tsx
 * Changes: reads props instead of `useReplayStore` (upstream's seek is async
 * because it round-trips to a Rust engine in a worker; ours is an array index),
 * `useTranslation` replaced by plain English, and the error line dropped -
 * there is no seek that can fail. Layout, transport order and the accent
 * colours are upstream's.
 *
 * The speed `<select>` also carries explicit dark colours. Upstream styles the
 * closed control and lets the popup follow the page; in a standalone
 * `file://` page the browser draws that popup with the SYSTEM light theme, so
 * `text-slate-100` options came out white on white and could not be read. The
 * fix is `color-scheme: dark` (which is what actually retints the native
 * popup) plus per-`<option>` colours for the browsers that ignore it.
 *
 * `extra` is an added slot on the right of the transport, for controls that
 * belong to the viewer and not to playback — the card-size zoom lives there.
 */

import type { ReactNode } from "react";

const SPEED_OPTIONS = [0.5, 1, 2, 4, 8];

export interface ReplayControlsProps {
  currentIndex: number;
  totalSteps: number;
  isPlaying: boolean;
  playbackSpeed: number;
  onSeek: (index: number) => void;
  onPlayPause: () => void;
  onSpeed: (speed: number) => void;
  /** MTGA+ addition: controls that are about the view, not about playback. */
  extra?: ReactNode;
}

export function ReplayControls({
  currentIndex,
  totalSteps,
  isPlaying,
  playbackSpeed,
  onSeek,
  onPlayPause,
  onSpeed,
  extra,
}: ReplayControlsProps) {
  const last = Math.max(totalSteps - 1, 0);

  return (
    <div className="flex w-full flex-col gap-2 border-t border-white/10 bg-black/70 px-4 py-3 backdrop-blur">
      <input
        type="range"
        min={0}
        max={last}
        value={currentIndex}
        onChange={(e) => onSeek(Number(e.target.value))}
        className="w-full accent-amber-400"
        aria-label={`Step ${currentIndex + 1} of ${totalSteps}`}
      />
      <div className="flex items-center justify-between gap-3 text-sm text-slate-200">
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => onSeek(currentIndex - 1)}
            disabled={currentIndex <= 0}
            className="rounded bg-white/10 px-2 py-1 hover:bg-white/20 disabled:opacity-40"
            aria-label="Step back"
          >
            «
          </button>
          <button
            type="button"
            onClick={onPlayPause}
            className="rounded bg-amber-500/90 px-3 py-1 font-semibold text-black hover:bg-amber-400"
          >
            {isPlaying ? "Pause" : "Play"}
          </button>
          <button
            type="button"
            onClick={() => onSeek(currentIndex + 1)}
            disabled={currentIndex >= last}
            className="rounded bg-white/10 px-2 py-1 hover:bg-white/20 disabled:opacity-40"
            aria-label="Step forward"
          >
            »
          </button>
        </div>

        <span className="tabular-nums text-slate-400">
          Step {currentIndex + 1} of {totalSteps}
        </span>

        <div className="flex items-center gap-4">
          {extra}
          <label className="flex items-center gap-2 text-slate-400">
            Speed
            <select
              value={playbackSpeed}
              onChange={(e) => onSpeed(Number(e.target.value))}
              className="rounded bg-white/10 px-1.5 py-1 text-slate-100"
              style={{ colorScheme: "dark" }}
            >
              {SPEED_OPTIONS.map((speed) => (
                <option
                  key={speed}
                  value={speed}
                  style={{ backgroundColor: "#1e293b", color: "#e2e8f0" }}
                >
                  {speed}×
                </option>
              ))}
            </select>
          </label>
        </div>
      </div>
    </div>
  );
}
