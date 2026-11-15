# Drop-in: findings and Stage 3 handoff (2026-11-15)

Companion to [drop-in-plan.md](drop-in-plan.md) (status, risks) and [drop-in-spikes.md](drop-in-spikes.md) (spike results). This file records what was found while finishing Stage 2 and starting Stage 3, and what is left.

## 1. Window tiling: works, no change

`tests/session.sh` + `tests/place-window.ps1`: host at x=0, guest at x=656 (640×360 windows), same row. The helper scripts exit with the games.

## 2. Battle drift (finding 8): fixed

**Wrong lead.** The live-battle reproduction in the plan passed `--sosonline-encounter` to the host only, which desyncs everything. Pass it to every PC (`tests/battle-live.sh` does).

**Cause.** The battle start's party dash (`PlayerDashState.UpdateAnticipation`) advances a 0.1 s anticipation by `Time.smoothDeltaTime / 0.1`. `smoothDeltaTime` is Unity's average of that PC's own recent frame times, so it differs in the last bits between PCs. Six frames make exactly 0.1 s: progress was 1.0000004 on the host (dash on frame 6) and 0.9999998 on the guest (frame 7). The whole battle then ran a frame apart, so the guest corrected positions on every snapshot.

Ruled out along the way:
- Asynchronous loads at battle start: `Encounter`, `EncounterIntro` and `CombatManager` calls land on identical frames.
- Physics steps: identical per frame.

**Also wrong, and fixed:**
- Each PC's absolute `Time.time` differs.
- A guest whose title screen is paused (window without focus, timeScale 0) loses frames of game time before lockstep takes over. In one run the clocks were 6 frames apart at the first load.
- `unscaledTime`/`unscaledDeltaTime` are wall-clock.

**Fix:** `Lockstep/GameClock.cs`.
- **Hooks.** Native detours on Unity's icalls (`il2cpp_resolve_icall` + `INativeDetour`, so even inlined callers are covered) for `time`, `timeAsDouble`, `unscaledTime(AsDouble)`, `unscaledDeltaTime`, `smoothDeltaTime` and `frameCount`.
- **Values during lockstep.**
  - `time` is local time plus an offset.
  - Unscaled time advances 1/60 per lockstep frame.
  - `smoothDeltaTime` returns `deltaTime`.
  - `frameCount` is 10,000,000 + the lockstep frame.
- **Base.** Set at session start (the `Start` message) and on the join frame (`DropIn`/`DropInReload`), and re-set on every load release (the `LoadRelease` message carries the base). The clock is kept after a session, and bases only move forward.
- Protocol version 5.

**Result:** a live two-instance battle gives 0 corrections and 0 mismatches in 121 snapshots (before: 10 corrections, 2 mismatches). Spike B's replay is bit-identical to the recording for 3,898 frames, 1,383 of them in battle; the replay adopts the recording's clock at L through `Session.ReleaseClock`.

**Open (latent):** the fixed step is 0.02 s against 1/60 s frames, so the frames that get no physics step depend on each PC's start phase. They were identical in every measured run. `--sosonline-fixedlog=F1,F2` logs the steps per frame.

## 3. Level load running on the join frame: fixed

**Before:** every PC called `ReloadInPlace` over the running load. The barrier's held load was discarded, and the host would wait forever for the joiner's report of that load.

**Now** (`Session.ScheduleReloadInPlace` → `PostponeDropIn`):
- On the join frame, every PC already in checks `LoadingLevel || InBattle`. That state is identical on each of them, so they all decide the same way.
- If either is true, the joining slot leaves on that frame (`SetLeaveFrame`). The host sends `DropInAbort`; the joiner stops lockstep and waits. The drop-in then restarts with a fresh autosave.
- Also fixed: stopping lockstep while a load is held now releases it (`AfterLockstep`).

**Test:** `HOSTFLAGS="--sosonline-loadatjoin --sosonline-nocatchup" tests/dropin.sh`. Postponed at f1908–f1911, joined about 245 frames later; joiner and guest identical to the host.

## 4. Joining from inside your own game: works

**Test:** `JOIN_DELAY=6 JOINFLAGS="--sosonline-play=0 --sosonline-joinafter=15" tests/dropin.sh`. The joiner plays its own save for 15 s, then joins and is replaced in place (Stage 2) or catches up (Stage 3). It is identical to the host afterwards in both cases.

**Caveat:** its own game was the same save and area as the host's. A different area is untested.

## 5. Stage 3 (only the joiner waits): implemented, working on loopback

On by default; `--sosonline-nocatchup` forces Stage 2.

### Flow

**Every PC, at every load release (frame L).**
- `OnFrameApplied` → `AreaLog.SaveAtEntry` autosaves.
- The host keeps a copy of the save file and the index. It also records what the save doesn't hold: party positions, clock base, slot→player map, occupied slots, and which co-op players are playing. From L on it logs every frame's inputs (serialized) and its state snapshots (`Net/AreaLog.cs`).

**Host (`UpdateCatchUpHost`, per pending peer).**
- **None:** if `WantsCatchUp`, send the files, with the L save replacing the current autosave.
- **Files:** once the joiner is ready, `SendCatchUp`: the `CatchUp` message, the log as `InputLog` blocks of 600 frames, the logged snapshots, and the inputs already buffered. Live inputs, snapshots and `LoadRelease` messages then reach the joiner as usual.
- **Running:** on `CaughtUp`, `MergeCaughtUp`: merge frame = applied + Delay + 3, `Merge` to everyone, `AddSlot` everywhere.

