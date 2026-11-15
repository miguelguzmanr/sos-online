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
- **Running:** on `CaughtUp`, `MergeCaughtUp`: merge frame = applied + Delay + 3, `Merge` to everyone (joiners still replaying included), `AddSlot` everywhere.

**Roster changes since L (`AreaLog.Roster`).**
- The host logs each change the inputs don't reproduce, with its frame: a Stage 3 merge (`MergeCaughtUp`) and a leave after a lost connection (`DropPeer` → `ApplySlotLeave`).
- A leave from the synced pause menu (`LeaveSlotDeterministic`) is input, so the replay reproduces it and it isn't logged.
- The `CatchUp` message carries the list. Right after `BeginLockstep` at L, the joiner applies each change on its frame: `AddSlot` + `RegisterSlotPlayers`, or `SetLeaveFrame` + `MakeLeave`, as the session did. Changes after the `CatchUp` reach a replaying joiner live (`Merge`, `SlotLeft`).
- A load release keeps only the changes after its frame; the occupied slots recorded at L cover the rest.
- A Stage 2 drop-in still invalidates the log: everyone reloads, and the reload's load release starts a new one.

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
  - a Stage 2 drop-in since L (everyone reloaded);
  - more than 54,000 frames since L.
- The replay diverged (`CatchUpFailed`).
- The catch-up took over 120 s.
- A Stage 2 drop-in started meanwhile (`CancelCatchUp`).
- An area change during the file transfer restarts the catch-up from the new entry.

**Messages** (protocol version 6): `CatchUp` 25, `InputLog` 26, `CaughtUp` 27, `Merge` 28, `CatchUpFailed` 29 (plus `DropInAbort` 24).

### Results (`tests/dropin.sh`, three loopback instances)

| Case | Flags | Result |
|---|---|---|
| Free roaming | none | 1,420 frames replayed, caught up 3.6 s after start (1.8 s loading); P3 playing 6 frames after the merge; joiner 129/129 snapshots identical; guest 1 unaffected |
| P2 playing at L | `HOSTFLAGS=--sosonline-reload=1300 JOIN_DELAY=40` | Restarted at the new entry f1504; `Join()` re-created P2 on the joiner; 129/129 identical |
| During a battle | `HOSTFLAGS="--sosonline-encounter=1500 --sosonline-bot" JOINFLAGS=--sosonline-encounter=1500` | Merged and took P3 mid-battle; 129/129 identical |
| Own game | `--sosonline-play=0` | 1,491 frames in 3.3 s; identical |
| Divergence | `JOINFLAGS=--sosonline-hpdiverge=600` | Caught at f630/f660 mid-replay → Stage 2 → joined at f2060; identical afterwards |
| Second joiner, same area | `tests/dropin-two.sh` | A merged at f1714; B replayed that join from the area log and merged at f2997; both 169/169 identical |
| Second joiner after the first left | `AFLAGS=--sosonline-quit=2400 tests/dropin-two.sh` | B replayed A's join (f1734) and leave (f2404); 169/169 identical |
| Two joiners at once | `JOIN2_DELAY=1 tests/dropin-two.sh` | B was replaying when A merged (live `Merge`); both 170/170 identical |

**Note for tests:** harness frame actions (`--sosonline-encounter`, `--sosonline-reload`, …) must go to the joiner too; it replays those frames.

**Bug found on the way (fixed):** the host's save transfer id was global, so a second joiner's transfer made it ignore the first joiner's `Ready`, and the first joiner waited forever. Each peer now keeps the id of its own transfer (`Peer.TransferId`).

### Left for Stage 3

