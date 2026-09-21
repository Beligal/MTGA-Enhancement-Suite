# MTGA Enhancement Suite

<img width="256" height="256" alt="a_plus" src="https://github.com/user-attachments/assets/39dbf120-a7ff-4bff-aa3f-6cf236bca393" />


A [BepInEx 5](https://github.com/BepInEx/BepInEx) mod for **Magic: The Gathering Arena** (Windows) that adds deck- and collection-management tools Wizards of the Coast hasn't built into the game.

Everything it does runs on your own machine: no account, no server, no network
calls of its own beyond checking this repository for updates.

Bugs and questions: **[open an issue](https://github.com/Beligal/MTGA-Enhancement-Suite/issues)**.

> **Beta** — This mod is in active development. Expect rough edges.

> **This is a continuation of [MTGA Enhancement Suite](https://github.com/MayerDaniel/MTGA-Enhancement-Suite) by Dan Mayer**, who built it and has stepped back from the
> project. It is maintained here with his blessing. All credit for the original
> work is his; releases and updates now come from this repository.

### Arena on Unity 6.3

Arena's 25 August 2026 update moved the client to Unity 6.3, which **crashes the
game on startup** with older builds of the mod — before the plugin even loads, so
there is nothing in the mod's own log to explain it. The cause is BepInEx: 5.4.23.2
cannot hook that engine. Installing the current release (which ships BepInEx
5.4.23.5) fixes it. If you already have the mod and Arena no longer starts, that
is this.

## Installation (Windows)

### Installer (Recommended)

Download **[MTGAPlus-Installer.msi](https://github.com/Beligal/MTGA-Enhancement-Suite/releases/latest/download/MTGAPlus-Installer.msi)** from the [latest release](https://github.com/Beligal/MTGA-Enhancement-Suite/releases/latest) and run it. No dependencies needed.

### Command-Line Install

If Windows SmartScreen blocks the installer, open **PowerShell**, **CMD**, or **Windows Terminal** and run:

```
powershell -c "irm https://raw.githubusercontent.com/Beligal/MTGA-Enhancement-Suite/main/install.ps1 | iex"
```

Both methods will:
1. Find your MTGA installation
2. Install BepInEx 5 (if not already present)
3. Download the latest plugin release

### Manual Install

1. Download [BepInEx 5 x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) and extract it into your MTGA directory (`C:\Program Files\Wizards of the Coast\MTGA\`)
2. Download `MTGAEnhancementSuite.dll` and `MTGAESBootstrapper.dll` from the [latest release](https://github.com/Beligal/MTGA-Enhancement-Suite/releases/latest)
3. Place both files in `<MTGA>\BepInEx\plugins\MTGAEnhancementSuite\`
4. Launch MTGA normally

> **Note:** The bootstrapper handles auto-updates. Future updates will be downloaded automatically and applied on next restart.

## Installation (Linux)
### 1. Install BepInEx (Windows x64)
   + Download **[BepInEx 5.4.23.5 win_x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5)**
     — the Windows x64 build, *not* the Linux or x86 ones. Under Proton the game runs
     as a Windows application, so it needs the Windows build.
   + The version matters: 5.4.23.2 and earlier crash Arena on startup since the
     Unity 6.3 update, and the crash happens before the mod loads, so nothing in
     the mod's log explains it.
   + Extract all contents into the MTGA installation directory: `~/.../steamapps/common/MTGA/`

After extraction, the directory should include:
+ `MTGA.exe`
+ `winhttp.dll`
+ `doorstop_config.ini`
+ `BepInEx/`

✅ -- Ensure `winhttp.dll` exists — this is required for injection.

### 2. Configure Steam Launch Options
In Steam:

> Properties → General → Launch Options

Add:

`WINEDLLOVERRIDES="winhttp=n,b" %command%`

This is required to allow BepInEx/UnityDoorstop to hook correctly under Proton.


### 3. First Launch (Initialize BepInEx)

Launch the game once, then exit.

This allows BepInEx to generate its folder structure.


### 4. Install MTGA+

Place the MTGA+ plugin files into:

`BepInEx/plugins/MTGAEnhancementSuite/`

Files should include:
+ the provided `.dll` files


### 5. Launch the Game

Restart the game via Steam.

If everything is set up correctly:

+ BepInEx will load
+ MTGA+ will initialize
+ Mod functionality should be available in-game


### Notes / Troubleshooting

The error:

`...not compiled for x86 or x64 (might be ARM?)`

can occur under Proton but does not necessarily indicate an architecture issue.

Using the correct `winhttp.dll` and setting WINEDLLOVERRIDES launch option command resolves this.
Ensure you are using a working Proton version (Proton Experimental or recent stable versions recommended).

## Features

### Card Presets
Save any set of cards from the deck builder and drop it into another deck later —
a mana base, a sideboard package, a shell you keep coming back to. The gold
button to the right of the deck editor's filter bar saves the current list and
reopens it anywhere.

### Add from Another Deck
Open the gold button in the deck editor and pick **Add from another deck...** to
browse your saved decks, search them by name, filter them by color, and copy
cards across. Double-click a deck to see its cards: each one shows how many
copies that deck runs and how many you already have in the deck you are
building, colored green when you have none, amber when you have fewer, grey when
you are already covered.

Adding **levels you up to the other deck rather than stacking on top of it** — if
you have one copy and the other deck runs two, you end up with two, not three.
The button always names the number of copies that will actually go in. Nothing is
ticked to start with; **Select missing** ticks everything you are short of in one
go.

Only decks in the same format as the one you are editing are listed, because the
deck builder does not check format legality on its own and cards from another
format would quietly make your deck unplayable. The switch at the bottom shows
the rest, with a warning.

### Deck Name Styling
Arena stores deck names as plain text and never strips formatting tags from
them, so a name can carry color and styling. MTGA+ gives you an editor for it:
in the deck builder pick **Style deck name...** from the gold button's menu, or
**right-click any deck** in the deck list.

You type the name normally - the tags stay out of your way. Select a word,
pick a color, and only that word changes. A live preview shows exactly what
Arena will render, and a counter tracks the one hard limit: **a deck name may
not exceed 64 characters including the tags**, so the editor writes them in the
shortest form that works and refuses to save anything that would overflow.
(Arena's own answer to an over-long name is to cut it at 64, which would slice a
tag in half and leave the name in pieces.)

Besides color there is highlighting, bold, italic, underline, strikethrough,
small caps, upper and lower case, and sliders for size, height, letter spacing
and rotation. A color picker with a hue/saturation square, an opacity bar and a
hex box covers anything the ten preset swatches do not.

From the deck builder the new name is saved with the deck. From the deck list it
is sent straight away, and if the server refuses it the old name is put back.

### Deck Statistics and Match History
**Right-click any deck** in the deck list and pick **Deck statistics**. You get
matches and games played, win rate, how you did on the play and on the draw, a
count per event, and when you last played it. Behind it, **Match history** lists
every match with the date, the result, and who you were up against.

Arena never tells the client what deck the opponent was playing - it is not
hidden, it simply is not sent. So instead of guessing an archetype name from a
list somebody has to maintain, MTGA+ records the **colors you actually saw**:
`vs UB`. A single splashed card would turn a mono-colored deck into two colors,
so a color only makes the label once you have seen **two** cards of it; the odd
single card shows small after a `+`.

Only matches played while MTGA+ is running are recorded - anything from before
is not there. Results are kept per Arena account, one line per match, in
`%APPDATA%\MTGAEnhancementSuite\stats\`.

Draws count as matches played but stay out of the win rate, in both directions.

### Replays
**Off by default.** Click **MTGA+** in the navigation bar, then **Replays…** —
that window lists every recording you have, newest first, whichever deck it was
played with, and the switch to start recording is right there. Each row opens
its replay.

The same replays also appear per deck, in Match history, next to that deck's
numbers.

Clicking it writes a self-contained **HTML page** next to the recording and opens
it in your browser: the events turn by turn down the left, and the board as it
was at that moment on the right - real card art, tapped cards turned sideways,
identical permanents stacked with a count, power/toughness and damage, life
totals, and how many cards each player had in hand, graveyard, exile and
library. Hover a card to see it full size. Arrow keys step through the match,
space plays and pauses, and there is a scrubber along the bottom. There is no
local server and no firewall prompt, and the page is a file you can keep or
send to someone.

Card images come from [Scryfall](https://scryfall.com) as the page loads, so
the first view of a match needs a connection; after that your browser has them
cached. Cards without a picture - tokens, and abilities on the stack - show
their name instead.

**Your own hand is in there.** The recording is the stream your client
received, so it carries the cards you were holding, turn by turn - which is
usually the thing you came back to look at. Your opponent's hand shows as card
backs, because Arena never sent their contents; graveyards and exile are public
and show real cards, and the library stays a number. Attackers and blockers are
marked on the board.

Nothing is guessed: where the viewer shows a count instead of cards, it is
because the count is all your client was ever told.

The events column can be **dragged wider or narrower** by its right edge
(double-click it to reset), and **collapsed** with the `«` button so the board
gets the whole window. **Cards** at the bottom right makes the cards bigger or
smaller (`+` and `-` do the same), and clicking the percentage resets it. It
starts at 60%, which is where both battlefields fit at once on a normal window.
All of it is remembered for next time.

A recording is small: a two-minute game measured **15 KB** on disk. The file
keeps the match stream untouched rather than a trimmed-down version of it, so a
later version of MTGA+ can show more from replays you have already saved.

### Deck Folders and Local Decks
Group your decks into folders, reorder them, and select several at once to move
or delete them in bulk. Local decks live as plain text files on your machine
(`%APPDATA%\MTGAEnhancementSuite\localdecks`), so you can keep more decks than
Arena's own limit and edit them outside the game.

### Collection Export
Writes your whole collection to a text file in Arena's deck-list format
(`4 Card Name (SET) 123`), ready to paste into a deckbuilding site or keep as a
backup. In the **MTGA+** panel, under Settings.

### Gameplay Settings
Click the **MTGA+** button in the navigation bar to open the panel:
- **Disable Companions** — Hides all pets/companions during matches. No rendering, animations, or sounds.
- **Disable Card Animations** — Removes flashy ETB effects, 3D model popups, and cosmetic card animations. Basic arrival sounds and card draw trajectories are preserved.
- **Hide Cards Already in Your Deck** — In the deck builder, drops from the card pool anything you can no longer add, so what is left is what you can still play. Uses Arena's own rule for the "+" button, so per-card exceptions (basic lands, Relentless Rats, the Nazgûl) keep working. Constructed only: never applied while browsing the collection, viewing a deck read-only, in draft and sealed, or while sideboarding in a match.
- **Force Simplified Cards** — Always draws cards without special treatments (showcase, borderless, alternate art). The artwork is kept. Applies both in matches and in the examine view. Three kinds of printing are left alone on purpose: **basic lands** (no rules text, so there is nothing to make readable), **frameless printings**, whose artwork already contains its own frame — the Kamigawa Japanese lands are the usual example — and the four **full-art** printings, whose artwork does not fit a normal frame.

Settings are saved automatically and persist across game restarts.

### Animation Fast-Forward
Press **F7** during a match to run the animation queue at 100× until it has caught
up. Useful when a long chain of triggers is playing out and you already know the
outcome. It stops on its own once the backlog is drained.

### Sparky Training Games
Press **F9** before starting a game against Sparky to choose two things:

- **Which deck Sparky plays** — random, or any of the decks the game exposes.
  The list is read from the game at runtime rather than written by hand, so it
  follows whatever Arena ships.
- **How Sparky plays** — normal AI, never attack, always attack, or just draw
  and pass.

Both choices last for the session only and reset when you restart the game, on
purpose. They apply to games against the bot and have no effect on any PvP
match.

## Usage

1. Launch MTGA — you should see an **MTGA+** button in the top navigation bar
2. Click it to open the panel: gameplay settings and **Export Collection**
3. In the **MTGA+** panel, **Replays…** lists every recorded match and opens any of them
4. In the deck builder, the gold button to the right of the filter bar handles **card presets**, **Add from another deck** and **deck name styling**
5. In the deck list, right-click a **folder header** for folder actions, right-click a **deck** for its statistics or to style its name, or select several decks for bulk move and delete
6. Press **F7** in a match to fast-forward animations
7. Press **F9** before a Sparky game to pick its deck and how it plays

## Uninstalling

Delete the plugin folder:
```
<MTGA>\BepInEx\plugins\MTGAEnhancementSuite\
```

To fully remove BepInEx, also delete `winhttp.dll`, `doorstop_config.ini`, and the `BepInEx\` folder from your MTGA directory.

## Building from Source

Requires the .NET SDK and the MTGA managed assemblies as references. The plugin
targets **netstandard 2.1**: since Unity 6.3, Arena's own assemblies are built
against it, and a net472 target pulls in a netstandard 2.0 facade that the SDK
prefers over any explicit reference, which fails the build (CS1705).

```bash
dotnet build MTGAEnhancementSuite.sln -c Release
```

The build output goes to `Plugin/bin/Release/MTGAEnhancementSuite.dll`.

## Architecture

| Layer | Technology |
|-------|-----------|
| Mod framework | BepInEx 5 (Mono) |
| Patching | HarmonyX |
| Target framework | netstandard 2.1 (matches Arena since Unity 6.3) |
| Storage | Plain JSON and text files, local to your machine |
| Network | GitHub releases, for update checks only |

The mod has **no backend**. Earlier versions (up to 0.19.0) ran private lobbies,
a public lobby browser, custom formats and `mtgaes://` invite links on a Firebase
project belonging to the original author; that service was his, and it went with
him. All of it was removed in 0.20.0, and the mod now does its work entirely on
your machine. Updating from an older version also clears out what it left behind:
the leftover `config.json` and the `mtgaes` registry entry.

## License

MIT — originally by Dan Mayer, and unchanged here.

The replay viewer's UI is partly derived from
[phase.rs](https://github.com/phase-rs/phase), which is dual-licensed MIT or
Apache-2.0 and is used here under MIT. The upstream licence text, the NOTICE
and a file-by-file record of what was taken and what was changed are in
`third_party/phase/`.

Card images are fetched from [Scryfall](https://scryfall.com) by your browser
at view time. No card art is bundled or redistributed; the artwork remains the
property of Wizards of the Coast.