**Joiner (`OnCatchUp`).**
- `BeginLockstep` at frame L with its own slot not in the session, then `ReloadInPlace` of the save. `Engine.Hold` keeps frame L from running until the save's load is held at the barrier, so the load ends on L.
- On L: `CatchUpEntry` restores exact positions and calls `Join()` for the co-op players playing at L.
- It replays uncapped (vsync off, `targetFrameRate` −1, cameras off, Wwise volume 0) and checks the host's snapshots as it goes.
- When it is within Delay+2 frames of the newest frame known, for 30 updates, with no desync streak: it sends `CaughtUp`, turns fast-forward off, and on `Merge` arms the auto-join.

**Engine changes (`LockstepEngine`).**
- `Hold` gate.
- `NewestKnownFrame`.
- `OnRemoteFrame` takes a whole logged frame.
- Local input is stored and sent only when the slot is in the session and `target >= joinFrame + Delay`.
- `HoldFrame` also blocks a load's release frame until that load is held on this PC; a fast replay would otherwise get there first.

**Fallbacks to Stage 2 (`ForceStage2`).**
- The area log is invalid:
  - the save wasn't allowed at L;
  - a player joined or left since L;
  - more than 54,000 frames since L.
- The replay diverged (`CatchUpFailed`).
- The catch-up took over 120 s.
- A Stage 2 drop-in started meanwhile (`CancelCatchUp`).
- An area change during the file transfer restarts the catch-up from the new entry.

**Messages:** `CatchUp` 25, `InputLog` 26, `CaughtUp` 27, `Merge` 28, `CatchUpFailed` 29 (plus `DropInAbort` 24).

### Results (`tests/dropin.sh`, three loopback instances)

| Case | Flags | Result |
|---|---|---|
| Free roaming | none | 1,420 frames replayed, caught up 3.6 s after start (1.8 s loading); P3 playing 6 frames after the merge; joiner 129/129 snapshots identical; guest 1 unaffected |
| P2 playing at L | `HOSTFLAGS=--sosonline-reload=1300 JOIN_DELAY=40` | Restarted at the new entry f1504; `Join()` re-created P2 on the joiner; 129/129 identical |
| During a battle | `HOSTFLAGS="--sosonline-encounter=1500 --sosonline-bot" JOINFLAGS=--sosonline-encounter=1500` | Merged and took P3 mid-battle; 129/129 identical |
| Own game | `--sosonline-play=0` | 1,491 frames in 3.3 s; identical |
| Divergence | `JOINFLAGS=--sosonline-hpdiverge=600` | Caught at f630/f660 mid-replay → Stage 2 → joined at f2060; identical afterwards |

**Note for tests:** harness frame actions (`--sosonline-encounter`, `--sosonline-reload`, …) must go to the joiner too; it replays those frames.

### Left for Stage 3

1. **Roster changes since L.** Replay joins and leaves (log `AddSlot`/`SetLeaveFrame` events with their frames and replay them on the joiner) instead of invalidating the log. Today, any join (including a Stage 3 merge) invalidates the log until the next level load, so a second joiner in the same area uses Stage 2.
2. **Joiner's screen during catch-up:** a frozen frame plus a toast. Add an overlay, or keep one camera rendering at a low rate.
3. **Real Steam P2P:** the log blocks and snapshot burst over Valve's relay are untested, and so is everything Steam-related (no second account here).
4. **Catch-up time budget:** the cut-off is a fixed 54,000 frames plus a 120 s timeout. Measure the replay speed on real hardware, and decide on Stage 3 or Stage 2 from (frames since L) ÷ (speed − 60), as the plan says.
5. **Physics step phase** (above). Watch for it and align it if it ever diverges.
6. **Joining from another area of your own game** is untested.
7. **Merging during a battle:** the auto-join retries every 90 frames up to 12 times. Check that a long battle doesn't exhaust it.

## Tools added

| Script / flag | Use |
|---|---|
| `tests/battle-live.sh` | Two-instance battle; prints the guest's corrections and mismatches |
| `tests/dropin.sh` | Three-instance drop-in; `HOSTFLAGS`, `JOINFLAGS`, `JOIN_DELAY`, `QUIT`; compares both guests with the host |
| `--sosonline-noclock` | Disable the shared clock (A/B) |
| `--sosonline-nocatchup` | Force Stage 2 |
| `--sosonline-loadatjoin` | Start a level load the frame before the first join frame |
| `--sosonline-play=SLOT`, `--sosonline-joinafter=S` | Joiner plays its own game first |
| `--sosonline-tracecalls=Class[.Method],…` | Log calls with the lockstep frame. Tracing all of `CombatManager` stalls the boot; trace specific methods |
| `--sosonline-dashprobe`, `--sosonline-fixedlog=F1,F2` | The probes that found finding 8 |

**Run hygiene:**
- Run `tests/display-prefs.sh save` before a test session and `restore` after.
- Delete `SosOnlineTest-*` and `SosOnlineSession-<port>` folders in the save folder afterwards. Leave `SosOnlineSession` and `SosOnlineBackup` alone.
- Don't build while a test game runs: the build copies the DLL into the game folder.
