/**
 * Card images, from Scryfall, by **Arena's own card id**.
 *
 * **Not phase.rs's pipeline, and deliberately not.** phase.rs resolves images
 * by Scryfall `oracle_id` against a generated `scryfall-data.json` that its own
 * source says is 36,748,238 bytes, then caches the pictures in IndexedDB
 * through an installable "visual pack" system. Neither half fits here:
 *
 *   - a 37 MB sidecar cannot live inside a single HTML file;
 *   - the page is opened from `file://`, where IndexedDB has no origin it can
 *     rely on. The browser's own HTTP cache does the job instead — these URLs
 *     are immutable and Scryfall serves them with a long lifetime.
 *
 * **Why the id and not the name.** Scryfall indexes cards by Arena's `grpId`
 * (`/cards/arena/:id`), which is exactly what the replay carries. Matching on
 * the printed name instead would have to survive split cards, adventures,
 * double-faced cards and Arena's own spelling; the id survives all of them
 * because it is the same number both sides are talking about. Verified on
 * 2026-09-20: `/cards/arena/93940` is Llanowar Elves, Creature — Elf Druid,
 * which is what our reconstructor independently derived from the replay.
 *
 * `format=image` redirects to the CDN, so the URL goes straight into an
 * `<img src>`: no JSON round-trip, no CORS (an `<img>` needs none), no key.
 * Ids Scryfall does not know — tokens, emblems — simply fail to load, and the
 * caller shows a text tile.
 */

/**
 * Scryfall's generic MTG card back, at a fixed id on its CDN.
 *
 * Hotlinked rather than bundled, which is phase.rs's reasoning and a good one:
 * it keeps WotC-copyrighted raster art out of the repo entirely. The viewer
 * shows this for cards it knows are there and cannot name - the opponent's
 * hand.
 */
export const CARD_BACK_URL =
  "https://backs.scryfall.io/normal/0/a/0aeebaf5-8c7d-4636-9e82-8c27447861f7.jpg";

const BY_ARENA_ID = "https://api.scryfall.com/cards/arena";
const BY_NAME = "https://api.scryfall.com/cards/named";

/**
 * Where to look for a card's picture, best first.
 *
 * The id is not a complete index. Measured on 2026-09-20 against the ids in one
 * real replay: 14 of 17 answered, and three did not - a basic Forest printing,
 * and two cards whose Arena id Scryfall has not mapped. So the name is a real
 * second attempt rather than a formality, and it is the one that saves basic
 * lands, which appear in every game.
 *
 * Callers walk this list on each `<img>` error. A name we invented - the
 * `#12345` placeholder the C# side writes when Arena's database could not name
 * a card - is not a name, and is skipped.
 */
export function cardImageCandidates(grpId: number | undefined, name: string): string[] {
  const urls: string[] = [];
  if (grpId && grpId > 0) urls.push(`${BY_ARENA_ID}/${grpId}?format=image&version=normal`);
  if (name && !name.startsWith("#")) {
    urls.push(`${BY_NAME}?exact=${encodeURIComponent(name)}&format=image&version=normal`);
  }
  return urls;
}
