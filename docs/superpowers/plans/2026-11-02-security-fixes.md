# Security Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the four findings and five minor findings of the 2026-11-02 security review, each one pinned by tests.

**Architecture:** Each fix sits at the narrowest choke point that every path goes through:
- the model class for time-capsule content, used by both server and client;
- `PlayerManager` for admission;
- `EntityRegistry` for parenting;
- the default relay's blacklist;
- a small limiter class for refusal logs;
- the glTF reader's parse step.

No packet layout changes, so `CoopProtocol`'s fingerprint stays the same.

**Tech Stack:** C# 14. `Nitrox.Model` and `Nitrox.Model.Subnautica` target `net472;net10.0`, the server `net10.0`. Tests use MSTest, FluentAssertions and NSubstitute. The installer is Windows PowerShell 5.1.

**Spec:** `docs/superpowers/specs/2026-11-02-security-fixes-design.md`. Findings F1–F4 and M1–M5 are referenced by those ids below.

## Global Constraints

- Code in `Nitrox.Model` and `Nitrox.Model.Subnautica` must compile for **net472**: no `System.Threading.Lock` and no APIs newer than .NET Framework 4.7.2. Use `lock (object)`, and `Regex` with `RegexOptions.Compiled` and no source generators in those projects.
- Do not add, remove or reorder serialized members of any packet or data type: the wire format must stay identical (`CoopProtocolTest` has to keep passing unchanged).
- Test names are sentences in PascalCase, like `ANewTimeCapsuleEntryKeepsItsContent`. Test classes use the production namespace, as `PdaManagerTimeCapsuleTest.cs` does.
- Commit messages are plain imperative sentences, like the repo history ("Say which file to download and that Nitrox is included"), and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Follow `.editorconfig`. Comments explain *why*, in the fork's style.
- Work only in this worktree (`.claude/worktrees/security-fixes`, branch `worktree-security-fixes`).

## Review Focus

1. **A real time capsule from Unknown Worlds still shows its picture.** The real `imageUrl` format was not confirmed offline. Task 1 Step 7 records a real value and checks it against the new rule.
2. **Legitimate moves still work after the cycle guard:** items between containers, docking, entering a Cyclops, moving an entity to its current parent, and moving to no parent. Pinned in Task 3 by `ANormalMoveStillWorks`, `MovingToTheCurrentParentStillWorks` and `MovingToNoParentStillWorks`.
3. **A player who really crashed gets back in once the old session has been silent for 10 s.** The boundary is pinned in Task 2 by `TheSilenceBoundaryIsTenSeconds`.
4. **Saved worlds that already hold unsafe capsule content are cleaned on load,** not only new packets. Pinned in Task 1 by `ALoadedStateIsCleaned`.
5. **Real player models still load under the new limits.** The existing `GlbModelTest`, `GlbDocumentTest` and `RigFixture`-based tests must stay green. Task 6 adds `AChainOfTwoHundredNodesStillLoads`.

## Before you start (once)

- [x] **Install the .NET 10 SDK.** Done: SDK 10.0.403 is installed.
- [x] **Build.** `dotnet build Nitrox.slnx -m:1` succeeds and finds the game at `E:\SteamLibrary\steamapps\common\Subnautica` (version 83031). **Always build with `-m:1`:** a parallel build fails in BepInEx's publicizer, with "Assembly-CSharp.dll is being used by another process".
- [x] **Baseline tests.** On 2026-11-02, before any change: **3024 passed, 13 skipped, 0 failed**.

**How to run tests in this plan:** where a step says `dotnet test Nitrox.Test/Nitrox.Test.csproj [--filter X]`, run `dotnet build Nitrox.Test/Nitrox.Test.csproj -m:1 -nologo -v:minimal` first, then `dotnet test Nitrox.Test/Nitrox.Test.csproj --no-build [--filter X]`.

---

### Task 1: Time-capsule content is safe on both sides (F1)

**Files:**
- Modify: `Nitrox.Model.Subnautica/DataStructures/GameLogic/TimeCapsuleContentData.cs`
- Modify: `Nitrox.Server.Subnautica/Models/GameLogic/PdaManager.cs:24-34` (the `PdaState` setter) and `:95-111` (`AddEncyclopediaEntry`)
- Modify: `NitroxClient/Communication/Packets/Processors/PDAEncyclopediaEntryAddProcessor.cs:60-75` (`SetTimeCapsuleContent`)
- Test: `Nitrox.Test/Model/Subnautica/TimeCapsuleContentDataTest.cs` (new, namespace `Nitrox.Model.Subnautica.DataStructures.GameLogic`)
- Test: `Nitrox.Test/Server/GameLogic/PdaManagerTimeCapsuleTest.cs` (extend)

