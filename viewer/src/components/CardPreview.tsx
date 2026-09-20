import type { CardViewProps } from "../phase/cardProps";
import { useCardArt } from "./useCardArt";

/**
 * The hovered card, large, in a fixed column.
 *
 * phase.rs floats its preview at the cursor and dismisses it on a poll
 * (`CardPreview.tsx`, 1,703 lines, plus `useCardHover` and a UI store). A fixed
 * column needs none of that and never covers the thing you are pointing at —
 * which matters more here, where the point is to read the board.
 *
 * The column keeps its width whether or not a card is hovered, so the
 * battlefield does not reflow and resize every card each time the pointer
 * crosses one.
 */
export interface CardPreviewProps {
  card: CardViewProps | null;
  grpId: number | undefined;
}

export function CardPreview({ card, grpId }: CardPreviewProps) {
  const art = useCardArt(grpId, card?.name ?? "");
  const url = card && !card.isAbility ? art.url : null;

  return (
    <aside className="hidden w-[260px] shrink-0 border-l border-white/10 p-3 lg:block">
      {card === null ? (
        <p className="pt-4 text-center text-xs text-slate-600">hover a card</p>
      ) : (
        <>
          {url ? (
            <img
              src={url}
              alt={card.name}
              className="w-full rounded-lg"
              onError={art.onError}
            />
          ) : (
            <div className="flex aspect-[63/88] w-full items-center justify-center rounded-lg bg-neutral-800 p-3 text-center text-sm text-neutral-300">
              {card.name}
            </div>
          )}
          <p className="mt-2 text-sm font-semibold text-slate-100">{card.name}</p>
          <p className="text-xs text-slate-400">
            {card.isCreature && card.power !== null && card.toughness !== null
              ? `${card.power}/${card.toughness}`
              : ""}
            {card.damageMarked > 0 && (
              <span className="ml-2 text-red-400">{card.damageMarked} damage</span>
            )}
          </p>
          {card.tapped && <p className="text-xs text-amber-400">tapped</p>}
        </>
      )}
    </aside>
  );
}
