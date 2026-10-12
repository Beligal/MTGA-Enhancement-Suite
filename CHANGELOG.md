# Changelog

What changed in each version of this fork, newest first. Dates are the day the
release was published on GitHub (Italian time).

Entries say what changed and, where it is not obvious, why. The reasoning behind
a particular decision is usually in a comment at the top of the file that owns
it; the full diff is in `git log`.

---

## 0.23.0 — 2026-10-12

### Added

**Compare decks.** Right-click a deck in the deck list, pick **Compare with...**
and double-click a second deck: one line per card with the copies in each deck,
differences first. "Add from another deck" already had a comparison column, but
it is one-sided by design — it lists the other deck's cards and says what the
open deck is missing, so cards that only the open deck runs never appear. This is
the full comparison: main deck, and sideboard, commander and companion when
either deck has them.

Cards are matched by name, as in "Add from another deck" (both now go through the
same grouping), so different printings of a card are one line. The window can
swap the two sides, pick a different second deck, hide the cards the two share,
and copy the comparison as plain text. Read-only.

Local decks are not supported yet: the only way to read one is Arena's importer,
which lowers every card to the copies you own.

---

## 0.22.0 — 2026-09-22

The largest release of the fork so far.

### Added

**Deck statistics.** Right-click a deck in the deck list for matches, games,
win rate, on-the-play / on-the-draw and a count per event, with a match history
behind it. Nothing is read from `Player.log`: it all comes from `MatchManager`
inside the process, which means it does not depend on Arena's "Detailed Logs"
setting and does not have to race the game for a file.

The opponent is labelled by the **colours actually seen**, not by an archetype
name — Arena does not send the opponent's deck, so an archetype would be a
guess maintained by hand at every set. A colour needs two distinct cards before
it makes the label, because one splashed removal spell used to turn a mono-white
opponent into "WB"; single cards are still shown, smaller, after a `+`. The
per-colour counts are stored rather than just the mask, so that threshold can be
changed later and recomputed over the existing history.

Results are per Arena account, one JSON line per match, in
`%APPDATA%\MTGAEnhancementSuite\stats\<account>\matches.jsonl`.

**Replay recording.** Off by default. What is written is the raw GRE message
stream — a 4-byte length then the protobuf message, repeated, in gzip — with no
transformation of any kind, because a transformation decides now what a future
viewer is allowed to show. Measured on a real match: 244 messages, 82 KB
uncompressed, **15 KB on disk**. The recorder reads its own first file back each
session and reports in the log whether the format held.

**Replay viewer.** Clicking a replay writes a self-contained HTML page next to
the recording and opens it in the browser. No local server and no listening
port, so no firewall prompt, and the page is a file that can be kept or sent to
someone.

