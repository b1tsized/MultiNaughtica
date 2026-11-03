# MultiNaughtica

A multiplayer (co-op) mod for **Subnautica**, for a few friends playing together. It is a **fork of [Nitrox](https://github.com/SubnauticaNitrox/Nitrox)** with extra features and changes on top.

> **This is not an official Nitrox release.** It is not made, endorsed or supported by the Nitrox team, and it has nothing to do with Unknown Worlds Entertainment, the makers of Subnautica. If you want stock Nitrox, get it from the [Nitrox project](https://github.com/SubnauticaNitrox/Nitrox).

**Shared as it is. There is no support, no promise of fixes and no promise to answer issues.** Please read "Things to know" below before you use it.

## What it adds to Nitrox

On top of Nitrox's multiplayer, this fork has (see `docs/coop` for the details of each):

**Vehicles and the Cyclops** (`docs/coop/VEHICLE_UPGRADES.md`)
- New vehicle modules: deeper depth modules for the Seamoth, the Prawn Suit and the Cyclops, a Seamoth afterburner, solar chargers, tow hooks, a Seamoth rider bar, and a Cyclops power share that charges nearby vehicles and draws emergency power from a docked one.
- Two extra module slots on every vehicle, and eight quick slots for the player instead of five (the keys 6, 7 and 8 can be rebound in the options).
- Cyclops crew stations, each for a second player: a navigator (terrain hologram, map console, marks and a sonar ping), a gunner (fires stasis spheres from the hull), an engineer (routes power, manages overheating and damage on a PDA page) and a scout drone you can fly from a drone bay. The author has tested these stations by hand in the game, but they are new: expect rough edges.
- A server setting for the numbers of all of these, in `server.cfg`.

**Teamwork**
- Quick ping: look at something and press the middle mouse button, and everybody sees a marker (`docs/coop/QUICK_PING.md`).
- Buddy oxygen: hold a key next to a teammate who is low on air to share yours (`docs/coop/BUDDY_OXYGEN.md`).
- Crafting from the lockers of a base or a Cyclops (`docs/coop/CRAFTING.md`).
- A line under every other player's name in the PDA's player list (health, food and water, distance, depth, vehicle), a "Saving..." indicator when the server saves, and a "Clear all" button for the PDA's databank badges.

**Player models and emotes** (`docs/coop/PLAYER_MODELS.md`)
- You can wear a custom 3D character that the other players see, and use an emote wheel (hold G). **No model is included**: you bring your own `.glb` file. See "Player models" below.

**Fixes to the synchronisation** of bases, vehicles, creatures, the story and other players' movement, found while playing in small groups. They are not complete, and some things are still wrong.

## What you need before you start

You have to bring these yourself. The installer cannot get them for you:

- **Windows 10 or 11, 64 bit.**
- **Your own copy of Subnautica** (the first game, not Below Zero), installed and up to date. It has been tested on Steam. The installer also looks for Epic and GOG copies, but those have not been tested. Nothing from the game is included in this mod.
- **An internet connection and permission to install software**, if your PC is missing the two Microsoft runtimes listed below. Windows may ask you to allow them.
- Everybody who plays together must use **the same zip**. The game refuses a server of another build and tells you so.

You do **not** need to install Nitrox: this download is a complete, modified Nitrox with its own launcher, server and game client. You do not need BepInEx or QModManager either. Other mods are not supported in multiplayer, and the launcher warns you if it finds them in the game folder. Do not run the launcher of stock Nitrox at the same time: both patch the same game folder.

## What the installer puts on your PC

`Install.cmd` does the following and shows each step as it goes:

| What | Where | When | Removed by the uninstaller? |
|---|---|---|---|
| **Microsoft .NET Runtime 10** (x64, about 80 MB) | `C:\Program Files\dotnet` | Only if it is missing or too old | No, other programs may need it |
| **Microsoft ASP.NET Core Runtime 10** (x64, about 30 MB) | `C:\Program Files\dotnet` | Only if it is missing or too old | No, other programs may need it |
| **MultiNaughtica**, the complete modified Nitrox (about 60 MB) | `%LOCALAPPDATA%\MultiNaughtica`, or a folder you pick | Always | Yes |
| Shortcuts: **MultiNaughtica** on the desktop and in the Start menu, **Uninstall MultiNaughtica** in the Start menu | Desktop and Start menu | Always | Yes |
| The folder of your game | Registry, `HKCU\SOFTWARE\Nitrox`, value `PreferredGamePath` | When it finds the game | No |
| The server address you type | `%APPDATA%\Nitrox\servers` | Only if you type one | No |
| A log of the install | `%TEMP%\MultiNaughtica-install.log`, with a copy in the program folder | Always | Only the copy |

The runtimes come from `winget` if your PC has it, otherwise straight from Microsoft's download site, checked against the checksum Microsoft publishes. The installer also removes the Windows "downloaded from the internet" mark from the program files it copied.

It does **not** install Subnautica or any player model, and it adds no background service and nothing that starts with Windows. It does not change your game either: the launcher does that, as described below.

**Your game files.** When you press **PLAY MULTIPLAYER**, the launcher copies `NitroxPatcher.dll` into `Subnautica_Data\Managed` and adds a small patch to the game's `Assembly-CSharp.dll`. **SINGLEPLAYER** takes the patch out again and leaves the copied file, which the normal game ignores. The uninstaller does not touch the game folder, so press **SINGLEPLAYER** once before you uninstall, or use Steam's "Verify integrity of game files" afterwards.

**Your worlds and settings** are in `%APPDATA%\Nitrox`, which stock Nitrox uses too. Uninstalling keeps them.

## Install

1. Download the `MultiNaughtica_..._win_x64.zip` file from the [Releases page](https://github.com/Alexander1170/MultiNaughtica/releases/latest), under **Assets**. Do not use **Source code (zip)** or the green **Code** button: they only hold the source and install nothing. To check that it is the published zip, run `Get-FileHash .\MultiNaughtica_<version>_win_x64.zip -Algorithm SHA256` in PowerShell and compare the result with the SHA-256 in the release notes; if they differ, do not install it. Unzip the file anywhere (not into the Subnautica folder, not into `Program Files`).
2. Double-click **`Install.cmd`**. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**. It installs the two runtimes if they are missing or too old, finds Subnautica, sets everything up and asks for the server address your host gave you (Enter skips that).
3. Start **MultiNaughtica** from your desktop and press **PLAY MULTIPLAYER**. In the game choose **Multiplayer**, pick the server and join.

`docs/coop/INSTALL.md` (also in the zip) has the details for players and for the host, and what to do when something goes wrong. To remove it, press **SINGLEPLAYER** once to take the patch out of the game, then use **Uninstall MultiNaughtica** from the Start menu; your worlds are kept.

## Hosting a game

1. In the launcher, open **Servers** and create a new server (world). Use a world made with this version.
2. The server listens on **UDP port 11000**. If your friends are not on your own network, either use a VPN such as Tailscale, or **forward UDP 11000** on your router to the PC that runs the server.
3. **Set a world password** (letters, digits and the underscore only) on the world's **Manage** page whenever the server can be reached from the internet.
4. Start the server, and give your friends your address.

## Player models

This version ships **no player models**. To use one, put your own `.glb` file (a humanoid with a Mixamo-style skeleton) into `%APPDATA%\Nitrox\models`, then choose it in the game under Options, Nitrox, **Model the others see**. The other players only see it if they have the same file. With no model installed, the list only shows **Default** and there is no emote wheel. `docs/coop/PLAYER_MODELS.md` (also in the zip) explains where to get a model, what the file has to be, the profile file, tuning and emotes. Only use models you have the right to use.

## Things to know

- **No support.** This is shared as it is. Nobody is obliged to fix bugs or answer questions. You are welcome to fork it and change it.
- I will try to help fix issues that are reported but again there is no promise on the timeline. I am excited to share this with other but I am just one person with a Claude Sub haha but I am happy to try to fix what you report.
- **Play only with people you trust.** The server relays what a client sends to the other clients and does not authenticate it, so a modified client can send things that harm other players or alter the world. Set a password and do not share it with strangers. See `SECURITY.md`.
- **Back up your worlds.** Saves are in `%APPDATA%\Nitrox\saves`. The server keeps its last backups, but a copy of your own is cheap.
- **Written mostly with AI tools.** Most of the code, tests and documentation of this fork were written with AI tools (Claude, from Anthropic), directed and checked by a person and play-tested in small groups. It has had far less play-testing than a mature mod.

## Building from source

You need the .NET 10 SDK and Windows. `dotnet build Nitrox.slnx -c Release` builds everything, `dotnet test Nitrox.Test/Nitrox.Test.csproj` runs the tests, and `docs/coop/tools/package_release.ps1` builds the release zip. This version is build 17 of the fork (`CoopBuild` in `Directory.Build.props`); it is part of the connection key, so two different builds cannot join each other.

## Credits and licence

MultiNaughtica is built on **[Nitrox](https://github.com/SubnauticaNitrox/Nitrox)**, the open source Subnautica multiplayer mod, and owes everything underneath to the Nitrox team and all of its contributors. Please support them. The changes in this fork are described in `NOTICE.md`.

It uses the same licence as Nitrox: the **GNU General Public License v3** (`LICENSE.txt`). The copyright notices of the original authors are kept as they are. Subnautica is a trademark of Unknown Worlds Entertainment; this project is not affiliated with them.
