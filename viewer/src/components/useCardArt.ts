import { useEffect, useState } from "react";

import { cardImageCandidates } from "../services/cardImage";

/**
 * The URL to show for a card, walking the candidates as each one fails.
 *
 * Scryfall answers for most Arena ids but not all, so "the image failed" is not
 * the end of the search - it is the signal to try the next way of asking. The
 * index resets when the card changes, otherwise a tile that fell back once
 * would start every later card at the fallback.
 */
export function useCardArt(grpId: number | undefined, name: string): {
  url: string | null;
  onError: () => void;
} {
  const candidates = cardImageCandidates(grpId, name);
  const key = candidates.join("|");
  const [attempt, setAttempt] = useState(0);

  useEffect(() => { setAttempt(0); }, [key]);

  return {
    url: attempt < candidates.length ? candidates[attempt] : null,
    onError: () => setAttempt((a) => a + 1),
  };
}
