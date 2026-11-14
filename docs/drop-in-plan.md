# Seamless drop-in / drop-out: plan

Goal: joining and leaving a co-op session should feel like Divinity: Original Sin 2 or Baldur's Gate 3. A friend joins a game in progress from Steam, the players already in it are barely interrupted, and nobody visits the title screen.

Spike instructions are in [drop-in-spikes.md](drop-in-spikes.md).

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
| A richer state hash costs frame time | Stutter every 30 frames | Hash a rotating subset per interval; measure in Stage 0 |