1. ~~Roster changes since L~~: done, see above.
2. ~~Joiner's screen during catch-up~~: done, see *Catch-up screen* below.
3. **Real Steam P2P:** the log blocks and snapshot burst over Valve's relay are untested, and so is everything Steam-related (no second account here).
4. **Catch-up time budget:** the cut-off is a fixed 54,000 frames plus a 120 s timeout. Measure the replay speed on real hardware, and decide on Stage 3 or Stage 2 from (frames since L) ÷ (speed − 60), as the plan says.
5. **Physics step phase** (above). Watch for it and align it if it ever diverges.
6. **Joining from another area of your own game** is untested.
7. **Merging during a battle:** the auto-join retries every 90 frames up to 12 times. Check that a long battle doesn't exhaust it.
8. **Pause-menu leave during a replay:** not logged, on the assumption that the replayed menu input calls `LeaveSlotDeterministic` on the joiner too. Untested; `--sosonline-pauseleave` calls it directly, so it doesn't cover this.

## 6. Catch-up screen: done

`UI/CatchUpScreen.cs`, drawn with `UI/GameUi.cs`, which builds the mod's screens from the game's own UI art.
- **Look.** It uses the pause menu's recipe, measured with `--sosonline-uiprobe`. All the art is in one UI atlas, drawn at the game's 640×360 reference size:
  - **Panel:** tiled `panel-background-t1`, a sliced `panel-ornament-2-full-t1` frame, a `panel-border-rounded` outline and `Shadow-rounded`.
  - **Separators:** two-tone `panel-separator-base` bars.
  - **Fonts:** `copy1056-webfontBitmap` for main text and `kroegerBitmap` in grey for secondary text, both 8 px with the drop-shadow materials.
  - **Highlight:** the cursor's cyan.
- **Isolation.** Everything sits on the mod's own overlay canvas. Nothing is added to the game's UI hierarchy or view stack, so the simulation never sees it. Without the art (before the title screen has loaded it), a plain IMGUI version is drawn instead.
- **Content.** A black screen with one panel showing "Joining X's game", the step (loading the area, catching up, joining the party), a progress bar, frames replayed, replay speed and the estimated seconds left. The replay speed is measured every 0.5 s and the estimate is frames left ÷ (speed − 60). Toasts wait until the screen is gone.
- **Camera.** Overlay canvases only render when a camera renders, and fast-forward turns every camera off, so the screen used to freeze on the last frame drawn. A UI-only camera draws nothing of the scene, clears to black and renders one frame every 0.1 s. Fast-forward skips it. The replay speed is unchanged (1,442 frames in 3.7 s).
- **Fonts.** The bitmap fonts lack some characters (`@`, `›`, most non-Latin scripts). Names are drawn without the characters the font can't draw (`GameUi.Drawable`).

## 7. Pause and menus online: how they work, and a menu banner

**How it works.** There is one game, so a pause is the same on every PC:
- Any player can pause (`PauseManager.TogglePauseMenu(Player)`) or open the main menu. It opens on every PC on the same frame, and the game stops for everyone.
- The game makes that player the UI player (`SinglePlayerPlusManager.uiInputsPlayer`). Only they can move in the menus; everyone else watches the same screens.
- This is the game's own couch co-op behaviour, carried over by lockstep.

**Banner (`UI/MenuStatus.cs`).** It says who has the game paused or a menu open, and which menu:
- Others see, for example, "P2 Alex paused the game: Pause menu > Options. They have the controls until they close it." The player in the menu sees "Paused for everyone: …".
- It is built from the game's art (a small panel at the bottom centre) and draws on every PC.
- It needs no network messages. Every 0.15 s it reads the game's pause flag, the open screens (`UIManager.screensByType`: pause menu, main menu with its section, map, shop, options, controls, Assign Players and others), and the UI player mapped to a slot. All of this is identical on every PC.
- It logs `[menu]` lines for tests (`tests/menu-status.sh`).

**Found: Options opened by a guest desyncs the menus.**
- When a guest opens Options, the game shows that player's controls page, built from the controllers assigned to that player on this PC.
- On the guest's PC that is its keyboard. On the host and the other guests, that player has no controllers, so the page is empty and ignores the guest's Back. Those PCs stayed in Options until the end of the test, while the guest's own PC closed it and went on to the host's main menu.
- The game state stayed identical (0 snapshot differences): only the UI diverged. It would turn into a game-state divergence as soon as the stuck PCs' menus take input differently.
- The same steps by the host stay in sync on all three PCs: they all show the General tab and close on the same frames.
- Fixed: see §8. Outside battles and cutscenes the pause (and its Options) is local now; in a shared pause, Options is blocked for guests.

