# MTGA Enhancement Suite

<img width="256" height="256" alt="a_plus" src="https://github.com/user-attachments/assets/39dbf120-a7ff-4bff-aa3f-6cf236bca393" />


[Discord](https://discord.gg/67NhSegmvs)

A [BepInEx 5](https://github.com/BepInEx/BepInEx) mod for **Magic: The Gathering Arena** (Windows) that adds community features Wizards of the Coast hasn't built into the game.

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
4. Register the `mtgaes://` URL scheme for invite links

### Manual Install

1. Download [BepInEx 5 x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) and extract it into your MTGA directory (`C:\Program Files\Wizards of the Coast\MTGA\`)
2. Download `MTGAEnhancementSuite.dll`, `MTGAESBootstrapper.dll`, and `config.json` from the [latest release](https://github.com/Beligal/MTGA-Enhancement-Suite/releases/latest)
3. Place all three files in `<MTGA>\BepInEx\plugins\MTGAEnhancementSuite\`
4. Launch MTGA normally

> **Note:** The bootstrapper handles auto-updates. Future updates will be downloaded automatically and applied on next restart.

## Installation (Linux)
### 1. Install BepInEx (Windows x64)
   + Download the Windows x64 version of BepInEx (do NOT use Linux or x86 builds).
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
+ provided `.dll` files
+ `config.json` (from the MTGA+ release)


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

### Custom Format Lobbies
Create challenge lobbies with format rules (currently **Pauper (Paper Banlist)**, **Historic Pauper**, **Standard Pauper**, **Planar Standard**, and **Modern**). When a player selects a deck, it is validated against the format's legal card list — illegal decks are rejected with a message showing which cards aren't allowed. Format rules are synced between host and joiner in real-time via Firebase, and backed by Scryfall.

### Lobby Invite Links
Share a clickable `https://` link to your lobby. The recipient's browser opens a landing page that launches MTGA and joins the lobby automatically — no friend request needed. If MTGA is already running, the link is forwarded to the existing instance.

### Public Lobby Browser
Mark your lobby as public and it appears in a server browser visible to all Enhancement Suite users. Browse open lobbies, filter by format, see the host name and Bo1/Bo3 setting, and join with one click.

### Format Sync Across Matches
When a lobby host changes the format, the joiner's client updates automatically. Format settings persist across rematches — play multiple games in the same lobby without re-configuring.

### Gameplay Settings
Click the **MTGA+** button and switch to the **Settings** tab to access:
- **Disable Companions** — Hides all pets/companions during matches. No rendering, animations, or sounds.
- **Disable Card Animations** — Removes flashy ETB effects, 3D model popups, and cosmetic card animations. Basic arrival sounds and card draw trajectories are preserved.

Settings are saved automatically and persist across game restarts.

## Usage

1. Launch MTGA — you should see an **MTGA+** button in the top navigation bar
2. Open your **Friends List** and click the **Challenge** button in the top right
3. Use the **format selector** below the coin flip spinner to choose a format
4. Click **Copy Link** to get a shareable invite, or **Make Public** to list in the server browser
5. Click the **MTGA+** button to browse public lobbies or access settings

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
| Backend | Firebase Realtime Database + Cloud Functions |
| Card validation | Scryfall API (daily sync to Firebase) |
| URL handling | Custom `mtgaes://` scheme + TCP IPC |

## License

MIT — originally by Dan Mayer, and unchanged here.
