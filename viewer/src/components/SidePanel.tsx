import { useCallback, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";

import { clampNumber, readPref, writePref } from "../services/preferences";

const MIN_WIDTH = 180;
const MAX_WIDTH = 640;
const DEFAULT_WIDTH = 320;

/**
 * Both are remembered between visits through `services/preferences`, which
 * explains there why that storage is a convenience and never a requirement.
 */
function readStored(): { width: number; open: boolean } {
  return {
    width: readPref("sidebar.width", (raw) => clampNumber(raw, MIN_WIDTH, MAX_WIDTH), DEFAULT_WIDTH),
    open: readPref("sidebar.open", (raw) => (typeof raw === "boolean" ? raw : null), true),
  };
}

export interface SidePanelProps {
  title: string;
  children: ReactNode;
}

/**
 * The events column: draggable edge, and collapsible so the board can have the
 * whole window.
 */
export function SidePanel({ title, children }: SidePanelProps) {
  const initial = useRef(readStored());
  const [width, setWidth] = useState(initial.current.width);
  const [open, setOpen] = useState(initial.current.open);
  const [dragging, setDragging] = useState(false);
  const panelRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    writePref("sidebar.width", width);
    writePref("sidebar.open", open);
  }, [width, open]);

  const onPointerDown = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    setDragging(true);
    e.preventDefault();
  }, []);

  // The move and release listeners live on the WINDOW, not on the handle.
  //
  // The handle is six pixels wide and the cursor is always ahead of what it is
  // dragging, so it leaves the element immediately; listening on the element
  // would end the drag on the first fast movement. `setPointerCapture` solves
  // that too, but it throws on a pointer id the browser does not recognise,
  // which makes the whole thing untestable without a real mouse. Window
  // listeners have neither problem.
  useEffect(() => {
    if (!dragging) return;

    const onMove = (e: PointerEvent) => {
      const left = panelRef.current?.getBoundingClientRect().left ?? 0;
      setWidth(Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, e.clientX - left)));
    };
    const onUp = () => setDragging(false);

    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    window.addEventListener("pointercancel", onUp);
    return () => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
      window.removeEventListener("pointercancel", onUp);
    };
  }, [dragging]);

  // While dragging, a stray text selection follows the cursor across the whole
  // page and stays selected when the drag ends.
  useEffect(() => {
    if (!dragging) return;
    const previous = document.body.style.userSelect;
    document.body.style.userSelect = "none";
    return () => { document.body.style.userSelect = previous; };
  }, [dragging]);

  if (!open) {
    return (
      <div className="flex w-9 shrink-0 flex-col items-center border-r border-white/10 pt-3">
        <button
          type="button"
          onClick={() => setOpen(true)}
          title={`Show ${title.toLowerCase()}`}
          aria-label={`Show ${title.toLowerCase()}`}
          className="rounded bg-white/10 px-1.5 py-1 text-xs text-slate-300 hover:bg-white/20"
        >
          »
        </button>
        <span
          className="mt-3 text-[10px] uppercase tracking-widest text-slate-600"
          style={{ writingMode: "vertical-rl" }}
        >
          {title}
        </span>
      </div>
    );
  }

  return (
    <div ref={panelRef} className="flex shrink-0" style={{ width }}>
      <div className="flex min-w-0 flex-1 flex-col">
        <div className="flex items-center justify-between border-b border-white/5 px-4 py-1">
          <span className="text-[10px] uppercase tracking-widest text-slate-600">{title}</span>
          <button
            type="button"
            onClick={() => setOpen(false)}
            title={`Hide ${title.toLowerCase()}`}
            aria-label={`Hide ${title.toLowerCase()}`}
            className="rounded px-1.5 text-xs text-slate-500 hover:bg-white/10 hover:text-slate-200"
          >
            «
          </button>
        </div>
        <div className="min-h-0 flex-1">{children}</div>
      </div>

      <div
        role="separator"
        aria-orientation="vertical"
        aria-label={`Resize ${title.toLowerCase()}`}
        onPointerDown={onPointerDown}
        onDoubleClick={() => setWidth(DEFAULT_WIDTH)}
        title="Drag to resize, double-click to reset"
        className={`w-1.5 shrink-0 cursor-col-resize border-r border-white/10 transition-colors ${
          dragging ? "bg-sky-400/60" : "bg-transparent hover:bg-sky-400/30"
        }`}
      />
    </div>
  );
}
