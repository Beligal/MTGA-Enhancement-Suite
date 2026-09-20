import { useEffect, useRef } from "react";

import type { ReplayStep } from "../adapter/replayFile";

export interface EventListProps {
  steps: ReplayStep[];
  currentIndex: number;
  onSelect: (index: number) => void;
}

/** `@turn Name` is the reconstructor's marker for "a new turn began". */
function eventText(step: ReplayStep): string {
  return step.ev
    .map((e) => (e.startsWith("@turn ") ? `${e.slice(6)}'s turn` : e))
    .join(" / ");
}

export function EventList({ steps, currentIndex, onSelect }: EventListProps) {
  const activeRef = useRef<HTMLButtonElement | null>(null);

  useEffect(() => {
    activeRef.current?.scrollIntoView({ block: "nearest" });
  }, [currentIndex]);

  let lastHeading: string | null = null;

  return (
    <nav className="h-full overflow-y-auto border-r border-white/10">
      {steps.map((step, i) => {
        const heading = `${step.g}/${step.t}`;
        const showHeading = heading !== lastHeading;
        lastHeading = heading;
        const active = i === currentIndex;

        return (
          <div key={i}>
            {showHeading && (
              <h3 className="sticky top-0 bg-[#12121e] px-4 pb-1 pt-2 text-[11px] uppercase tracking-wider text-slate-500">
                {step.g > 0 && `Game ${step.g} · `}
                {step.t > 0 ? `Turn ${step.t}` : "Start"}
              </h3>
            )}
            <button
              type="button"
              ref={active ? activeRef : undefined}
              onClick={() => onSelect(i)}
              className={`block w-full border-l-[3px] px-4 py-1.5 text-left text-[13px] ${
                active
                  ? "border-sky-400 bg-sky-400/10 text-slate-100"
                  : "border-transparent text-slate-300 hover:bg-white/5"
              }`}
            >
              {eventText(step)}
              <span className="block text-[10px] text-slate-500">
                {[step.ph, step.st].filter(Boolean).join(" · ")}
              </span>
            </button>
          </div>
        );
      })}
    </nav>
  );
}