## 8. Local pause screen (tier 1): done

**What changed.** In free gameplay a pause opens on the player's own PC only, for the host too (`UI/LocalPause.cs`):
- The pause press, and everything pressed while the screen is open, is replaced by neutral input before it is sent (the last stage of `Engine.LocalInputFilter`, after the test scripts). Every PC gets the same input, so the shared game never sees the pause and the world keeps running; the paused player's character stands still.
- Buttons still held when the screen closes are kept out until released, so the closing press doesn't act in the game.
- Free gameplay means no battle, cutscene (`SaveManager.CanSave`), loading, `PauseManager.pauseDisablers`, or shared pause or menu. Everywhere else the press goes to the game as before (shared pause), so cutscene skipping and the like still work.
- The screen: Resume, Options, Leave session (guests) or End session (host), each leave with a confirmation page. Game-styled on the mod's own canvas.

**"P2 is paused".** Others see it in the banner (`MenuStatus`) when no shared menu is open ("P1 and P2 are paused" for several). It comes from `Msg.PauseStatus` (any → host, host relays; display only, never input). A joiner gets the current ones on Welcome. Protocol version 7.

**Leave and End session from the local screen.** They no longer run inside a synced menu, so the host picks the frame:
- Guest: `Msg.LeaveRequest` → the host applies a slot leave on `AppliedFrame + Delay + 2` and sends `SlotLeft`, like a drop-out (logged for joiners).
- Host: `Msg.EndAt` (frame, reason) to everyone; `LockstepEngine.EndAt` stops lockstep at the start of that frame on every PC. Pending joiners get `Stop`.
- That frame is safe: a guest can't be past the frames whose System input the host has sent, and the message arrives before anything the host sends later.

