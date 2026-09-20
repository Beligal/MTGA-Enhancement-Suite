/**
 * The handful of view settings the page remembers between visits.
 *
 * `localStorage` here is a convenience and never a requirement. The page is
 * opened from `file://`, where a browser may refuse storage outright, and every
 * exported replay shares that single origin — so a value written while reading
 * one replay is read back while reading another, which is exactly right for
 * "how big do I like the cards" and would be wrong for anything about a
 * specific match. Nothing about a match is kept here.
 *
 * Both directions are wrapped: a failure means "no memory", not an error. The
 * viewer must render correctly having never stored a thing.
 */

const PREFIX = "mtgaplus.replay.";

export function readPref<T>(key: string, parse: (raw: unknown) => T | null, fallback: T): T {
  try {
    const raw = window.localStorage.getItem(PREFIX + key);
    if (raw === null) return fallback;
    const value = parse(JSON.parse(raw));
    return value === null ? fallback : value;
  } catch {
    return fallback;
  }
}

export function writePref(key: string, value: unknown): void {
  try {
    window.localStorage.setItem(PREFIX + key, JSON.stringify(value));
  } catch {
    // Private window, blocked site data, or a browser that will not give a
    // file:// page an origin. Nothing to do and nothing worth saying.
  }
}

export function clampNumber(value: unknown, min: number, max: number): number | null {
  if (typeof value !== "number" || !Number.isFinite(value)) return null;
  return Math.min(max, Math.max(min, value));
}
