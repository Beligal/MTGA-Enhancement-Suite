import { getBevelBorderStyle, getCardDisplayColors } from "../phase/cardFrame";
import { PTBox } from "../phase/PTBox";
import type { CardViewProps, PTDisplay } from "../phase/cardProps";
import { CARD_BACK_URL } from "../services/cardImage";
import { useCardArt } from "./useCardArt";

export interface CardTileProps {
  card: CardViewProps;
  /** Arena's card id: what Scryfall indexes the picture by. */
  grpId: number | undefined;
  ptDisplay: PTDisplay | null;
  /** How many identical permanents this tile stands for; 1 hides the badge. */
  count: number;
  summoningSick: boolean;
  onHover: (card: CardViewProps, grpId: number | undefined) => void;
  onLeave: () => void;
}

/**
 * One permanent (or one stacked group of identical permanents).
 *
 * The structure follows phase.rs's `PermanentCard` — art, bevelled frame in the
 * card's colours, P/T in the bottom corner, a 90° rotation when tapped — with
 * the 26 imports that make that file 1,288 lines removed: its stores, its
 * action dispatch, its "can I act now" hooks. In a replay nobody can act.
 *
 * Sizing comes from the `--card-w` / `--card-h` custom properties the row sets,
 * which is phase.rs's convention and the reason this scales without a line of
 * layout arithmetic here.
 */
export function CardTile({
  card,
  grpId,
  ptDisplay,
  count,
  summoningSick,
  onHover,
  onLeave,
}: CardTileProps) {
  const art = useCardArt(grpId, card.name);
  const colors = getCardDisplayColors(card.colorIdentity, card.isLand, card.subtypes);
  const border = getBevelBorderStyle(card.isHidden ? [] : colors, 2);
  const url = card.isAbility ? null : card.isHidden ? CARD_BACK_URL : art.url;

  // A tapped card is turned 90°, so the space it takes up is turned too. The
  // OUTER box carries the swapped dimensions and the inner one does the
  // rotating: a CSS transform does not change the room a parent reserves for
  // its child, so without this the first tapped land overlaps the row below it
  // and leaves a hole where it used to be.
  const slot = card.tapped
    ? { width: "var(--card-h)", height: "var(--card-w)" }
    : { width: "var(--card-w)", height: "var(--card-h)" };

  return (
    <div
      className="relative flex shrink-0 items-center justify-center"
      style={slot}
      onPointerEnter={() => onHover(card, grpId)}
      onPointerLeave={onLeave}
    >
      <div
        className="relative transition-transform duration-200"
        style={{
          width: "var(--card-w)",
          height: "var(--card-h)",
          transform: card.tapped ? "rotate(90deg)" : undefined,
        }}
      >
        <div
          className={`h-full w-full overflow-hidden rounded-[6%] bg-neutral-900 ${
            card.isAttacking ? "ring-2 ring-red-400" : card.isBlocking ? "ring-2 ring-sky-400" : ""
          }`}
          style={border}
        >
          {url ? (
            <img
              src={url}
              alt={card.name}
              loading="lazy"
              draggable={false}
              className="h-full w-full object-cover"
              onError={card.isHidden ? undefined : art.onError}
            />
          ) : (
            <TextTile name={card.name} isAbility={card.isAbility} />
          )}
        </div>

        {summoningSick && (
          <div
            className="pointer-events-none absolute inset-0 rounded-[6%] bg-sky-300/20"
            title="Summoning sickness"
          />
        )}

        {ptDisplay && <PTBox ptDisplay={ptDisplay} />}

        {(card.isAttacking || card.isBlocking) && (
          <div
            className={`absolute left-0 top-0 z-20 rounded-br px-1 text-[9px] font-bold leading-4 ${
              card.isAttacking ? "bg-red-500 text-white" : "bg-sky-500 text-white"
            }`}
            title={card.isAttacking ? "Attacking" : "Blocking"}
          >
            {card.isAttacking ? "ATK" : "BLK"}
          </div>
        )}

        {count > 1 && (
          <div className="absolute -right-1 -top-1 z-20 rounded-full bg-amber-400 px-1.5 text-[10px] font-bold leading-4 text-black">
            ×{count}
          </div>
        )}
      </div>
    </div>
  );
}

/**
 * What a card looks like when there is no picture.
 *
 * This is the **normal** case for tokens and for abilities on the stack, not a
 * rare failure: Scryfall has no entry for most token names, and an ability is
 * not a card at all. So the tile has to be legible rather than apologetic —
 * phase.rs's `CardArtFallback` makes the same call.
 */
function TextTile({ name, isAbility }: { name: string; isAbility: boolean }) {
  return (
    <div
      className={`flex h-full w-full items-center justify-center px-1 text-center leading-tight ${
        isAbility ? "bg-indigo-950 text-indigo-200" : "bg-neutral-800 text-neutral-200"
      }`}
      style={{ fontSize: "clamp(7px, calc(var(--card-h) * 0.085), 11px)" }}
    >
      <span className="line-clamp-4">{name}</span>
    </div>
  );
}
