# Security fixes from the 2026-11-02 review

The review compared this fork with its Nitrox base (`7815b4e`, see `NOTICE.md`) and read the fork's code. It found no remote code execution and no arbitrary file access. What it did find is below: each finding with the behaviour the fix must give. The threat model is the one in `SECURITY.md`: packets are not authenticated and a player can grief the shared world. These fixes cover what goes beyond that: harm to other players' PCs, crashing the server, and getting around the password.

## Findings to fix

**F1. Time-capsule image address is passed on unchecked (Medium, fork).**
A player sends `PDAEncyclopediaEntryAdd` with `Content.ImageUrl = "//attacker.example/a.png"`. The server stores and relays it, and the other clients hand it to the game. When a victim opens the entry, the game fetches that address: their IP leaks and a file lands in `%APPDATA%\Nitrox\screenshots`. The entry is saved in the world.
*Must:* server and client accept only a relative address made of safe characters: no scheme, no leading `/` or `\`, no `:`, no `%`, no `..`, at most 500 characters. Anything else becomes an empty address, which means no picture. Title and text are cut to 120 and 2000 characters on both sides. Saved worlds that already hold bad content are cleaned when they load. The server also bounds how many encyclopedia entries and time-capsule contents it keeps.

**F2. Kick by name without the password (Medium, fork).**
`MultiplayerSessionReservationRequestProcessor` evicts the "stale" session that holds a name before the ban, password and name checks run. Anyone on the same public IP can kick a player again and again.
*Must:* evict only when the request passes the ban, password and name checks, *and* the old session has sent nothing for at least 10 seconds. A live player can then never be kicked this way. A client that really crashed is still let back in.

**F3. One packet crashes the server through a parent cycle (High, upstream bug).**
`EntityReparented(X, X)`, `SubRootChanged` or `EscapePodChanged` pointing at the sender's own entity, or `EntitySpawnedByClient` with `ParentId == Id` builds a parent cycle. The transform math, or the child walk in `AddOrUpdate`, then recurses until the stack overflows, and the process dies.
*Must:* the registry refuses any parent that is the entity itself or one of its descendants, keeps the entity where it was, and does not relay a refused reparent. `NitroxTransform.SetParent` throws a catchable exception on a cycle instead of recursing.

**F4. Server-only packets are relayed from clients (Low, fork).**
The `Debug*` packets, `DebugStartMapPacket` and `ServerSaving` have no server processor and are not on the do-not-relay list.
*Must:* the default processor never relays them, and a test fails if a new `Debug*` packet is added without being listed.

## Minor findings to fix

**M1. Refused connections flood the log (Low, fork).** Each refused connection writes a warning containing up to 80 characters of text from the sender, with no rate limit, and line breaks are kept. *Must:* each address is logged at most once a minute, with at most 20 refusal lines a minute in total, and the next line logged says how many were left out. Control characters in the sent key are shown as `?`.

**M2. Bad model files crash the game (Low, fork, local files only).** A node that is its own child, or nodes that form a cycle, overflow the stack. Negative or huge accessor counts, offsets or strides allocate gigabytes. *Must:* the reader rejects these with a `GlbException`, which every loader already catches. Limits: 4096 nodes, 16,777,216 values per accessor.

**M3. Installer runs the .NET installer as admin from `%TEMP%` after checking it (Low).** Between the hash check and the elevated start, another program running as the same user could swap the file. *Must:* hold the file open (read share only, no delete share) from the hash through the end of the install, inside a new, unique folder.

**M4. `NuGet.config` was deleted (Low).** Builds use whatever package sources the build machine has configured. *Must:* restore upstream Nitrox's file, which clears the sources and maps every package to nuget.org.

**M5. Documentation.** *Must:* `INSTALL.md` says how to check the zip's SHA-256 against the release notes. It also warns that `LocalhostIsAdmin` makes every player admin when the host uses a tunnel that connects from the same PC (for example playit.gg or ngrok).

## Out of scope

- The fact that packets are unauthenticated in general (accepted in `SECURITY.md`).
- Rich text in capsule text and in nav labels from a malicious server: cosmetic.
- Very deep entity trees in one packet overflowing the upstream serializer: upstream Nitrox's to fix.
- Code-signing the release.
