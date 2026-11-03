# MultiNaughtica: install and play

## You are the friend: five steps

You need Windows 10 or 11 (64 bit) and your own copy of **Subnautica** (the first game; Steam is what has been tested). Everything else is installed for you if it is missing: `README.md` lists exactly what goes on your PC.

1. **Unzip** the zip (right-click it, Extract All) anywhere. Before that, you can check that it is the zip that was published: in the folder of the zip, open PowerShell and run `Get-FileHash .\MultiNaughtica_<version>_win_x64.zip -Algorithm SHA256`, then compare the long number with the SHA-256 in the release notes (or the one your host sent you). If they differ, do not run `Install.cmd`.
2. **Double-click `Install.cmd`** in the unzipped folder. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**. It installs what is missing, finds Subnautica by itself and asks for the **server address your host gave you** (press Enter if you do not have it yet).
3. Wait for **"Done, start MultiNaughtica from your desktop"**.
4. **Start "MultiNaughtica" from your desktop** and press **PLAY MULTIPLAYER**. (The first time, the launcher may ask to restart as administrator: allow it.)
5. In the game choose **Multiplayer**, click your host's server, type your name and press **Join**. The first load takes a minute or two; when the game says "Press any button", press one.

To play the normal single-player game, press **SINGLEPLAYER** in the launcher. To remove the program, press **SINGLEPLAYER** once (it takes the patch out of the game), then run **Uninstall MultiNaughtica** from the Start menu (your worlds and the Microsoft runtimes are kept). Running `Install.cmd` again is safe: it updates in place and keeps your worlds and settings; it asks before it replaces a different co-op build.

If something goes wrong, the installer says why in plain words and writes `%TEMP%\MultiNaughtica-install.log`; send that file to your host. Everybody who plays together must use **the same zip**: the game refuses a server of another build and tells you which one it is.

## Player models (optional)

This build ships **no** player models. If you want the other players to see you as a custom character, put your own `.glb` file into `%APPDATA%\Nitrox\models` and pick it in the game under Options, Nitrox, **Model the others see**. Everybody who should see you in it needs the same file. `PLAYER_MODELS.md` (next to this file in the zip, in `docs/coop` in the repository) tells where to get a model, what the file has to be, how to describe it, and how to use emotes (hold **G**).

## You are the host (this part is for the person who runs the server)

### What you need

- Windows 10 or 11, 64 bit, Subnautica (the first game, updated; build 83031 was used for testing, Nitrox refuses a build older than 82304).
- Run `Install.cmd` yourself first (the steps above): it installs the two Microsoft runtimes (.NET Runtime 10 and ASP.NET Core Runtime 10, x64) if they are missing or too old and sets everything up. If you prefer to do it by hand: `winget install Microsoft.DotNet.Runtime.10` and `winget install Microsoft.DotNet.AspNetCore.10`, unzip into a normal folder (not `Program Files`, not the Subnautica folder) and start `Nitrox.Launcher.exe`. Do not run the launcher of stock Nitrox at the same time: both have the same program name and patch the same game folder.
- Give the friend **the same zip** and its SHA-256 (`Get-FileHash <zip> -Algorithm SHA256` on your copy; `package_release.ps1` also writes it to `<zip>.sha256`), and tell them your server address (below).

### 1. Make a new world with this build

1. Launcher, **Servers**, **CREATE NEW SERVER**. Give it a name and choose the game mode.
2. **Do not reuse a world that was made by a Debug build** (the test builds of this fork): a Debug build writes `SaveInterval=0` for new worlds and then the server never autosaves. Worlds made with this zip use 2 minutes ("AUTO SAVE INTERVAL (s)" on the **Manage** page shows 120).
3. **Manage** on the new world: the port is 11000 (leave it). Set a **PASSWORD** when the server is reachable from the internet (option B below): only letters, digits and the underscore are allowed, anything else makes the server refuse to start. "Enable auto port forwarding" and "Allow LAN Discovery" are **off** in this build on purpose (nothing opens by surprise); a friend on your own network adds the server by its address like any other. Press **SAVE** after a change.
4. Press **Start** on the world's row (or **START SERVER MULTIPLAYER** on its Manage page). When the console says "Server is listening on port 11000 UDP" players can join. Its first lines say which build it is.
5. If Windows Defender Firewall asks whether to let `Nitrox.Server.Subnautica` communicate, allow it on private **and** public networks (Tailscale counts as public).

