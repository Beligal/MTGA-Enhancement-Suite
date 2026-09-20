# Code vendored from phase.rs

Part of the MTGA+ replay viewer (`viewer/src/phase/`) is derived from
**phase.rs**, an open-source Magic: The Gathering client.

| | |
|---|---|
| Upstream | https://github.com/phase-rs/phase |
| Commit | `7fee171ca673030afc3c7349cbbad4fd232e96be` (2026-09-20) |
| Licence | MIT **or** Apache-2.0, at your option. We take the **MIT** option. |
| Copyright | Copyright (c) 2024-2026 phase.rs contributors |

`LICENSE-MIT` and `LICENSE-APACHE` are verbatim copies of the upstream files.
`NOTICE-phase` is the upstream `NOTICE`; it attributes phase.rs's *card data* to
MTGJSON. **No card data was taken** — MTGA+ gets card names from Arena's own
database and card images from Scryfall — but the notice is reproduced whole
rather than edited.

## What was taken

Only presentation helpers, for a read-only replay viewer. **None** of phase.rs's
rules engine, AI, multiplayer, deck builder, mana payment, action validation or
server code is here, and none of it is linked.

| File in `viewer/src/phase/` | From | Changes |
|---|---|---|
| `boardSizing.ts` | `client/src/components/board/boardSizing.ts` | verbatim. Note that its `baseWidth` is also a **cap** (`Math.min(1, fit)`), so MTGA+ passes larger base widths than phase.rs does: its board is one fixed-height screen, ours is a column that scrolls |
| `types.ts` | `client/src/adapter/types.ts` | the ~40 lines of `GameObject` / `CardType` / `ManaColor` that the files below read, out of 5,270; same field names |
| `fanGeometry.ts` | `client/src/components/card/fanGeometry.ts` | verbatim |
| `cardFrame.ts` | `client/src/components/card/cardFrame.ts` | mana-pip paths removed (MTGA+ has no pip data); local types |
| `cardProps.ts` | `client/src/viewmodel/cardProps.ts` | reduced to `toCardProps` / `computePTDisplay` / `publicName`; counters, class levels and changeling dropped |
| `battlefieldProps.ts` | `client/src/viewmodel/battlefieldProps.ts` | reduced to `partitionByType` / `groupByName`; `groupKey` trimmed to the fields MTGA+ carries |
| `PTBox.tsx` | `client/src/components/board/PTBox.tsx` | per-source attribution tooltip removed; `useTranslation` removed |
| `ReplayControls.tsx` | `client/src/components/replay/ReplayControls.tsx` | reads props instead of `useReplayStore`; `useTranslation` removed; the speed `<select>` given `color-scheme: dark` and explicit `<option>` colours, because a standalone `file://` page draws the native popup with the system light theme and upstream's `text-slate-100` was white on white |

Every one of those files carries a header naming its origin. Files written by
MTGA+ from scratch are **not** in `viewer/src/phase/` — the directory boundary
is the provenance record.

## Why a subset

phase.rs's board components (`PermanentCard.tsx`, `GameBoard.tsx`,
`StackDisplay.tsx`, ...) are written against its Zustand stores, its action
dispatch and its 5,270-line mirror of the Rust engine's types. A replay has no
actions and no engine. The analysis behind the split — what was reusable, what
was adapted, what was left as a reference — is in
`research/arena-internals/replay-viewer-phase-reuse.md`.