It shows the board with real card art, tapped cards turned sideways, identical
permanents stacked with a count, attackers and blockers marked, a scrubbable
timeline, and the hidden zones **for exactly as much as the stream knows**: your
own hand card by card (the stream carries it — measured 7 of 7 known, against 0
of 4 in the opponent's), the opponent's as card backs, graveyards and exile with
real cards because those are public, and the library as a number because the
stream lists its ids but never sends the cards.

The page is a React application in `viewer/`, built by Vite into a single file
and embedded in the assembly. State is reconstructed in C#, not in the browser,
because the stream is made of differences (167 state messages in the test match,
**zero** full ones) and because the card database lives on the C# side. Parts of
the interface derive from [phase.rs](https://github.com/phase-rs/phase),
dual-licensed MIT/Apache-2.0 and used here under MIT — see `third_party/phase/`.

Card images come from Scryfall by Arena card id, with the card name as a second
attempt: the id answered for 14 of 17 cards in the test replay, and basic lands
are among those the name saves.

**Replay management.** The replay list is reachable in two ways — **MTGA+ →
Replays…** for every recording regardless of deck, or a deck's own match
history. From there replays can be selected and deleted, marked "keep" so a
delete-all skips them, and the folder can be opened. The window shows how much
disk they take.

Deleting is the only place the mod removes files at a user's request, so it is
hemmed in: never a folder, never a wildcard, only a file name with no path
separators, only the replay extension and the `.html` page beside it, and a
final check that the resolved path really sits inside the replay folder. The
match history is never touched — `matches.jsonl` is append-only, and the list
simply shows the matches whose file still exists.

**Add from another deck.** In the deck builder, the gold menu lists your other
decks and then their cards, with a comparison column showing how many copies the
open deck already has — counted by **name**, not by printing. Adding **levels
up** rather than summing: only the missing copies go in.

**Deck name styling.** An editor for colour and rich-text tags in deck names,
from the gold menu or by right-clicking a tile. Arena's limit is 64 characters
**including** the tags, and its own remedy for an over-long name is a
`Substring(0, 64)` that would cut a tag in half, so the editor refuses to save
past it.

### Changed

**Replay files are named after the match**, not just its id: date, deck name and
the opponent's colours. The colours are only known once the match is over, so
the file is renamed at the end, before the match is recorded — otherwise the
history would remember the old name.

**Updates are now transactional.** The bootstrapper writes a journal before it
touches the first installed file and keeps backups; the plugin confirms the
update only once it is actually running, with its patches attached. An update
that installs but does not start is therefore rolled back at the next launch.
Previously the backups were deleted in the same `Awake` that applied the update
— that is, before anyone had seen the new build work, so a build that did not
start left nothing to go back to.

**A failed local-deck save now says so.** `LocalDeckStore.Save` reports whether
the write reached the disk, and the deck-builder patch keeps you on the page when
it did not. The previous version swallowed the error and handed back the *old*
deck, so the caller marked the save good, dropped the cached deck and navigated
away — which is how edits were lost.

### Removed

**The combat counter**, with its `F8` shortcut and its `combatTotals` setting
(`Features/CombatTotals.cs`, 727 lines). The Settings panel is back to five
rows; its anchors are hand-written, so the rows below the removed one were moved
up by one step rather than leaving a hole.

---

## 0.21.1 — 2026-09-15

Two fixes to **Force Simplified Cards**, both in the same guard:

- Basic lands and frameless printings are left alone. A frameless printing's
  artwork *contains* its own frame, so simplifying it draws a second one over
  the first — the Kamigawa Japanese lands are the clearest case.
- The four full-art printings are left alone too, for a different reason:
  `GetArtSize` reports `Normal` for anything that is not a token, so artwork
  that is `Full` ends up in a window that is not its own.

---

## 0.21.0 — 2026-09-14

- **Combat counter** during combat, with totals for attackers and for the
  creatures that could still block, including how much damage is stopped and
  how much gets through. (Removed again in 0.22.0.)
- **Sparky controls**: pick the deck the bot plays and how it plays, from `F9`.
  Bot games only.
- **Hide cards already maxed out** in the deck builder, using Arena's own rule
  for the "+" button so per-card exceptions keep working.
- Card views the card database does not know are no longer simplified — they
  were losing their frame and colour.
- Frame-cost measurement extended to the patches that act during a match; a
  "0.00 ms" reading in a duel previously did not mean we cost nothing, only
  that nobody was measuring.

---

## 0.20.x — August and early September 2026

The groundwork that made the fork its own thing:

- **Compatibility with Arena on Unity 6.3**, which broke three things at once:
  BepInEx had to go to 5.4.23.5 / Doorstop 4.5.0, the Input System replaced
  `UnityEngine.Input`, and the plugin had to target `netstandard2.1`.
- **The auto-updater points at this fork**, with its own signing key. A release
  without a signed manifest is not installed, and staged files are deleted when
  a download fails.
- **The server-backed features were removed** — private lobbies, the match
  browser, custom formats, deck validation and the `mtgaes://` scheme, all of
  which depended on a backend this fork does not have.
- Installer fixes: the script upgrades BepInEx instead of only checking that it
  exists, it can be run from disk, and the assemblies carry a real version —
  without one, Windows Installer refuses to replace them.
- Card presets keep their own mana base instead of letting Arena redo it.

Older history is upstream's: see
[MayerDaniel/MTGA-Enhancement-Suite](https://github.com/MayerDaniel/MTGA-Enhancement-Suite).
