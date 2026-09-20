/*
 * Derived from phase.rs - https://github.com/phase-rs/phase
 * Commit 7fee171ca673030afc3c7349cbbad4fd232e96be
 * Copyright (c) 2024-2026 phase.rs contributors
 * Licensed MIT OR Apache-2.0; used here under MIT. See third_party/phase/.
 *
 * Origin: client/src/components/card/cardFrame.ts
 * Changes: mana-pip colour path removed (MTGA+ has no pip data); local CSSProperties import.
 */

import type { CSSProperties } from "react";

const FRAME_COLORS: Record<string, string> = {
  White: "#F5E6C8",
  Blue: "#0E68AB",
  Black: "#2B2B2B",
  Red: "#D32029",
  Green: "#00733E",
  Colorless: "#A8A29E",
};

const LAND_SUBTYPE_COLORS: Record<string, string> = {
  Plains: "White",
  Island: "Blue",
  Swamp: "Black",
  Mountain: "Red",
  Forest: "Green",
};

/**
 * The colors a card's frame should show.
 *
 * phase.rs asks the engine which mana pips a land can actually produce and
 * colors the frame from those. Arena's replay stream carries no such thing, so
 * only the basic-land-subtype branch survives - which is why a Wastes or a
 * fetchland comes out grey here and gold there.
 */
export function getCardDisplayColors(
  color: string[],
  isLand: boolean,
  subtypes: string[],
): string[] {
  if (isLand && color.length === 0) {
    return subtypes.flatMap((s) => (LAND_SUBTYPE_COLORS[s] ? [LAND_SUBTYPE_COLORS[s]] : []));
  }
  return color;
}

export function getFrameColor(colors: string[]): string {
  if (colors.length === 0) return "#8E8E8E";
  if (colors.length >= 2) return "#C9B037";
  return FRAME_COLORS[colors[0]] ?? "#8E8E8E";
}

export function getFrameGradient(colors: string[]): string {
  if (colors.length === 0) return "#8E8E8E";
  if (colors.length === 1) return FRAME_COLORS[colors[0]] ?? "#8E8E8E";
  const hexes = colors.map((c) => FRAME_COLORS[c] ?? "#8E8E8E");
  return `linear-gradient(to right, ${hexes.join(", ")})`;
}

function hexToRgb(hex: string): [number, number, number] {
  const h = hex.replace("#", "");
  return [
    parseInt(h.slice(0, 2), 16),
    parseInt(h.slice(2, 4), 16),
    parseInt(h.slice(4, 6), 16),
  ];
}

function blend(color: string, target: string, amount: number): string {
  const [r1, g1, b1] = hexToRgb(color);
  const [r2, g2, b2] = hexToRgb(target);
  const r = Math.round(r1 + (r2 - r1) * amount);
  const g = Math.round(g1 + (g2 - g1) * amount);
  const b = Math.round(b1 + (b2 - b1) * amount);
  return `rgb(${r}, ${g}, ${b})`;
}

export function frameNeedsLightText(colors: string[]): boolean {
  if (colors.length === 0) return false;
  if (colors.length >= 2) return false; // gold frames use dark text
  const hex = FRAME_COLORS[colors[0]];
  if (!hex) return false;
  const [r, g, b] = hexToRgb(hex).map((c) => {
    const s = c / 255;
    return s <= 0.04045 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  });
  const luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
  return luminance < 0.35;
}

export function getBevelBorderStyle(
  colors: string[],
  borderWidth = 3,
): CSSProperties {
  if (colors.length >= 2) {
    const first = FRAME_COLORS[colors[0]] ?? "#8E8E8E";
    const last = FRAME_COLORS[colors[colors.length - 1]] ?? "#8E8E8E";
    return {
      borderWidth,
      borderStyle: "solid",
      borderTopColor: blend(first, "#ffffff", 0.3),
      borderLeftColor: blend(first, "#ffffff", 0.4),
      borderBottomColor: blend(last, "#000000", 0.3),
      borderRightColor: blend(last, "#000000", 0.4),
    };
  }
  const base = getFrameColor(colors);
  return {
    borderWidth,
    borderStyle: "solid",
    borderTopColor: blend(base, "#ffffff", 0.4),
    borderLeftColor: blend(base, "#ffffff", 0.25),
    borderBottomColor: blend(base, "#000000", 0.4),
    borderRightColor: blend(base, "#000000", 0.25),
  };
}