**Interfaces:**
- Produces, on `TimeCapsuleContentData`:
  - `public static bool IsSafeImageUrl(string? url)`
  - `public void Sanitize()`: cuts `Title` to `MAX_TITLE_LENGTH` and `Text` to `MAX_TEXT_LENGTH`, turns null into `""`, and sets `ImageUrl` to `""` unless `IsSafeImageUrl`. It mutates in place, so the server relays the cleaned object. The public constructor calls `Sanitize()` instead of its own truncation.
- Produces, on `PdaManager`:
  - `public const int MAX_ENCYCLOPEDIA_KEY_LENGTH = 128;`
  - `public const int MAX_ENCYCLOPEDIA_ENTRIES = 4096;`
  - `public const int MAX_TIME_CAPSULE_CONTENTS = 256;`

**URL rule** (`RegexOptions.Compiled`): `\A[A-Za-z0-9_-][A-Za-z0-9_./-]{0,499}\z`, and the text does not contain `..`. Use `\A` and `\z`, not `^` and `$`: in .NET, `$` also matches just before a final newline, and that would let `"a.png\n"` through. That allows `capsule.jpg` and `images/2017/abc_def.jpeg`. It refuses anything with a scheme, a host, `:`, `\`, `%`, spaces or control characters, a leading `/` or `.`, or `..`.

- [ ] **Step 1: Write the failing model tests** in `TimeCapsuleContentDataTest.cs`.
  - `UnsafeImageUrlsAreRefused`, a `[DataTestMethod]` with these `[DataRow]` values, asserting `IsSafeImageUrl(url).Should().BeFalse()`: `"//attacker.example/a.png"`, `@"\\host\share\a.png"`, `"http://x/a.png"`, `"https://x/a.png"`, `"file:///C:/a.png"`, `@"C:\a.png"`, `"/a.png"`, `"../a.png"`, `"a/../../b.png"`, `"%2F%2Fhost/a.png"`, `"a b.png"`, `"a.png\n"`, `""`, `null`, and `new string('a', 501)` (that last one in its own `[TestMethod]`, since a `DataRow` needs a constant).
  - `SafeImageUrlsAreKept`, with `[DataRow]` `"capsule.jpg"`, `"images/capsule.jpg"`, `"2017-05/abc_def.jpeg"`, `"ABC123.PNG"`, asserting `IsSafeImageUrl(url).Should().BeTrue()`.
  - `SanitizeEmptiesAnUnsafeImageUrlAndCutsTheText`: build an instance without the constructor, `(TimeCapsuleContentData)RuntimeHelpers.GetUninitializedObject(typeof(TimeCapsuleContentData))`, which is what arrives over the wire. Set `Key = "c1"`, `Title = new string('t', 1000)`, `Text = new string('x', 10000)`, `ImageUrl = "//evil.example/a.png"`. Call `Sanitize()`. Assert `ImageUrl == ""`, `Title.Length == 120` and `Text.Length == 2000`.
  - `SanitizeMakesMissingTextEmpty`: an uninitialized instance with null fields, after `Sanitize()`, has `Title`, `Text` and `ImageUrl` all `""`.
- [ ] **Step 2: Run the tests and check they fail.** Run `dotnet test Nitrox.Test/Nitrox.Test.csproj --filter "FullyQualifiedName~TimeCapsuleContentDataTest"`. Expected: a compile error saying `IsSafeImageUrl` and `Sanitize` do not exist.
- [ ] **Step 3: Implement** `IsSafeImageUrl` and `Sanitize` in `TimeCapsuleContentData.cs`, and make the constructor call `Sanitize()`.
- [ ] **Step 4: Write the failing server tests** in `PdaManagerTimeCapsuleTest.cs`. Add a helper `Wire(string key, string imageUrl)` that builds an uninitialized instance the way Step 1 does.
  - `AnUnsafeImageUrlIsNotStoredOrRelayed`: `TimeCapsuleContentData content = Wire("capsule-1", "//evil.example/a.png"); manager.AddEncyclopediaEntry("capsule-1", content).Should().BeTrue();`. Assert the stored `ImageUrl` is `""` and `content.ImageUrl` is `""`, since it is the same object the processor relays.
  - `AnOverlongKeyIsRefused`: `AddEncyclopediaEntry(new string('k', 129))` returns `false`, and the key is not in `GetInitialPDAData().EncyclopediaEntries`.
  - `EntriesStopAtTheCap`: 4096 distinct keys all return `true`, and the 4097th returns `false`.
  - `TimeCapsuleContentsStopAtTheCap`: add 257 entries, each with matching content. The 257th call returns `true` (its entry is kept) but `TimeCapsuleContents.Count == 256`.
  - `ALoadedStateIsCleaned`: make a `PdaStateData` whose `TimeCapsuleContents` holds `Wire("c1", "//evil.example/a.png")`, then set `manager.PdaState = state`. Assert `GetInitialPDAData().TimeCapsuleContents[0].ImageUrl == ""`.
- [ ] **Step 5: Implement** in `PdaManager.cs`.
  - In `AddEncyclopediaEntry`, inside the lock, before anything else:
    - return `false` when `entry` is null or empty, longer than 128 characters, or the entry count is already at 4096;
    - call `content.Sanitize()`;
    - store the content only while `TimeCapsuleContents.Count < 256`.
  - In the `PdaState` setter, inside the lock: call `Sanitize()` on every content, and drop contents past 256.
  - Log each refusal once, with `ZLogWarning`.
- [ ] **Step 6: Implement the client side.** In `SetTimeCapsuleContent`, call `content.Sanitize()` right after the null check, before `TimeCapsuleContentProvider.Set`. Both callers go through it: the live packet and `PdaInitialSyncProcessor.cs:64`.
- [ ] **Step 7: Check a real capsule's address.** This step needs the game.
  - Add a temporary `Log.Info($"[TimeCapsule] imageUrl='{imageUrl}'")` in `NitroxPatcher/Patches/Dynamic/PDAEncyclopedia_Add_Patch.cs:23`.
  - Build in Debug, start a world, and open a time capsule in the game.
  - Check that the logged value passes `IsSafeImageUrl`. If it does not, widen the rule just enough for it, add the real value as a `SafeImageUrlsAreKept` row, and tell the user.
  - Remove the temporary log line.
  - If the user cannot run the game now, leave this box unchecked and say so in the final report.
- [ ] **Step 8: Run the tests and check they pass.** Run `dotnet test Nitrox.Test/Nitrox.Test.csproj --filter "FullyQualifiedName~TimeCapsule"`. Expected: all pass, including the existing `TooLongTextIsCutBeforeItIsStored`.
- [ ] **Step 9: Commit:** "Accept only a relative image address for time capsules, on the server and in the game".

### Task 2: Only evict a silent session after the credentials check (F2)

**Files:**
- Create: `Nitrox.Server.Subnautica/Models/Communication/ISessionActivity.cs`
- Modify: `Nitrox.Server.Subnautica/Models/GameLogic/PlayerManager.cs:69-172`
- Modify: `Nitrox.Server.Subnautica/Models/Packets/Processors/MultiplayerSessionReservationRequestProcessor.cs:12, 38-41`
- Modify: `Nitrox.Server.Subnautica/Services/LiteNetLibServerService.cs:23` (implement the interface)
- Modify: `Nitrox.Server.Subnautica/Extensions/ServiceCollectionExtensions.cs:211` (register it)
- Test: `Nitrox.Test/Server/GameLogic/PlayerManagerReservationTest.cs` (new, namespace `Nitrox.Server.Subnautica.Models.GameLogic`)

**Interfaces:**
- Produces, in `ISessionActivity`: `internal interface ISessionActivity { TimeSpan? GetTimeSinceLastPacket(SessionId sessionId); }`. It returns null when the session has no live peer.
  - `LiteNetLibServerService` implements it under `contextLock`, from `PeerContext.Peer.TimeSinceLastPacket`. That value is a float in **milliseconds**.
  - Register it as `.AddSingleton<ISessionActivity>(provider => provider.GetRequiredService<LiteNetLibServerService>())`.
- Produces, on `PlayerManager`:
  - `public static readonly TimeSpan STALE_SESSION_SILENCE = TimeSpan.FromSeconds(10);`
  - `public bool TryFindEvictableSession(SessionId requestingSessionId, IPEndPoint requestingEndPoint, AuthenticationContext authenticationContext, Func<SessionId, TimeSpan?> timeSinceLastPacket, out SessionId staleSessionId)`. It returns true only when all three hold:
    - `CheckAdmission(...)` is null;
    - the existing `TryFindStaleSession` finds a session;
    - `timeSinceLastPacket(id)` is null or at least `STALE_SESSION_SILENCE`.
  - `private MultiplayerSessionReservation? CheckAdmission(SessionId sessionId, IPEndPoint endPoint, AuthenticationContext authenticationContext)`: runs the ban, password and name-regex checks moved out of `ReservePlayerContext`, and returns the rejection or null.
  - `ReservePlayerContext` calls `CheckAdmission` first, then keeps its capacity, hardcore and unique-name checks.
  - `TryFindStaleSession` becomes `private`.

- [ ] **Step 1: Write the failing tests.**
  - **Setup:**
    - A real `SessionManager` (substitute its `ISessionCleaner.Trigger`).
    - A `BanService` that bans nothing: build it with substitutes or nulls for what `IsBanned` does not touch, and if that is not possible, substitute `IsBanned`.
    - `SubnauticaServerOptions { ServerPassword = "secret" }`.
    - "Alice" reserved through `ReservePlayerContext` from `10.0.0.5:1000` with the right password, on the session that `sessionManager.GetOrCreateSession(endPoint)` returns.
    - The request comes from `10.0.0.5:2000`, a second session.
  - **Tests:**
    - `AWrongPasswordNeverEvictsTheSessionThatHoldsTheName`: wrong password, silence 60 s, returns `false`.
    - `AnInvalidNameNeverEvicts`: the name `"x"` (the regex needs 3–25 characters), returns `false`.
    - `ALiveSessionIsNotEvictedEvenWithTheRightPassword`: right password, silence 2 s, returns `false`.
    - `ASilentSessionFromTheSameAddressIsEvicted`: right password, silence 30 s, returns `true`, and `staleSessionId` is Alice's session.
    - `ASessionThatHasNoPeerAnyMoreIsEvicted`: right password, silence `null`, returns `true`.
    - `ASessionFromAnotherAddressIsNeverEvicted`: request from `10.0.0.6`, silence 30 s, returns `false`.
    - `TheSilenceBoundaryIsTenSeconds`: 9.9 s gives `false`, and exactly 10 s gives `true`.
- [ ] **Step 2: Run the tests and check they fail.** Run `dotnet test Nitrox.Test/Nitrox.Test.csproj --filter "FullyQualifiedName~PlayerManagerReservationTest"`. Expected: compile errors for the missing members.
- [ ] **Step 3: Implement** `CheckAdmission`, `TryFindEvictableSession` and `STALE_SESSION_SILENCE` in `PlayerManager.cs`, then `ISessionActivity`, its implementation and its registration.
- [ ] **Step 4: Wire the processor.**
  - Add `ISessionActivity sessionActivity` to the constructor.
  - Replace the `TryFindStaleSession` call at line 38 with `playerManager.TryFindEvictableSession(context.Sender.SessionId, context.Sender.EndPoint, authenticationContext, sessionActivity.GetTimeSinceLastPacket, out SessionId staleSessionId)`.
  - Update the comment above it: why the old session must be silent for 10 s, and why the credentials are checked first.
- [ ] **Step 5: Run the tests and check they pass.** Run the filter from Step 2, then the whole suite. Expected: all pass. `BuddyOxygenTransferProcessorTest` builds `PlayerManager` with nulls and is not affected, because the constructor is unchanged.
- [ ] **Step 6: Commit:** "Check the password before evicting a stale session, and only evict a session that has gone silent".

### Task 3: Refuse parent cycles in the entity registry (F3)

**Files:**
- Modify: `Nitrox.Server.Subnautica/Models/GameLogic/Entities/EntityRegistry.cs:434-517`
- Modify: `Nitrox.Server.Subnautica/Models/Packets/Processors/EntityReparentedProcessor.cs:35`
- Modify: `Nitrox.Model/DataStructures/Unity/NitroxTransform.cs` (`SetParent`)
- Test: `Nitrox.Test/Server/GameLogic/Entities/EntityRegistryParentCycleTest.cs` (new). Build the registry with `new EntityRegistry(NullLogger<EntityRegistry>.Instance)` and the entities the way `CraftingClaimArbiterTest` does.
- Test: `Nitrox.Test/Model/DataStructures/NitroxTransformTest.cs` (new, namespace `Nitrox.Model.DataStructures.Unity`)

**Interfaces:**
- Produces, on `EntityRegistry`:
  - `public bool WouldCreateCycle(NitroxId entityId, NitroxId? newParentId)`.
  - Every `ReparentEntity` overload now returns `bool`: `false` when refused or not found, `true` when done. Existing callers can ignore the result. Do the check **before** `RemoveFromParent`, so a refused move leaves the entity exactly where it was.
- Produces, on `NitroxTransform`: `SetParent` throws `InvalidOperationException` when the new parent is this transform or has it anywhere in its `Parent` chain.

**Cycle walk** (the signature does not fix the algorithm):
```
if newParentId == null: return false
if newParentId == entityId: return true
visited = {}; current = newParentId
while TryGetEntityById(current, out e) and e.ParentId != null:
    if e.ParentId == entityId: return true
    if !visited.Add(e.ParentId): return true   // the registry already holds a cycle: refuse
    current = e.ParentId