**Options (`UI/LocalOptions.cs`).** Master, music, sound effects, ambience (with environment), voices, brightness, display mode. Applied with the game's setters on `SaveManager.SaveData.optionsSaveData` (`ApplyAudio`, `ApplyVideo`) and written with `SaveManager.SaveOptions` when leaving the page or closing the screen.
- Options files are now each PC's own: `SaveMirror` no longer sends `SOS_Options.sos` and never redirects it into the session copy. Before, a guest loaded the host's options (volumes, display, controls) at session start.
- Left out: language and "Timed Hit HUD feedback" (they may change what the shared game shows or does), and the control mappings (the game's controls pages need its own Options screen).
- Risk to watch: guests now keep their own language too. If text length or language ever drives game timing, that would diverge; nothing seen so far.

**Shared pause, guest Options.** (Untested: see the table.) `PauseMenu.Options` is skipped when the UI player is a guest (synced state, so every PC skips it on the same frame); the guest gets a toast pointing to their own pause screen. Fixes the §7 desync where it can still happen (battles, cutscenes).

**Tests** (scripts in `tests/scripts`, run with `tests/menu-status.sh`; set `HOSTSCRIPT`, `GUESTSCRIPT`, `EXTRA`):

| Scripts | Result |
|---|---|
| `host-local-pause.txt` + `guest-local-pause.txt` | No `[menu]` lines on any PC; `[localpause]` open/close lines; others log and show "P1/P2 paused"; 0 snapshot differences |
| `host-local-end.txt` + `guest-local-leave.txt` | Guest 1 leaves on f2454 on all three PCs; host and joiner stop on f2901; 0 differences up to there |
| `host-local-pause.txt` + `guest-local-options.txt` | Only guest 1's own options file changes (master 10→8, brightness up); host's and joiner's unchanged; nothing in the session copy; 0 differences |
| `host-battle-options.txt` / `host-walk.txt` + `guest-battle-options.txt`, `EXTRA=--sosonline-encounter=1500` | No pause menu opened at all during the battle, for the host (player or System channel) or the guest: the game seems to ignore `UI_Start` there. So the Options guard is in place but untested; it matters for cutscenes or wherever else a shared pause still opens. 0 differences |
| `tests/dropin.sh`, `tests/dropin-two.sh` | Pass, 0 differences |

Not covered: a joiner that catches up *after* a local-screen leave (the leave is in the area log like a drop-out, so it should replay); a leave or end while a joiner is mid-catch-up; changing display mode (only volumes and brightness were exercised). Test scripts mark a held key as repeating on every frame, so a held left/right in a script moves a setting several steps; hardware repeats at Rewired's interval.

**Test games after a session ends:** `--sosonline-quit` counts lockstep frames, which stop at End session or Leave. The harness now counts the frames that were left in Unity frames (at least one second) and quits then.

## 9. Stage 1: done on loopback

**Open to friends** (`Settings.OpenToFriends`, `BepInEx/config/sosonline.coop.cfg`; a toggle in the title screen's Online menu, and the pause menu's Online entry offers it). While on, a Steam lobby opens whenever this PC plays its own game (`Session.AutoOpen`), with no hosting step.

**Solo to co-op** (`Session.UpdateSoloCoop`). A friend connects while the host plays its own game (lobby open, no lockstep):
- Once the friend's ping is measured, lockstep starts right there with the host alone (frame 0, no load), and the friend is queued like any joiner (`Welcome` sent again with `Pending`).
- The friend drops in through the existing paths: a reload in place (Stage 2), or a catch-up (Stage 3) once the host has entered an area since.
- The host's input runs `Delay` frames late from then on, as in any session.

**Co-op to solo.** When the last guest has left (leave, drop-out), the host stops lockstep and keeps playing; the lobby stays open. The next friend starts a new run. A new run clears the area log entirely (`AreaLog.Clear`): its roster changes counted frames of the old run, and a joiner replaying them diverged (found by the test below).

**Re-sync on desync.** A guest whose state check fails twice (`StateSync.Desynced`) sends `ResyncRequest`; the host makes it leave on a frame every PC can reach, then queues it again (`ForceStage2`), and it drops in by a reload in place. Nobody else stops.

**Characters.** The host remembers each guest's character by Steam id (`_lastCharacter`). When the guest begins playing again with another one (the game hands out the next free character), the host schedules `Msg.AssignCharacter` on a safe frame and every PC calls `UnassignCharacter` + `AssignCharacter` there, if that character is still free (not the leader, nobody else's). The area log is invalidated and running catch-ups cancelled, since a replay can't reproduce it.
- **Not done: a direct `SinglePlayerPlusPlayer.Join` on a scheduled frame.** The synced Join press stays. A press is input, so a Stage 3 replay reproduces it for free; a direct call would have to be logged for every kind of join (start, reload in place, merge) to keep catch-up working. The press's slowness (finding 4) is cosmetic.

**Only the session's slot is sent** (`SaveMirror.PackHostFiles(only)`): the index plus the slot or autosave in use (2 files in the tests instead of the whole folder). If a name isn't found on disk, everything is sent as before.

**Join from a Steam invite while playing** (`SteamInvites`): one confirmation popup ("Join your friend's game? Your own game stops here…"), then the mod leaves this PC's session if any and joins; the drop-in replaces this PC's game in place. Not offered while this PC is in a running co-op session (a popup there would be a game view on one PC only); the toast asks to leave first.

**Fixed along the way:**
- A joiner could be stuck in the drop-in queue forever: when the party changed area during a reload drop-in, the joiner stayed on the stale joiner list, which also blocked the catch-up the new area made possible. The list is now cleared when the drop-in starts over.
- A PC whose own game stood still for a while (a save loading while it boots) dropped its peers as timed out right after. Time the game stood still is no longer counted as anyone's silence.

