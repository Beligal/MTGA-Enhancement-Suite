/*
 * Derived from phase.rs - https://github.com/phase-rs/phase
 * Commit 7fee171ca673030afc3c7349cbbad4fd232e96be
 * Copyright (c) 2024-2026 phase.rs contributors
 * Licensed MIT OR Apache-2.0; used here under MIT. See third_party/phase/.
 *
 * Origin: client/src/components/board/PTBox.tsx
 * Changes: the per-source attribution tooltip is gone (it reads the engine's
 * CR 613 layer side-table, which a replay has no equivalent of). The `clamp()`
 * sizing against `--card-w` / `--card-h` is upstream's and is the reason this
 * box stays legible at every card size without a line of JavaScript.
 */

import type { CSSProperties } from "react";

import type { PTColor, PTDisplay } from "./cardProps";

interface PTBoxProps {
  ptDisplay: PTDisplay;
  /** Keeps a creature-planeswalker's P/T clear of its bottom-right loyalty badge. */
  position?: "left" | "right";
}

const COLOR_CLASSES: Record<PTColor, string> = {
  green: "text-green-400",
  red: "text-red-400",
  white: "text-white",
};

const ptBoxStyle = {
  fontSize: "clamp(8px, calc(var(--card-h, 88px) * 0.095), 12px)",
  paddingInline: "clamp(3px, calc(var(--card-w, 63px) * 0.055), 6px)",
  paddingBlock: "clamp(1px, calc(var(--card-h, 88px) * 0.012), 2px)",
} as CSSProperties;

export function PTBox({ ptDisplay, position = "right" }: PTBoxProps) {
  return (
    <div
      className={`absolute bottom-0 z-20 flex items-center gap-px bg-black/80 font-bold leading-none ${position === "left" ? "left-0 rounded-tr" : "right-0 rounded-tl"}`}
      style={ptBoxStyle}
    >
      <span className={COLOR_CLASSES[ptDisplay.powerColor]}>{ptDisplay.power}</span>
      <span className="text-gray-400">/</span>
      <span className={COLOR_CLASSES[ptDisplay.toughnessColor]}>{ptDisplay.toughness}</span>
    </div>
  );
}