return false
```

- [ ] **Step 1: Write the failing registry tests.**
  - `ReparentingAnEntityUnderItselfIsRefusedAndKeepsItsParent`: R is the parent of A. `ReparentEntity(A, A)` returns `false` and does not throw. `A.ParentId == R.Id`, and `R.ChildEntities` still contains A.
  - `ReparentingAnEntityUnderItsOwnChildIsRefused`: R → A → B. `ReparentEntity(A, B)` returns `false`, and the parents are unchanged.
  - `ANormalMoveStillWorks`: A goes from under B to under C. Returns `true`, `A.ParentId == C.Id`, and B no longer lists A.
  - `MovingToTheCurrentParentStillWorks`: returns `true`, and A is listed exactly once under its parent.
  - `MovingToNoParentStillWorks`: `ReparentEntity(A, (Entity?)null)` returns `true`, and `A.ParentId` is null.
  - `AnEntityThatNamesItselfAsItsParentIsAddedWithoutAParent`: `AddOrUpdate` of a new entity whose `ParentId` equals its own `Id` returns without throwing, `ParentId` is null, and `ChildEntities` does not contain the entity.
  - `ASpawnUnderOneOfItsOwnChildrenIsAddedWithoutAParent`: A is registered with child B. `AddOrUpdate` of a new A' (same id as A) whose `ParentId == B.Id` leaves A' without a parent.
- [ ] **Step 2: Write the failing transform tests.**
  - `SettingATransformAsItsOwnParentThrows`: `Action act = () => t.SetParent(t); act.Should().Throw<InvalidOperationException>();`
  - `SettingADescendantAsParentThrows`: given a → b, `a.SetParent(b)` throws.
  - `ANormalParentStillWorks`: `SetParent(other)` works, and `Position` is kept (with `worldPositionStays` true).
- [ ] **Step 3: Run the tests and check they fail.** Run `dotnet test Nitrox.Test/Nitrox.Test.csproj --filter "FullyQualifiedName~ParentCycle|FullyQualifiedName~NitroxTransformTest"`. Expected: compile errors, and for the self-parent tests a crash of the test host with a stack overflow. That crash is the bug itself. If it kills the run, mark those tests `[Ignore]` until Step 4 is done.
- [ ] **Step 4: Implement** `WouldCreateCycle`, the bool-returning `ReparentEntity` overloads, and a guard at the start of `AddToParent`: on a cycle, log a warning, set `entity.ParentId = null` and return. Add the guard in `NitroxTransform.SetParent` too.
- [ ] **Step 5: Don't relay a refused move.** In `EntityReparentedProcessor`, write `if (!entityRegistry.ReparentEntity(packet.Id, packet.NewParentId)) { logger.ZLogWarning(...); return; }` before `SendToOthersAsync`. `SubRootChanged` and `EscapePodChanged` need no change: the registry refuses the cycle, and what they relay only moves the sender's own player.
- [ ] **Step 6: Run the tests and check they pass.** Run the Step 3 filter, then the whole suite. Expected: all pass, including the existing `CraftingClaimArbiterTest` and `CyclopsDroneRestoreTest` reparent tests.
- [ ] **Step 7: Commit:** "Refuse to make an entity its own parent or the child of its own child, which crashed the server".

### Task 4: Never relay server-only packets from a client (F4)

**Files:**
- Modify: `Nitrox.Server.Subnautica/Models/Packets/Processors/DefaultServerPacketProcessor.cs:11-26`
- Test: `Nitrox.Test/Server/Packets/Processors/DefaultServerPacketProcessorTest.cs` (extend)

- [ ] **Step 1: Write the failing tests.**
  - `ADebugConsoleCommandThatAClientSendsIsNotRelayed`: `await relay.Process(context, new DebugConsoleCommand("goal story Goal_Disable_Gun"));` then `packetSender.SentToOthers.Should().BeEmpty();`.
  - `EveryDebugPacketAndTheSavingNoticeAreBlacklisted`: by reflection over `typeof(DebugConsoleCommand).Assembly`, take every non-abstract `Packet` type whose `Name` starts with `"Debug"`, plus `typeof(ServerSaving)`. Assert each one is in `defaultPacketProcessorBlacklist`, read with the same reflection the existing Cyclops test uses. Also assert that the scan found at least 8 types, so it can't pass by finding nothing.
- [ ] **Step 2: Run the tests and check they fail.** Run `dotnet test Nitrox.Test/Nitrox.Test.csproj --filter "FullyQualifiedName~DefaultServerPacketProcessorTest"`. Expected: the two new tests fail.
- [ ] **Step 3: Implement.** Add `DebugConsoleCommand`, `DebugDamageCreature`, `DebugSpawnEntity`, `DebugStartMapPacket`, `DebugStoryDump`, `DebugSyncSamplerControl`, `DebugWarpInto` and `ServerSaving` to the blacklist, with a one-line comment: only the server sends these, from admin commands and the save loop.
- [ ] **Step 4: Run the tests and check they pass.** Same filter. Expected: all pass.
- [ ] **Step 5: Commit:** "Do not relay debug packets and the saving notice from clients".

### Task 5: Rate-limit and clean the refused-connection log (M1)

**Files:**
- Create: `Nitrox.Server.Subnautica/Models/Communication/RefusalLogLimiter.cs`
- Modify: `Nitrox.Model.Subnautica/CoopProtocol.cs` (`ExplainRefusal`)
- Modify: `Nitrox.Server.Subnautica/Services/LiteNetLibServerService.cs:190-201`
- Test: `Nitrox.Test/Server/Communication/RefusalLogLimiterTest.cs` (new), and `Nitrox.Test/Model/Subnautica/CoopProtocolTest.cs` (extend)

**Interfaces:**
- Produces, in `RefusalLogLimiter`:
  - `internal sealed class RefusalLogLimiter`, with `public const int MAX_LOGGED_PER_MINUTE = 20;` and `public const int MAX_TRACKED_ADDRESSES = 1024;`.
  - `public bool ShouldLog(IPAddress address, DateTimeOffset now, out int suppressed)`: logs an address at most once per minute and logs at most 20 lines in any rolling minute. `suppressed` is the number of refusals, from any address, that were not logged since the last line that was.
  - `internal int TrackedAddresses { get; }`, for the test.
  - Thread-safe with a private `lock`: LiteNetLib can call from two receive threads.
  - Forget addresses older than a minute, and clear everything when the count would pass 1024.
- `ExplainRefusal` shows every `char.IsControl` character of the sent key as `?`, after the existing 80-character cut.

- [ ] **Step 1: Write the failing tests.**
  - `TheFirstRefusalOfAnAddressIsLogged`.
  - `TheSameAddressIsLoggedOncePerMinute`: at t0, `true`. At t0+30 s, `false`. At t0+61 s, `true` with `suppressed == 1`.
  - `AFloodFromManyAddressesIsCappedPerMinute`: 25 distinct addresses within one second give exactly 20 `true`. A new address at t0+61 s gives `true` with `suppressed == 5`.
  - `MemoryStaysBounded`: 10,000 distinct addresses at one-second steps, then `TrackedAddresses <= 1024`.
  - `TheRefusalShowsControlCharactersAsQuestionMarks`, in `CoopProtocolTest`: `CoopProtocol.ExplainRefusal("abc\r\n[fake] line")` contains `"abc??[fake] line"` and contains neither `'\r'` nor `'\n'`.
- [ ] **Step 2: Run the tests and check they fail.** Run `dotnet test Nitrox.Test/Nitrox.Test.csproj --filter "FullyQualifiedName~RefusalLogLimiterTest|FullyQualifiedName~CoopProtocolTest"`. Expected: compile errors for the missing members, and a failure for the new `CoopProtocolTest`.
- [ ] **Step 3: Implement** `RefusalLogLimiter` and the change to `ExplainRefusal`. In `OnConnectionRequest`, keep a `RefusalLogLimiter` field and log only when `ShouldLog(request.RemoteEndPoint.Address, DateTimeOffset.UtcNow, out int suppressed)`. When `suppressed > 0`, add ` ({suppressed} more refusals not logged)`. The reject reply stays as it is.
- [ ] **Step 4: Run the tests and check they pass.** Same filter. Expected: all pass, and the other `CoopProtocolTest` tests are unchanged.
- [ ] **Step 5: Commit:** "Log refused connections at most once a minute per address and show control characters as question marks".

### Task 6: Reject bad model files instead of crashing (M2)

**Files:**
- Modify: `Nitrox.Model.Subnautica/DataStructures/GameLogic/PlayerModels/GlbModel.cs:220-249` (`ReadNodes`)
- Modify: `Nitrox.Model.Subnautica/DataStructures/GameLogic/PlayerModels/GlbDocument.cs:179-194` (`GetAccessor`)
- Test: `Nitrox.Test/Model/Subnautica/PlayerModels/GlbModelTest.cs` and `GlbDocumentTest.cs` (extend). Use `SyntheticGlb.Build(tweak)` and `RigFixture`; read `SyntheticGlb.cs` to find the node and accessor indexes.

**Interfaces:**
- Produces, on `GlbModel`: `public const int MAX_NODES = 4096;`. `ReadNodes` throws `GlbException` when:
  - there are more nodes than that;
  - a node lists itself as a child;
  - a node is the child of two parents;
  - the parent links form a cycle. Check this in O(n) with a visited or done marking: no recursion, and no walk longer than the node count.

  Once these checks pass, `PlayerModelRig.MixamoRest`'s recursion is bounded by the depth, so it needs no change.
- Produces, on `GlbDocument`: `public const int MAX_ACCESSOR_VALUES = 16_777_216;`. `GetAccessor` throws `GlbException` when:
  - `count`, the combined byte offset or `byteStride` is negative;
  - `(long)count * components` is greater than `MAX_ACCESSOR_VALUES`.

  This happens before the existing end-of-chunk check, and so before `ReadFloats` allocates anything.

- [ ] **Step 1: Write the failing tests.**
  - In `GlbModelTest`, with `act.Should().Throw<GlbException>()`:
    - `ANodeThatIsItsOwnChildIsRefused`: set node 1's `children` to `[1]`.
    - `ANodeWithTwoParentsIsRefused`.
    - `NodesThatFormACycleAreRefused`: node 1 → 2 and node 2 → 1.
    - `TooManyNodesAreRefused`: 4097 nodes.
  - Also in `GlbModelTest`: `AChainOfTwoHundredNodesStillLoads`, which does not throw. Build it with `RigFixture` or a tweak.
  - In `GlbDocumentTest`, each one calling `ReadFloats` and expecting `GlbException`:
    - `ANegativeAccessorCountIsRefused`;
    - `ANegativeByteStrideIsRefused`;
    - `ANegativeByteOffsetIsRefused`;
    - `AnAccessorWithTooManyValuesIsRefused`: `count = 20_000_000` on a VEC3 accessor. It must throw `GlbException`, not `OutOfMemoryException`.
- [ ] **Step 2: Run the tests and check they fail.** Run `dotnet test Nitrox.Test/Nitrox.Test.csproj --filter "FullyQualifiedName~PlayerModels"`. Expected: the new tests fail, and the cycle test may crash the host with a stack overflow, as in Task 3 Step 3.
- [ ] **Step 3: Implement** the checks in `ReadNodes` and `GetAccessor`.
- [ ] **Step 4: Run the tests and check they pass.** Same filter. Expected: all pass, old and new.
- [ ] **Step 5: Commit:** "Refuse model files with node cycles or impossible accessor sizes instead of crashing the game".

### Task 7: Restore NuGet.config (M4)

**Files:**
- Create: `NuGet.config` (at the repo root)

- [ ] **Step 1: Create the file** with upstream Nitrox's exact content:
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <!-- clear to make sure that everyone uses the same sources -->
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <!-- Resolve all packages from nuget.org by default -->
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```
- [ ] **Step 2: Verify.** Run `dotnet restore Nitrox.slnx --force-evaluate`, then `dotnet build Nitrox.slnx`. Expected: both succeed. If restore fails because a package is not on nuget.org, stop and tell the user: that would explain why the fork removed the file.
- [ ] **Step 3: Commit:** "Restore NuGet.config so packages come only from nuget.org".

