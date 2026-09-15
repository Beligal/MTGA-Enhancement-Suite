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
- **Force Simplified Cards** — Always draws cards without special treatments (showcase, borderless, alternate art). The artwork is kept. Applies both in matches and in the examine view. Three kinds of printing are left alone on purpose: **basic lands** (no rules text, so there is nothing to make readable), **frameless printings**, whose artwork already contains its own frame — the Kamigawa Japanese lands are the usual example — and the four **full-art** printings, whose artwork does not fit a normal frame.

Settings are saved automatically and persist across game restarts.

### Animation Fast-Forward
Press **F7** during a match to run the animation queue at 100× until it has caught
up. Useful when a long chain of triggers is playing out and you already know the
outcome. It stops on its own once the backlog is drained.

## Usage

1. Launch MTGA — you should see an **MTGA+** button in the top navigation bar
2. Click it to open the panel: gameplay settings and **Export Collection**
3. In the deck builder, the gold button to the right of the filter bar handles **card presets**
4. In the deck list, right-click a **folder header** for folder actions, or select several decks for bulk move and delete
5. Press **F7** in a match to fast-forward animations

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