**Tests:**

| Script | Result |
|---|---|
| `tests/stage1.sh` (host plays its own save; A joins, gets GARL back with `--sosonline-rememberchar=last`, leaves from the pause screen; host back to solo; B joins later) | Co-op starts from the host's game twice; A dropped in by a reload, B by a catch-up; both handed GARL on every PC; 2 save files sent; A 0 of 26 snapshots differ, B 0 of 70 |
| `tests/resync.sh` (guest's HP lowered by 1 on its PC at f1500) | Found at f1530 and f1560; left at f1567; dropped in again at f1729; 0 of 59 snapshots differ after |
| `tests/dropin.sh`, `tests/dropin-two.sh`, local pause (`menu-status.sh`) | Pass: 0 differences, 0 corrections |

Harness: `--sosonline-play` on the host plays a save with the lobby open; `--sosonline-quitruns=N` keeps a host that goes back to solo running until N runs have ended; a test game that never gets into a session quits after 300 s.

**Pause for everyone** (local pause screen). The local pause had made the game's own pause menu unreachable in free gameplay, and with it Assign Players, Return to title and the host's game Options. The entry closes the local screen and puts a pause press into this PC's next synced input, so the shared pause menu opens on every PC as before (tested: open on f1107–1110 and closed on every PC by the banner's 0.15 s poll; a joiner caught up across it; 0 differences). Calling `PauseManager.TryPauseAndShowAssignControllers` on a scheduled frame was tried first: it returned false on every PC.

| `tests/resync.sh` with `DIVERGE=1600 QUIT=5400 EXTRA="--sosonline-encounter=1500 --sosonline-bot"` (re-sync during a battle) | Found mid-battle at f1620 and f1650 (stats and combat HP); the guest left at f1657, mid-battle, its character back to the party AI; the bots finished the battle; it dropped in again by a reload at f3033, after the battle; 0 of 75 snapshots differ after |
| `tests/dropin.sh` with `HOSTFLAGS=--sosonline-swapat=1500` (a character swap while the third player catches up) | The swap went out for f1505 (guest 1 given GARL on every PC); the host cancelled the joiner's catch-up the same frame; the joiner dropped in by a reload at f1783; guest 1 0 of 129, joiner 0 of 78 snapshots differ |