### Task 8: Hold the downloaded .NET installer locked while it runs (M3)

**Files:**
- Modify: `docs/coop/installer/install.ps1:182-202` (`Get-VerifiedRuntimeInstaller`, `Install-RuntimeFromMicrosoft`)

**Behaviour:**
- Download into a new folder, `Join-Path $env:TEMP ("MultiNaughtica-runtime-" + [guid]::NewGuid().ToString('N'))`.
- Open the file with `[System.IO.File]::Open($target, 'Open', 'Read', 'Read')`. That is read share only, with no write or delete share, so nobody can change, rename or delete the file while the handle is open.
- Compute SHA512 from that stream with `[System.Security.Cryptography.SHA512]::Create().ComputeHash($stream)`, and compare it as now.
- Keep the stream open across `Start-Process ... -Verb RunAs -Wait`.
- In `finally`, dispose the stream, then remove the folder recursively.
- `Get-VerifiedRuntimeInstaller` then returns the open stream along with the path, or the two functions merge. Either is fine, as long as the stream is open from the hash to the end of the install.

- [ ] **Step 1: Implement** the behaviour above.
- [ ] **Step 2: Check the script parses.** Run `powershell -NoProfile -Command "$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile('docs\coop\installer\install.ps1',[ref]$null,[ref]$e); if ($e) { $e[0].Message; exit 1 }"`. Expected: no output and exit code 0.
- [ ] **Step 3: Check the lock really holds.** In a scratch folder:
  - Copy `C:\Windows\System32\whoami.exe` there.
  - Open it the way the script does, in one PowerShell.
  - From a second PowerShell: `Remove-Item` must fail with "being used by another process", and so must `Set-Content`.
  - With the handle still open, `Start-Process <copy> -Wait` must still run.
