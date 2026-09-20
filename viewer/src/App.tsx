import { useCallback, useEffect, useState } from "react";

import { seatOrder, toPhaseObjects, toSeats } from "./adapter/replayAdapter";
import type { ReplayFile } from "./adapter/replayFile";
import { EventList } from "./components/EventList";
import { SidePanel } from "./components/SidePanel";
import { SeatPanel } from "./components/SeatPanel";
import { CardPreview } from "./components/CardPreview";
import { ZoomControl, ZOOM_DEFAULT, ZOOM_MAX, ZOOM_MIN, clampZoom } from "./components/ZoomControl";
import { ReplayControls } from "./phase/ReplayControls";
import type { CardViewProps } from "./phase/cardProps";
import { clampNumber, readPref, writePref } from "./services/preferences";

export interface AppProps {
  replay: ReplayFile;
}

export function App({ replay }: AppProps) {
  const [index, setIndex] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState(1);
  const [hovered, setHovered] = useState<{ card: CardViewProps; grpId: number | undefined } | null>(null);
  const [zoom, setZoom] = useState(() =>
    readPref("zoom", (raw) => clampNumber(raw, ZOOM_MIN, ZOOM_MAX), ZOOM_DEFAULT),
  );

  const changeZoom = useCallback((next: number) => {
    const value = clampZoom(next);
    setZoom(value);
    writePref("zoom", value);
  }, []);

  const last = Math.max(replay.steps.length - 1, 0);
  const step = replay.steps[Math.min(index, last)];

  const seek = useCallback(
    (next: number) => setIndex(Math.max(0, Math.min(next, last))),
    [last],
  );

  // Playback stops at the end rather than looping: a replay that quietly
  // restarts looks like a replay that never finished.
  useEffect(() => {
    if (!playing) return;
    if (index >= last) {
      setPlaying(false);
      return;
    }
    const timer = window.setTimeout(() => setIndex((i) => Math.min(i + 1, last)), 900 / speed);
    return () => window.clearTimeout(timer);
  }, [playing, index, last, speed]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "ArrowRight" || e.key === "ArrowDown") { seek(index + 1); e.preventDefault(); }
      if (e.key === "ArrowLeft" || e.key === "ArrowUp") { seek(index - 1); e.preventDefault(); }
      if (e.key === " ") { setPlaying((p) => !p); e.preventDefault(); }
      // The browser's own zoom scales the whole page including the events
      // column; these scale the cards, which is what is actually being read.
      if (e.key === "+" || e.key === "=") { changeZoom(zoom + 0.2); e.preventDefault(); }
      if (e.key === "-") { changeZoom(zoom - 0.2); e.preventDefault(); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [index, seek, zoom, changeZoom]);

  if (!step) {
    return (
      <div className="flex h-screen items-center justify-center text-slate-400">
        This replay has no steps to show.
      </div>
    );
  }

  const viewerSeat = replay.meta.seat;
  const seats = toSeats(step);
  const order = seatOrder(step, viewerSeat);

  const result =
    replay.meta.won === 1 ? <span className="text-green-400">WIN</span>
    : replay.meta.won === 0 ? <span className="text-red-400">LOSS</span>
    : <span className="text-slate-400">DRAW</span>;

  const subtitle = [replay.meta.date, replay.meta.event, replay.meta.format,
                    replay.meta.ranked ? "ranked" : ""].filter(Boolean).join("  ·  ");

  return (
    <div className="flex h-screen flex-col bg-[#12121e] text-slate-200">
      <header className="border-b border-white/10 px-5 py-3">
        <h1 className="text-lg font-semibold">
          {replay.meta.deck || "Deck"} <span className="text-slate-500">vs</span>{" "}
          {replay.meta.opp || "Opponent"} <span className="ml-2">{result}</span>
        </h1>
        <p className="text-[13px] text-slate-400">{subtitle}</p>
        {replay.meta.truncated && (
          <p className="mt-1 text-[13px] text-amber-300">
            This recording is incomplete — the game was closed before the match ended.
          </p>
        )}
      </header>

      <main className="flex min-h-0 flex-1">
        <SidePanel title="Events">
          <EventList steps={replay.steps} currentIndex={index} onSelect={seek} />
        </SidePanel>

        <div
          className="flex min-w-0 flex-1 flex-col gap-2 overflow-y-auto p-4"
          style={{ ["--zoom" as string]: zoom }}
        >
          {order.map((seat) => {
            const split = toPhaseObjects(step, replay.cards, seat);
            const isViewer = viewerSeat !== 0 && seat === viewerSeat;
            return (
              <SeatPanel
                key={seat}
                name={isViewer ? (replay.meta.deck || "You") : (replay.meta.opp || `Player ${seat}`)}
                isViewer={isViewer}
                isActive={step.ap === seat}
                seat={seats.find((s) => s.seat === seat)}
                board={split}
                onHover={(card, grpId) => setHovered({ card, grpId })}
                onLeave={() => setHovered(null)}
              />
            );
          })}
        </div>

        <CardPreview card={hovered?.card ?? null} grpId={hovered?.grpId} />
      </main>

      <ReplayControls
        currentIndex={index}
        totalSteps={replay.steps.length}
        isPlaying={playing}
        playbackSpeed={speed}
        onSeek={seek}
        onPlayPause={() => setPlaying((p) => !p)}
        onSpeed={setSpeed}
        extra={<ZoomControl zoom={zoom} onZoom={changeZoom} />}
      />
    </div>
  );
}