Saving: every 2 minutes, when the last player leaves, and with `save` in the server console. Stop the server with `stop` in its console (or **STOP SERVER** on the Manage page), not by closing the launcher. Backups of the last 10 saves are kept next to the world (`%APPDATA%\Nitrox\saves\<world>`).

### 2. Play yourself

**PLAY MULTIPLAYER** in the launcher, then in the game **Multiplayer**. The list has "Your server, 127.0.0.1:11000" (the game makes it, the installer keeps it); if it is missing, **Add Server** with address `127.0.0.1`, port `11000`. Players on your own PC are admins of your server, others are not.

### 3. Let the friend reach your server (pick one)

The server listens on **UDP 11000**.

**Option A: Tailscale (no router changes, recommended).** Install Tailscale on both PCs and put them in the same tailnet (invite the friend, or share your PC with their account from the Tailscale admin page). The address to give the friend is the Tailscale address of your PC: the tray icon shows it, or run `tailscale ip -4` (it looks like `100.x.y.z`). Nothing is opened on the internet.

**Option B: port forward.** Give your PC a fixed address on your network (a DHCP reservation) and forward **UDP 11000** to it (a port forward rule on your router: UDP, port 11000, to the PC that runs the server). The address to give the friend is your public IP address (it can change: if the friend suddenly cannot connect, look it up again). This opens a game server to the internet: set a server password and keep Windows updated.

**Not a tunnel on your own PC.** If you share your server through a tunnel that runs on your own PC (playit.gg, ngrok and the like), every player seems to connect from this PC and becomes admin, because players on your own PC are admins. Set `LocalhostIsAdmin=false` in `server.cfg` (in the world's folder) and, in the same file, `AdminPassword=` a password of your own (with no admin password, `/login` is switched off and nobody can become admin from the game). Then type `/login <your admin password>` in the game to get admin.

The friend types the address in `Install.cmd` when it asks (or later in the game: **Multiplayer**, **Add Server**, address, port `11000`). Your own copy of the game always uses `127.0.0.1`.

### 4. A new zip

Remove nothing by hand: the friend runs the new `Install.cmd` (it asks before it replaces a different build). Copy `%APPDATA%\Nitrox\saves\<world>` before you switch to a new zip, because a world saved by one build is not guaranteed to load in a later one.

The first start of an existing world with this build repairs it once (duplicate entities that a restart left behind): the server first zips the world to `backups\server-starts` and copies it completely to `<world>\backups\before-heal-<time>` (a subfolder, not a second world in the launcher); keep both until you have played the repaired world.

## When something goes wrong

- **The installer stops with a sentence**: the sentence says what to do (close the program, unzip the whole zip first, ...). The full log is `%TEMP%\MultiNaughtica-install.log`.
- **"You must install or update .NET to run this application"** when the launcher starts: a runtime is missing or too old; run `Install.cmd` again (it installs or updates them) or see the host section.
- **"Unable to contact the remote server"**: the server is not running, the address is wrong, or UDP 11000 is blocked (Windows Firewall on the host, the router, or Tailscale not connected on one side). Try from the host's own PC first with `127.0.0.1`.
- **"The server refused the connection ... another build"**: the zips differ.
- **A name is reserved** after a crash for 30 seconds: wait and join again.
- **Something desyncs** (a base, a vehicle, a creature in two places): leave and join again; the server keeps the world.
- **Logs** are in `%APPDATA%\Nitrox\logs`: `launcher-<date>.log`, `<date>_server_<world>_000.log` (host) and `game[<player>]-<date>.log`. Send the logs of the day if you report something.

## What this build is not

MultiNaughtica is a fork of Nitrox, shared as it is, with no support promise. It is not an official Nitrox release and has nothing to do with the Nitrox team or Unknown Worlds. It is meant for a few people who know each other: play only with people you trust. If something breaks, the logs (above) say what happened; you can send fixes, but nobody is obliged to answer.