- [ ] **Step 4: Manual end-to-end test (the user, optional).** In Windows Sandbox, which has no .NET 10, unzip a release zip built from this branch and run `Install.cmd`. Expected: the runtime installs from Microsoft and the install finishes. Leave this unchecked if not done, and say so.
- [ ] **Step 5: Commit:** "Keep the downloaded .NET installer locked from the checksum check until it has run".

### Task 9: Documentation (M5)

**Files:**
- Modify: `docs/coop/INSTALL.md`: the player install section, and the hosting section around line 49
- Modify: `README.md`: Install step 1

- [ ] **Step 1: Write the zip check.** In `INSTALL.md` (players) and in a short line in README Install step 1, explain how to check the zip: run `Get-FileHash .\MultiNaughtica_<version>_win_x64.zip -Algorithm SHA256` in PowerShell and compare the result with the SHA-256 in the release notes (`package_release.ps1` writes it to `<zip>.sha256`). If the two differ, don't run `Install.cmd`.
- [ ] **Step 2: Write the tunnel warning.** In `INSTALL.md` (hosting): "If you share your server through a tunnel that runs on your own PC (playit.gg, ngrok and the like), every player seems to connect from this PC and becomes admin. Set `LocalhostIsAdmin=false` in `server.cfg`, and use `/login` with an admin password instead."
- [ ] **Step 3: Commit:** "Say how to check the zip's checksum and warn about tunnels making every player admin".