**Not tested:** everything Steam (lobby, invites, the invite popup, `OpenToFriends`' automatic lobby).

## 10. First Steam session with a friend: fixes (2026-11-15)

Two PCs over Steam (host at 1920×1080, guest at 2560×1440), build `d9d6210`, about 35 ms ping. Both PCs' logs were compared frame by frame.

**Saving ended the co-op session.** At a save point, answering Yes to the game's own save question ran "End session for everyone". The game reuses its `GenericPopupScreen` view and adds its handler to `onPopupAnswered` with `+=`. Our `Popup` set its handler on that event and never removed it, so the game's later question also ran our old choice. **Fix:** `NativeMenus.Popup` ignores any answer that isn't for the pop-up it opened (a token per pop-up) and clears its handler after the answer.

**Drop-in loop.** The friend dropped in again and again every few seconds. "Open to friends" restarted co-op immediately after the session ended. After each drop-in by reload, the only difference was the facing of P2's character (`party=[…/5 …/2]`), which counted as out of sync and forced a full reload every time. **Fixes:**
- `StateSync`: facing is no longer part of the `party` hash. It travels with each character's position and the guest corrects it in place, but only if the character hasn't turned since that frame.
- `Session`: after 3 re-syncs within 2 minutes, the next drop-in waits 60 s. The host sees a toast.

**Input delay from one slow ping.** On a reconnect the first ping read 1630 ms, which set the maximum delay (15 frames, 250 ms) for the whole session. **Fix:** the ping is the median of the last 9 readings, and co-op from the host's own game waits for 3 readings before choosing the delay. The automatic delay is now at least 4: every drop-in at delay 3 went out of sync and none at 5, 6 or 15 did. This works around a cause that wasn't found; revisit it once the fixes below are confirmed.

**Title screen Online menu.** The cursor went back to the first item whenever the page refreshed (a toggle, a player joining), and cancel (B/Esc) did nothing. **Fix:** the cursor stays on the same item, and cancel works like the Back item.

**Battles went out of sync, and the guest's game crashed.**
- **Damage rolls.** In the Mooncradle training, both PCs matched on every frame until a basic attack hit for 11 on the host and 10 on the guest, on the same frame with the same inputs, positions and MP. Hits get a random modifier (`PlayerAttackDamage.GetRandomDamageModifier`, `GlobalCombatSettings.playerRandomDamageRange`). The engine reseeds `UnityEngine.Random` once per frame, but visual effects (screen shakes, randomized sprites and particles, animator offsets) draw from it during the frame, and how often depends on each PC's screen. **Fix:** `Lockstep/DeterministicRandom.cs` runs every gameplay roll on a generator seeded from the session seed, the frame and the roll count, then restores the previous state. It covers the damage modifier, enemy move and pattern choices, random target picks, hit patterns, a few boss mechanics and loot. Not yet confirmed that the game rolls damage with `UnityEngine.Random`: check the `[random]` line at startup and a battle's HP in both state logs.
- **Corrections during battles.** In another training round the guest ran Valere's attack 90 frames (1.5 s) before the host. The position correction then moved Valere 5 times in the middle of the attack, the guest's battle ended early, and its game crashed 2 s after leaving the run (no error in the mod log). In-sync battles need 0 corrections (finding 8), so a gap there means the PCs are at different points of the battle. **Fix:** no position or facing corrections during a battle. A gap over 0.5 m counts as a mismatch, and two in a row start a re-sync.
- **Timing verdicts are always logged** (`[timing] attack impact …` and `[timing] block …`, a few lines per battle), so comparing both PCs' logs shows whether a battle desync came from timing.

**Drop-in autosave between training rounds.** The game allows saving in the short gaps between the training's rounds (`combat=off` for about 2 s), and a drop-in autosave was taken in one of them (f68987). The guest had already crashed by then (15:39:00, save at 15:39:24), so it didn't cause the crash, and the join-frame check would have postponed the join when the next round started. It still saved and sent files for nothing. **Fix:** the host takes a drop-in autosave only once no battle has run for 3 s (`Session.BattleSettleFrames`, 180 frames).

**Protocol 9:** snapshots carry each character's facing.

**Open:**
- Why the guest ran Valere's attack 1.5 s early. It could be the same random-roll cause (the training dummy's behaviour); retest with the new build first.
- The crash: no game log was kept. If it happens again, get `Player.log` (or `Player-prev.log` after one restart) from `%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars\` before the game is started twice more.
- The minimum automatic delay of 4.

**Not tested in the game yet:** everything in this section. The game folder was in use during the session, so the fixes were only built.

## Tools added

| Script / flag | Use |
|---|---|
| `tests/battle-live.sh` | Two-instance battle; prints the guest's corrections and mismatches |
| `tests/dropin.sh` | Three-instance drop-in; `HOSTFLAGS`, `JOINFLAGS`, `JOIN_DELAY`, `QUIT`; compares both guests with the host |
| `tests/menu-status.sh` | Host and guest walk; the guest pauses and opens Options, the host opens the main menu; a third instance catches up. `HOSTSCRIPT`, `GUESTSCRIPT`, `SHOTS`, `EXTRA`, `JOIN_DELAY`, `QUIT` |
| `--sosonline-uiprobe=F` | At frame F, log every visible game screen's images (sprite, slicing, colour, size) and texts (font, material, size, colour) |
| `tests/dropin-two.sh` | Host alone, then two joiners in one area; `HOSTFLAGS`, `JOINFLAGS`, `AFLAGS`, `BFLAGS`, `JOIN_DELAY`, `JOIN2_DELAY`, `QUIT` |
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
