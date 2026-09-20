import { useLayoutEffect, useState } from "react";
import type { RefObject } from "react";

/**
 * The element's own width, kept current as it changes.
 *
 * Card sizes are computed from it, so a stale value means cards sized for a
 * layout that no longer exists. `ResizeObserver` rather than a window listener
 * because the things that change this width are mostly not the window: the
 * events column being dragged or collapsed, the preview column appearing, a
 * scrollbar arriving when the board gets taller.
 *
 * Measuring the element instead of deriving it from a parent's width is the
 * point — padding, borders and scrollbars are exactly the pixels that make a
 * row overflow by a hair, and a subtraction written by hand gets them wrong.
 */
export function useElementWidth(ref: RefObject<HTMLElement | null>, fallback: number): number {
  const [width, setWidth] = useState(fallback);

  useLayoutEffect(() => {
    const node = ref.current;
    if (!node) return;

    const observer = new ResizeObserver((entries) => {
      for (const entry of entries) setWidth(entry.contentRect.width);
    });
    observer.observe(node);

    // Read it once synchronously as well, and not only for tidiness: a
    // ResizeObserver delivers at the end of a rendering frame, so a page that
    // is not being painted - opened in a background tab, or a window behind
    // another one - gets NO callback at all, not even the initial one. The
    // synchronous read means the first layout is right regardless; the
    // observer then keeps it right once the page is actually on screen.
    setWidth(node.clientWidth);

    return () => observer.disconnect();
  }, [ref]);

  return width;
}
