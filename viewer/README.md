# Replay viewer

The page MTGA+ opens when you click **Replay** in Match history. React +
TypeScript + Tailwind, built by Vite into **one HTML file**.

```bash
cd viewer
npm install
npm run build
```

The build writes `../Plugin/Resources/replay-viewer.html`, which the .csproj
embeds in the assembly. **That built file is committed**, so `dotnet build`
never needs Node — only someone changing the viewer does. Change the viewer,
rebuild it, commit both.

`npm run dev` serves it on its own, but with no data: MTGA+ substitutes the
`__MTGAPLUS_REPLAY_DATA__` placeholder in `index.html` at export time, so the
dev server shows the "no replay data" message. To see a real replay, export one
from the game and open the file it writes next to the `.mtgarep.gz`.

## Layout

```
src/phase/        code derived from phase.rs - see third_party/phase/
src/adapter/      the boundary: an MTGA+ step -> objects shaped like phase.rs's
src/components/   MTGA+'s own UI
src/services/     Scryfall image URLs
```

`src/phase/` is the provenance record: everything in it carries a header naming
the upstream file and what changed, and nothing else in the tree is derived
from phase.rs. The analysis behind that split is in
`research/arena-internals/replay-viewer-phase-reuse.md`.

## What the viewer must not assume

The replay is **the stream one player received**, not a simulation. It has no
actions, no legal moves, no hidden-zone contents and no base characteristics —
which is why hands and graveyards are counts rather than cards, and why
`base_power` / `base_toughness` are null rather than copied from the current
values. Anything the UI cannot honestly derive from the stream, it does not
draw.
