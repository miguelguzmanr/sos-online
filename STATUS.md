# Status

Current state of the project, for whoever picks it up next. Update it at the end of every working session: replace what changed, don't append a diary. Technical background is in [CLAUDE.md](CLAUDE.md); causes and fixes in detail are in [docs/FINDINGS.md](docs/FINDINGS.md), the drop-in plan and its progress in [docs/PLAN.md](docs/PLAN.md).

*Last updated: 2026-11-27.*

## State right now

- **Branch:** `main`, released as `v0.1.86` (2026-11-26). New since `v0.1.77`: crash-free upgraded blocks, log pieces paced over Steam, the DLC gate (findings §17), drop-ins wait while a scripted scene plays, hidden test runs, test logs kept apart (`logs	ests`), and the work below.
- **New in v0.1.86:**
  - **Battle System** (host setting, Host Settings): *Custom (Fixed Characters)*, the default, gives each player turn to the owner of the character it starts on and keeps the cursor on characters they own (`Lockstep/BattleTurns.cs`); *Classic (Random Characters)* is the game's own random pick. Fixed when a session starts, sent with every lockstep start (protocol 14).
  - **Host moderation** on the host's pause screen, which now opens in battles for the host: *Take over this turn*, *Pass turn to P{n}* (`Msg.TurnCommand`, scheduled; lasts only that turn) and *Remove a player* (`Msg.Removed` plus the scheduled leave).
  - **Player bar** (`UI/PlayerBar.cs`) replaces the top status line (the old line is **Mod Options** → **Advanced Status Bar**, off by default): icons only in play, names on the title and pause screens, Assign Players and the save screen; hidden over the game's full-screen menus. Whose turn, who is paused and who has a menu open are **notices** in the game's own notice style (`UI/PlayerNotice.cs`); the game's join and leave notices carry the name. The top toasts for a plain join or leave and the bottom menu banner are gone. *Waiting* needs a wait over 0.1 s.
  - **Host Settings** and **Mod Options** are two pages again.
  - Logs sent to the host carry only the session folder's saves, never the PC's own.
  - A leaving PC waits 1.5 s before disconnecting, so its last inputs reach the host (a removed player's last frames were lost and the host stalled 20 s, then ended co-op); the host also moves a silent leaver's leave to the frame after its last input.
  - `[rumble]` log line: which player the game's controller rumble is for. The game rumbles the current turn's player when another presses Interact, as in local co-op; whether to keep that online is still open.
  - **Tested:** the full sweep on 2026-11-26 (hidden): every session test 0 differ or 0 mismatches, no errors in any log. The camp test found no campfire near the user's newest save (Mooncradle), so camping itself wasn't exercised. Also three players with ownership turns, a pass, a take over and a removal over a lagged link (170/170 and 137/137 snapshots), and screenshot runs of every notice and bar status. Not yet: Steam, the Classic setting in a battle.
- **Tests run hidden** (merged 2026-11-26): every bash test script starts the game on a hidden desktop, muted and deaf to this PC's keyboard, mouse and pads (`tests/launch.sh`, `--sosonline-background`); `VISIBLE=1` shows them. The `.cmd` scripts still start visible.
- **Latest release on `main`:** `v0.1.86`, 2026-11-26. The repository's history was rewritten and the GitHub repository recreated as `sosonline` and renamed to `sos-online`, so the old name redirects again (v0.1.63 and older look up releases there).
- **Installed on the user's PC:** v0.1.86 (played over Steam with two friends on 2026-11-26).
- **Updates are manual from v0.1.60 on.** v0.1.56 and older download v0.1.60 at their next start; after that, *sos-online \<version\> (\<update\> available)* on the title screen and *Check for Updates* under **Mod Options**.
- **Test saves:** existing single/2-player scripts run from the user's newest save (`--sosonline-start=0a`), while `tests/three-player-*.sh` and `tests/rejoin-paused.sh` use the sandboxed fixtures in `tests/saves/` (`midgame-necromancer/`, `endgame-worldmap/`).

## Open items, by priority

From the playtest of 2026-11-26 (v0.1.86, three players over Steam, Mountain Trail and Forbidden Cavern; host logs and seven friends' log zips). Committed on `main`, not released (findings §18, §19):

- **Fixed: wandering NPCs split the PCs on Mountain Trail, and catch-up replays failed 12 of 12.** Game time ran during the load barrier, for a different number of frames on each PC; it now stands still while a load is held. Two hidden instances from the user's newest save: 0 of 81 snapshots differ (53 before); a replay there matches the host (57 of 57).
- **Fixed: Join from Steam with the game closed got stuck** (findings §19): the request came in while the game started, its question was removed by the title screen's load, and the join waited for an answer forever; friends had to use **Online** → **Join**. Now it waits for the title screen and joins there. Check in the next Steam session: a friend clicks **Join Game** with their game closed.
- **Committed 2026-11-27, not released: Rejoin and the menu rework.**
  - **Rejoin** (Host Settings) replaces Join Mode: *Require Approval (Safest)* (default) / *Auto (Experimental)* / *Off* (title screen only). Every reload that affects everyone (new friend, reconnect, re-sync that can't replay) goes through it. Catch Up is tests only.
  - **Fixed: the Zenith Academy soft lock** (findings §20): a drop-in reloaded a mid-room autosave, and the save doesn't keep a room's own state (the placed stone, the wall it raised). Reproduced from a recording of the user's run. Now drop-ins and rejoins by a reload happen only at the next area entry, with the game's own entry autosave; players hold still from it until the reload (`LockstepEngine.HoldPlayers`, `Msg.PlayersRelease`); joiners report when their game has started (`Msg.Booted`), so the hold lasts about 2 s instead of 15. **Return to Area Start** on the host's main pause page lets a friend in at once. Protocol 17. Test scripts that stay in one area use `--sosonline-dropinanywhere`; `tests/dropin.sh` with `DOOR=F` tests the real path (sweep: `dropin-door`).
  - **Guests reconnect** for 60 s after a lost connection instead of going to the title screen (a guest gives up on a silent host after 10 s).
  - **Open on Start** replaces Open to Friends; ending co-op no longer changes it. **Battle Turns** (Original / Custom), **Status Bar** (Off / On / Advanced), **Mod Settings**, Input Delay as Auto / Low / Medium / High.
  - Pause screen: Let X In under Resume, **Players** page (Remove, Ask for Logs), **Host Settings** (Battle Turns and Input Delay locked during co-op), End/Leave Co-op last, cursor kept by label.
  - Wording: the review's fixes; `docs/messages.csv` rows 2–306 updated with dated notes, new rows 309–327, for review.
  - Tested hidden, in a separate copy of the game: the user's Zenith Academy recording replayed with a friend waiting: no reload mid-room (the wall stays), the friend joins at a test door with a 2 s hold, 0 of 60 snapshots differ. `dropin.sh` with `DOOR=2400` (Auto): 0 of 127 and 0 of 51. `stage1.sh`: 0 of 26 and 0 of 65. `resync.sh`: 0 of 174, 0 of 249 twice. New `tests/reconnect.sh` (in the sweep): the guest's network drops for 8 s, it notices after 10 s, reconnects on the first try and rejoins by a reload: 0 of 159 differ afterwards. Earlier in the day: Require Approval with **Let Robin In** at f2720, 0 of 156 and 0 of 68.
  - Watch: one `resync.sh` run of four had its catch-up replay diverge (combat, f1830) and fall back to a reload; not reproduced since. Joins at area entries and reconnecting not yet tried over Steam. No full sweep yet.
- **New: Join Mode** (Host Settings): *Reload (Ask Me)* by default, the host lets a friend in from its pause screen and everyone reloads; *Reload (Automatic, Experimental)*; *Catch Up*, the old way (test runs keep it unless `--sosonline-joinmode`). Protocol 15. Tested hidden with `tests/dropin.sh` (in a separate copy of the game): Ask Me, the joiner waited until the host chose **Let Robin in** at f2720, joined by a reload, 0 of 170 snapshots differ; Automatic, joined by a reload without asking, 0 of 202; Catch Up, joined by replay as before, 0 of 261. New strings are rows 297–308 of `docs/messages.csv`, for review. Not tested over Steam.
- **Fixed: a joining or rejoining player listed twice** on the host's player bar: once on their slot's row and once as a waiting joiner.
- **Fixed: the re-sync waiting screen left the session** as it showed (it read the synced Back press).
- **Fixed: auto input delay stuck at 15 frames** from one 723 ms ping sample at the join.
- **Fixed: the player bar** no longer fades in play (players took it for gone after a cutscene).
- **Fixed: ultrawide.** The mod's UI sits in the game's 16:9 picture, not behind its black bars (checked at 1680x720).
- **Changed: replays longer than 5 minutes** (was 15) reload instead; a 51,000-frame replay never finished.
- **Logs:** duplicate combat lines and the 5-second `[test] playing` line only when they change, `[rumble]` only in test runs, and a closing `[quit]` line, so a crash can be told from a quit.
- **Watch in the next session:** the shop opening on the host only (f5759, `pause differs`), the Bosslug battle split and the pause state differing in replays. All followed a level load, so likely the same cause; not reproduced.
- **Player bar on the title screen: not reproduced.** The user never saw it on the title screen during the Steam session, though friends sat in the lobby there for 30–40 s twice. On UDP it shows on the title, on the Online page and on Load Game, for host and guest, at 1280x720 and fullscreen 1920x1080, on both v0.1.86 and the working copy (`--sosonline-titlebarshot`). Now the log says when the bar shows or hides and why (`[bar] shown, 2 rows, title screen` / `[bar] hidden: …`): check the host's log after the next Steam session. Also fixed: the player icons, which load with the game's co-op HUD, replace their "P1" text stand-ins when they turn up later.
- **Not done:** a tolerance for the NPC check. It compares a hash of positions to 0.1 m; with the cause fixed it should no longer fire, and a tolerance would need the positions sent.

From the playtest of 2026-11-25 (v0.1.77, three players over Steam):

- **Next Steam session:** check that **Send Logs to Host** arrives with a crash dump in it (`[steam] send queue … full` / `drained` in the friend's log), that owners get *DLC is off: a friend doesn't have it* and everyone sees the base-game intro on a new game, and that a block or timed hit upgraded for a guest no longer crashes.
- **Assign Players split** (session 1, f26740): it opened on the host while the pause menu closed on the guests. Not reproduced; possibly the DLC difference.
- **Load Game refusal for a DLC save** (`saveRequiresTOTW` / `isInDLC`) and the host's refusal of a joiner while its game is in the DLC are untested: no save in the DLC yet.

Earlier:

From the first session on v0.1.53 (user's report, 2026-11-16 late): the Bosslug fight had two desyncs, and **in both the automatic rejoin never worked** (looped until both players returned to the title screen). Both play with controllers.

1. **Verify the rejoin loop fix live over Steam.** *Root cause found in the friend's log zips from 2026-11-16 (`v0.1.53`, fixed on `tests/three-player-save`):* `GameBridge.ReloadInPlace` called `LevelManager.LoadLevel` without closing open UI screens (`ShopMenu` from an aborted replay at `f36570`, `PauseMenu` from solo before invite at `f161`) or resetting `PauseManager`, leaving `pause=[on]` across loads and looping immediately on `mismatch #1: pause differs (local [on])`. Fixed via `MenuStatus.CloseOpenMenus` + `GameBridge.ClearMenusAndPause` on `ReloadInPlace` and `ResyncWaiting` (`tests/rejoin-paused.sh` passed `54/54` snapshots, `0` differ). Needs live Steam verification.
2. **Verify fishing minigame live over Steam.** *Root cause found & fixed on `tests/three-player-save`:* `Fish*`, `FishingLine`, and `FishZone` (`Init`, `SpawnAllFish`, `InitSceneFish`, `SpawnFish`, `InitFishInstance`, `GetRandomFishSpawnPosition`) were unwrapped in `DeterministicRandom`. Fixed (`tests/three-player-fishing.sh` passed `55/55` and `65/65` FishingLake snapshots, `0` gameplay probe hits). Needs live Steam verification.
3. **The first battle with Garl in the party desynced.** A title-menu test overwrote the host's `Autosave_SOS_SaveSlot0.sos` twice (2026-11-16 23:26, 23:30). The friend's mirror (`%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars\SosOnlineSession\Autosave_SOS_SaveSlot0.sos`) may still hold the autosave; the user will ask for it. Checked 2026-11-17: no copy with Garl on the host PC. Both local slot-0 files, Steam Cloud (same checksums, synced 23:30:53) and the copies in Downloads hold the Forbidden Cavern state (Zale and Valere, level 2). Windows shadow copies need admin (Explorer → the save folder → Properties → Previous Versions). Every test run is sandboxed since (`SosOnlineTest-steam` for non-UDP runs).
4. **Ask the friend to copy their `SosOnlineSession` folder** before their next online session (the playtest's state may only be there; v0.1.61 backs it up by itself once installed).
5. **The reorganised Online page over Steam:** hosting adds Invite Friends, and a Join page full of friends (the status lines hide when the list reaches the copyright). Tested on UDP only; B on popups and on the page confirmed by the user with a pad.
6. **Verify over Steam with the friend:** a re-sync during a battle (catch-up log blocks over the relay), a lost battle (game over) while a friend waits to join, windows of different sizes (logic clipping, item 7), **Return to area start** (the area save goes to playing guests in one ~25 KB message), and **Later** on the *Check for updates* popup (the test helper couldn't press it).
7. **Party AI after a leave mid-battle replays differently now and then.** A second re-sync in the same area replays the guest's first leave; once, the AI's Valere moved 3.3 m differently right after it (f1680). Falls back to the reload. Next: call-probe the party AI between the leave and the next turn.
8. **Logic clipping** (`LogicClippingManager`): off-screen NPC logic is paused by camera frustum. A replay sees the Bosslug a snapshot early (f390, never two in a row). Turning clipping off broke replays. Might also differ live between PCs with different window sizes.
9. **Return to area start in a cutscene** (with *Pause for everyone*) is untested: camping in the test save still allows saving.
10. Older, not touched: guest crash after a re-sync in a battle (no repro; the log zip now includes the crash dump); `Session.MinAutoDelay` 4 (consider 2 after a stable Steam session); folded methods not covered by the deterministic-random wrappers (`CombatActor.OnPlayerPartyTurnDone`, `OnAnyCharacterTurnDone`, `OnPlayerCharacterTurnDone`, `OnEnemyCharacterTurnDone`); probes (turn, damage, clock lines) stay until battles are confirmed over Steam.

## Recently done

- **v0.1.77 (2026-11-25):** deterministic random rolls for 193 more game methods (enemy moves and casting, fishing, minigames, bosses, overworld AI, crypts; per-method seeds, loading keyed on the level load), the rejoin loop fixed (menus and pause cleared before a reload), large UDP test messages split into 32 KB pieces, **Host Settings** / **Mod Options** on the title screen's Online page, the *sos-online \<version\>* version line, and 3-player test saves and scripts. Save fixtures are stored as binary (`.gitattributes`): their length prefix broke with CRLF. Tested on UDP: the full sweep (0 mismatches; `stage1` once had 1 corrected snapshot after a local pause menu, 0 on a rerun), `rejoin-paused.sh` (53/53 after the paused rejoin), `three-player-battle` (163/163), `three-player-combos` (144/144), `three-player-fishing` (68/68), `three-player-endgame` (74/74, the earlier NPC differences gone), `three-player-menus` (1 of 133 differs at f2700 for P2, right after Assign Players closes, as before).

- **v0.1.63 (2026-11-18):** README has six screenshots (`docs/screenshots`, from `tests/readme-shots.sh` at 1280x720, names Alex/Sam/Robin; the two menu shots show the local build's "0.61-MODIFIED" version line). The co-op version line hides on the frame the game's own version line hides, or whenever the title screen is gone.

- **v0.1.61 (2026-11-18): save backups** (`Game/SaveBackup.cs`), after the playtest's save was lost (the second lost save; Steam Cloud only had the same files). Every save write or delete, and a new session replacing a guest's mirror, keeps the old file in `SosOnlineBackup/Saves` or `Session saves` (last 50 + one per day for 30 days); session snapshots in `SosOnlineBackup/Sessions` at co-op start, join, end and desync (last 30); the session's saves go into Send Logs to Host; test runs may only write saves in test folders (else `SosOnlineTest-blocked`, logged as an error). Tested on UDP: save-sync (manual save, logs zip with `saves/`), resync (snapshots 'desync P2' on the host, 'desync' and 'joined' on the guest; 250 snapshots, 0 differ), end-playing ('co-op ended' on both; the guest's old mirror backed up before the new session's saves), `--sosonline-guardtest`; the user's real save folder was byte-for-byte unchanged; dropin-catchup and dropin-reload 0 differ with 'joined' snapshots on the joiner.

- **v0.1.60 (2026-11-18):** the title screen's Online page and its popups. B on one of the mod's popups now chooses its last choice (Back, Later, OK): the game's own Back only moved the popup's cursor, the selection then went to the menu behind, and the unanswered popup blocked every later one (they showed as toasts). After a popup the cursor goes back to the entry that opened it; the page redraws once a check finishes (it kept *Checking for updates…*). B on the page puts the cursor on *Back*; B on *Back* goes back (confirmed by the user with a pad). A local build shows its version as *0.58-modified*. Test runs never install a release (a scripted answer once replaced the test build with v0.1.56), and the test helper's popup answers choose the right button.

- **Also in v0.1.60:** updates are manual (only a lookup at start, no download); *Online Co-op Version 0.57* in the title screen's bottom-left, mirroring the game's version line, with *(Update available)* when there is a newer release. Versions show as 0.57 instead of 0.1.57. Tested on the title screen (screenshots, both texts); *Check for updates* itself is unchanged apart from the version format.

- **v0.1.56:** *Check for updates* in the title screen's Online menu, with a confirmation before installing (on a modified build it offers the published release). The title menu's Online entries reset whenever the game shows its title choices again, and hide once a save is chosen.
- **v0.1.53:** removed the diagnostic patch on `InputManager.OnActiveControllerChanged` that crashed the game at start and in Options with a gamepad (in v0.1.52).
- **v0.1.52:** *Return to area start*; battle determinism (spell lock types, summon slots, session clock); re-sync by replay mid-battle. Details in findings §13.

## Verification history

### 2026-11-13, game build 3.0.60151

- Plugin loads; all Harmony patches apply, none on shared native code.
- Battles online: tutorial encounter at 8 frames of delay with host and guest each timing hits. Timing verdicts and all 87 state snapshots identical on both PCs.
- Title screen Online button, submenu and status line render with the game's widgets; the pause menu Online item appears.
- Steam lobby creation and leaving (real Steam).
- Two instances over UDP: connect, mirror 4 save files, start together; host walks, guest presses Join and walks Moongirl; 81 of 81 snapshots identical over 2,400 frames; identical screenshots.
- Pause menu opened by the host appears on the guest (synced menus).

### 2026-11-16, build `3fd5a2c` (Forbidden Cavern save)

Every loopback test in one sweep passes with 0 differences: saves (P1 and P2), live battle, drop-ins (catch-up, reload in place, level load on the join frame, from the joiner's own game), two joiners, menu banners, Stage 1, leaving, ending, a guest's game killed, Assign Players, title-screen menus and the restart popup. Camping runs on the same frames on both PCs.

### 2026-11-16, branch `milestone/battle-sync`

The Bosslug fight runs in step: 11 runs in a row of `battle-turns.sh` with 0 differences. A guest that gets out of sync mid-battle rejoins by replay in about 3.5 s. After the summon-slot fix, `resync.sh` passed 6 times in a row (241 of 241 snapshots identical after the rejoin).

### Not yet verified

- **Steam P2P beyond the initial sessions.** Live Steam testing is still needed for the rejoin-loop fix (#1), fishing (#2), and items #5–6 above.
- **3-player transient menu timing:** `tests/three-player-menus.sh` has a 1-frame difference for P2 at `f2700`, right after Assign Players closes at `f2672`; `stage1.sh` had a similar one once after a local pause menu (f1440, corrected in place). `three-player-endgame.sh` matched 74/74 on 2026-11-25.
- **Untested scenes & minigames:** Wheels, crypt generation, co-op additional-player QTEs, `FixedUpdate` RNG ordering, and multi-frame coroutines/open generics in scenes not yet exercised by `--sosonline-rngprobe`.
- **Long sessions, area transitions with heavy streaming, cutscenes.** Async loading is the most likely source of desyncs.
- **3-player Assign Players badge accuracy:** `tests/three-player-menus.sh` exercises 3-player Assign Players; P3's colour is approximate.
- Names with accented letters keep them, but the game's small font may not draw them.

## Known limitations (by design, for now)

- Joining mid-session waits for a moment the game allows saving. If nobody joined, left or swapped characters since the area entry, only the joiner waits; otherwise everyone briefly reloads an autosave in place (~1.5 s fade, ~2 s of progress other than positions undone).
- Guests can't open Options from the shared pause in battles and cutscenes.
- If the host leaves, the session ends; the host's save is the shared one.
- A desync of the host's own game can't be detected (the host is the reference).
- If one PC can't keep 60 fps, everyone slows down with it.
- Mod and game versions must match on every PC (checked when joining).
