# Seamless drop-in / drop-out: plan

Goal: joining and leaving a co-op session should feel like Divinity: Original Sin 2 or Baldur's Gate 3. A friend joins a game in progress from Steam, the players already in it are barely interrupted, and nobody visits the title screen.

Spike instructions are in [drop-in-spikes.md](drop-in-spikes.md).
Findings from finishing Stage 2 and building Stage 3, and the Stage 3 handoff: [drop-in-findings.md](drop-in-findings.md).

## Where the mod stands

**Drop-out is already at the target.** A player leaves on an agreed frame, their character returns to the party AI, and the others never stop.

**Drop-in is the gap.** Today a joiner:
1. is admitted as *waiting*,
2. waits for the party's next area change (or the host's *Let waiting players in now*),
3. then everyone returns to the title screen and reloads the host's autosave.

**Why.** Larian's games stream world state from the host. This mod syncs inputs only (deterministic lockstep), so a newcomer needs a simulation identical to everyone else's. The only way to get one today is a shared save load.

**Approach.** Keep lockstep. Shrink the shared reload in stages until only the joiner notices it.

## Review findings the plan depends on

| # | Finding | Where |
|---|---|---|
| 1 | **The desync check is nearly blind.** The engine reseeds `UnityEngine.Random` every frame, and the state snapshot probes the RNG right after. The probe matches on every PC by construction. Only scene names and party positions are really compared, so a divergence in HP, enemies or inventory goes undetected. | `Lockstep/LockstepEngine.cs` (reseed in `Tick`), `Lockstep/StateSync.cs` (`CaptureLocal`) |
| 2 | **Joiners are refused during a start or reload.** The host answers "try again in a moment"; the guest then leaves the lobby. There is no queue or retry. | `Net/Session.cs` (`Msg.Hello`, `Msg.Reject`) |
| 3 | **A crashed player freezes everyone for 6 s.** The engine blocks the game thread while it waits for input, and the host only drops the slot after 6 s. | `LockstepEngine.Tick`, `Session.OnStalled` |
| 4 | **Taking a character is slow.** After a start the auto-join waits 240 frames, then retries a simulated Join press every 90 frames. | `Session.AutoJoin` |
| 5 | **The reload is manual and timer-driven.** Wall-clock waits (0.5 s, 2 s), guests must already be on the title screen, and the host must start a session from the title screen. | `Session.UpdateDropIn`, `Session.StartFromSave` |
| 6 | **No loading barrier.** Levels finish loading 1–2 frames apart on different PCs. | README, *Not yet verified* |
| 7 | **The whole save folder is sent on every start**, including slots the session doesn't use. | `Game/SaveMirror.PackHostFiles` |
| 8 | **Live sessions drift around battles** (found by Stage 0's state hash). In a two-instance battle the guest corrects all party positions on every snapshot, and facing and enemy positions differ by small amounts afterwards. **Fixed:** each PC's own clock. The party's dash into a battle times its 0.1 s anticipation with `smoothDeltaTime` (Unity's average of that PC's recent frame times) against absolute `Time.time`; six frames make exactly 0.1 s, so float rounding ended it on frame 6 on one PC and frame 7 on the other. `GameClock` makes every time getter read the same on every PC. | `Lockstep/GameClock.cs` |

Finding 1 also means the README's "N of N snapshots identical" results prove less than they read: they confirm scenes and party positions only.

## Stages

Each stage is shippable on its own. Stages 2 and 3 are gated by spikes.

### Stage 0 — Foundations

Needed by every later stage, and by the spikes.

- **Real state hash.** Replace the RNG probe with game state: level id, party HP/MP, gold and inventory, combat actors and their HP. Log each component separately so a mismatch names what diverged.
- **Loading barrier.** Each PC reports "level loaded" with its frame. The host picks a release frame, and every PC resumes the simulation on it.
- **Faster drop detection.** Use Steam's connection status to drop a lost peer at once, and cut the silent-peer stall from 6 s to about 2 s.

Done when: a deliberately injected divergence (for example, one PC's party HP changed by a test flag) is reported within one snapshot interval, and two instances leave every level load on the same frame.

### Stage 1 — Always joinable, one re-sync procedure

No new game behaviour is required; this uses calls the mod already makes.

- **Open to friends.** A setting in the Online menu. The host plays normally with a Steam lobby open. No hosting step from the title screen.
- **One procedure, three uses.** *Re-sync*: autosave on an agreed frame, send that slot to whoever lacks it, reload, start lockstep with the new roster. Use it for:
  - solo → co-op (first friend joins),
  - drop-in (another friend joins),
  - automatic recovery when the state hash reports a desync.
- **Queue joiners.** Never reject for timing. Trigger the re-sync at the first moment the game allows saving, not at the next area change. During a battle or cutscene, show "*name* joins after this".
- **Join from anywhere.** A guest who accepts an invite while in their own game gets one confirmation; the mod then leaves that game for them.
- **Co-op → solo.** When the last guest leaves, lockstep stops on an agreed frame and the host continues in the normal game with the lobby still open.
- **Characters.** Remember which character each Steam ID played, and give it back on rejoin. Join the co-op player directly on a scheduled frame (`SinglePlayerPlusPlayer.Join`) instead of simulating button presses.
- **Send only the slot in use** (plus index and options).

Done when: with the host mid-game and never having opened the Online menu beyond the setting, a friend accepts a Steam invite and both are playing within one reload, with no manual steps on either PC.

### Stage 2 — Reload in place

Gated by **spike A**.

- Load the autosave from gameplay behind the game's own transition, skipping the title screen.
- Restore party positions after the load so nobody snaps back to the area entrance.
- Target: a fade of a few seconds for everyone, comparable to Larian's "player is joining" sync.

Done when: a join during free roaming costs the existing players under 5 s, and they resume where they stood.

### Stage 3 — Only the joiner waits (stretch)

Gated by **spikes B and C**.

- Every PC keeps the input log since the party entered the current area.
- The joiner loads the area-entry autosave, replays that log fast-forwarded with rendering and audio suppressed, and merges into the live session on an agreed frame.
- If the estimated catch-up is too long, or the state hash disagrees at the merge, fall back to Stage 2.

Done when: a third instance joins two that are walking around, and the two never pause.

## Status (2026-11-15)

- **Stage 0: done.** Real state hash, loading barrier (`Lockstep/LoadBarrier.cs`), Steam connection loss and a 2 s silent-peer drop. Steam's connection-loss path is untested (no second Steam account here); the 2 s drop is tested on loopback.
- **Spike A: pass.** Stage 2 is possible.
- **Spike B: pass** since the shared game clock (finding 8): the replay is bit-identical to the recording for all 3,898 frames from the area entry, 1,383 of them in battle.
- **Spike C: pass with cameras off** (about 16× real time).
- **Stage 1: not started.** The plan's own risk table asks for a real two-account Steam test first.
- **Stage 2: done (loopback).** `Session.UpdateDropIn`: autosave on an agreed frame, files to the joiner, then a join frame on which every PC, joiner included, reloads in place with the party's positions. The engine adds slots mid-run (`LockstepEngine.AddSlot`, `Start(startFrame)`). Measured in three-instance runs: 92–95 frames (about 1.6 s) from the join frame to the release, guests playing 5 frames later, the joiner's state identical to the host's from then on. Also takes in two Stage 1 items: joiners queue for the first saveable moment (no area-change wait, no "let them in now"), and nothing is rejected during a drop-in.
  - Co-op players are not in the save and survive an in-place load, so every PC makes the guests leave on the join frame and they rejoin by synced Join press.
  - A guest that is still booting must not drop in (or start a session): the boot sequence later loads the title screen on top of the game. Guests now report ready only once `GameBridge.Booted`.
  - A save older than 5 s when the joiners are ready is replaced by a fresh one.
  - A level load or battle running on the join frame: every PC already in sees it on that frame and postpones; the joining slot leaves again on the same frame, the joiner stops and waits, and the drop-in starts over with a fresh autosave (`--sosonline-loadatjoin`: postponed at f1911, joined at f2155, joiner identical to the host afterwards).
  - Joining from a guest's own gameplay: tested (`--sosonline-play`). The joiner's game is replaced in place; identical to the host afterwards.
- **Finding 8: fixed** (`Lockstep/GameClock.cs`). Live two-instance battle: 0 corrections and 0 mismatches in 121 snapshots (was 10 corrections and 2 mismatches).
- **Stage 3: working (loopback), on by default, falls back to Stage 2.** See *Stage 3 status* below.

### Stage 3 status

How it runs:
- On every load release (frame L) every PC autosaves. The host keeps a copy of that save, what the save doesn't hold (party positions, game clock, slot → player map, co-op players playing), every frame's inputs and its state snapshots from L on (`Net/AreaLog.cs`).
- A joiner gets that save, starts lockstep on frame L behind everyone (the engine holds until the save has loaded), replays the log uncapped with cameras and audio off, and then follows the live inputs as an observer. It checks every host snapshot on the way.
- Once it runs within the input delay of the session, the host picks a merge frame; every PC adds the slot there and the joiner presses Join. Nobody already playing reloads or waits.
- Joins and leaves since L are logged with their frames and replayed on the joiner, so a second joiner in the same area catches up too.
- Falls back to Stage 2 when: no valid log (the entry save wasn't allowed, a Stage 2 drop-in since L, more than 54,000 frames since L), the replay diverges from a host snapshot, it takes over 120 s, or a Stage 2 drop-in starts meanwhile.
- A frame on which a load is released doesn't run before that load has reached its end on this PC (a joiner replaying uncapped would otherwise get there first).

Measured (three loopback instances, `tests/dropin.sh`):

| Case | Result |
|---|---|
| Joiner during free roaming | 1,420 frames replayed, caught up 3.6 s after starting (1.8 s of it loading the save); merged; P3 playing 6 frames later. Joiner identical to the host in all 129 snapshots; guest 1 likewise. |
| Area entry after P2 was playing | The joiner's first catch-up was overtaken by a level load and restarted from the new entry; P2 joined on the joiner at L; identical in all 129 snapshots. |
| Joiner arrives during a battle | The replay crossed the battle start; merged and took P3 mid-battle; identical in all 129 snapshots. |
| Joiner from its own game | Same as from the title screen: 1,491 frames in 3.3 s, identical. |
| Divergence injected during the replay (`--sosonline-hpdiverge`) | Found at the next two host snapshots (f630, f660), mid-replay; fell back to Stage 2 and joined at f2060; identical afterwards. |
| Second joiner in the same area (`tests/dropin-two.sh`) | Replayed the first joiner's merge from the log; both identical in all 169 snapshots. Also with the first joiner gone before the second arrived (join and leave replayed), and with both joining at once (merge applied live mid-replay). |

Not done yet:
- Real Steam P2P (sending the log in 600-frame blocks is untested over the relay).
- Physics steps: the game's fixed step is 0.02 s against 1/60 s frames, so one frame in six has no physics step, and which one depends on when each PC's clock started. Identical in every run measured; not forced.

## Independent pause and menus (tier 1 done)

**Today.** A pause or menu opens in the shared game, stops it for everyone, and only its opener can move in it. A banner now says who and which menu.

**Asked for.** Each player pauses or browses menus on their own while the world keeps running.

**Constraint.** Lockstep needs every PC to run the identical simulation. A menu opened inside one PC's copy of the game changes that copy: the game pauses, the UI player changes, and the menu's own state moves. So that PC would diverge. The game's own menus therefore can't open on one PC only. An independent menu has to be drawn by the mod, outside the simulation, while that player's character gets neutral input (or the party AI) and the world runs on.

| Tier | What | Effort |
|---|---|---|
| 1 | Local pause screen: Resume, Options (audio, video and controls are per-PC settings), Online (leave). The opener's pause and game-menu presses are kept out of the synced input. Also fixes the guest Options desync (findings §7). | Moderate |
| 2 | Read-only browsing: party, stats, equipment, inventory, quests, drawn by the mod from the game state. Reading changes nothing. | Large (own screens) |
| 3 | Changes from a local menu (equip, use an item, relics, party order, shop purchases): each becomes a deterministic command in the input stream, applied on the same frame on every PC through the game's own methods. Conflicts (two players using the last potion) are resolved by command order. | Hard |

**Must stay shared:** saving, return to title, Assign Players, dialogues, cutscenes and battle menus (battle commands are already turns of the shared game).

**Status:** tier 1 done (findings §8). Next: tier 2.

## Order of work

1. Stage 0.
2. Spike A (one day). Decides whether Stage 2 exists.
3. Stage 1. Independent of the spikes, so it can run alongside them.
4. Spike B, then spike C only if B passes. Decides whether Stage 3 exists.
5. Stage 2, then Stage 3, as the spikes allow.

## Not in scope

- **Host migration.** As in Larian's games, the host owns the save; the session ends when the host leaves.
- **Joining mid-battle or mid-cutscene.** Joiners are queued until the game allows saving. Stage 3 may lift this later, since a replaying joiner does not need a save at the join moment.
- **More than three players.** The game's local co-op limit.

## Risks

| Risk | Effect | Mitigation |
|---|---|---|
| A save does not reproduce the live state (spike B fails) | No Stage 3 | Stage 2 is the end state: a short fade for everyone |
| In-place load leaves stale managers or co-op state (spike A fails) | No Stage 2 | Keep the title-screen route, automated and hidden behind an overlay |
| Real Steam P2P has never run end to end | Everything | Test two real accounts before Stage 1 UI work |
| A richer state hash costs frame time | Stutter every 30 frames | Measured: 3.4 ms average, 25.6 ms worst, every 30 frames. Acceptable for now |
| Absolute game time differs between PCs (finding 8) | Small drift in battles; a replaying joiner can never match the host exactly | Done: `GameClock` hooks Unity's time getters (time, unscaled time, smoothDeltaTime, frameCount) and re-bases them on every load release |
| Physics step phase differs between PCs | A `FixedUpdate` on different frames on different PCs | Not seen yet. If it shows up: align the phase on a load release (one frame with an adjusted fixed step) |