### Task 10: Verify the branch and hand it over

- [ ] **Step 1: Run every test.** Run `dotnet test Nitrox.Test/Nitrox.Test.csproj`. Expected: every test passes, except the ones that already failed in the baseline, and those are unchanged.
- [ ] **Step 2: Check the protocol fingerprint is unchanged.** The existing `CoopProtocolTest` must pass with no edits. Also run `git diff upstream/main -- Nitrox.Model.Subnautica/Packets`: it must not touch any serialized member.
- [ ] **Step 3: Build Release.** Run `dotnet build Nitrox.Launcher/Nitrox.Launcher.csproj -c Release -r win-x64`. Expected: it succeeds. Don't run `package_release.ps1` unless the user asks: it builds a zip for handing out.
- [ ] **Step 4: Report to the user.** List each finding and the commit that fixed it, the test counts before and after, and any box left unchecked: Task 1 Step 7 and Task 8 Step 4 are the likely ones.
- [ ] **Step 5: Push.** Only on the user's go-ahead: `git push -u origin worktree-security-fixes`. Open the PR against `b1tsized/MultiNaughtica` `main`, never against upstream.

## Follow-ups that are the user's call (not tasks)

- Raise `CoopBuild` in `Directory.Build.props` (17 → 18) and update the `NOTICE.md` row before handing out a zip. Players need the client-side fixes (F1, M2) as well as the server ones.
- Report the findings privately as `SECURITY.md` asks: F1, F2 and F4 to the maintainer of `Alexander1170/MultiNaughtica`, and F3 also to the Nitrox project, since it is their bug.
