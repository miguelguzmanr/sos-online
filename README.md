# Sea of Stars Online Co-op

Online co-op for **Sea of Stars: Sunset Edition** (Steam) over Steamworks. The game already supports local co-op for up to three players. This mod lets players 2 and 3 be Steam friends playing from their own PCs.

It is a BepInEx 6 (IL2CPP) plugin. The game ships Steamworks (`steam_api64.dll`, initialized by the game through Facepunch.Steamworks); the mod calls Steam's flat API directly.

## How it works

**Deterministic lockstep over the game's own input.**
- Every peer runs the full game from the same save.
- Each frame, every peer sends its controller input to the others.
- Every peer feeds all players' inputs into the game at the same frame, with a fixed 1/60 s time step.
- Same code, same input, same time step: everyone runs the same simulation. Local co-op does the rest. A remote player is, to the game, simply local player 2 or 3.

| Piece | What it does | Code |
|---|---|---|
| Input override | Rewired's `Player.GetButton*/GetAxis*` (all 25 getters) answer from the synced frame. Every game system (movement, menus, combat, the co-op Join and Leave) reads input through them. | `Lockstep/InputOverride.cs` |
| Input capture | Reads the local hardware through the original getters: 13 flags (down, up, repeat, short or long press, …) and axis values for each of the game's 59 actions. | `Lockstep/InputCapture.cs` |
| Lockstep engine | Runs in Rewired's update. Captures input for frame N + delay and sends it. Waits for every player's input for frame N, applies it, and reseeds `UnityEngine.Random` from (session seed, frame). | `Lockstep/LockstepEngine.cs` |
| Device routing | During a session your pads and keyboard drive your slot's player. The game's automatic controller (re)assignment and its pause on focus loss or Steam overlay are suspended, because they would only happen on one PC. | `Patches/Patches.cs` |
| Save mirroring | The host sends its save folder (slots, autosaves, options) to the guests. Guests redirect the game's save I/O to `SosOnlineSession\`, so their own saves are never read or overwritten. | `Game/SaveMirror.cs` |
| Host fixes | Every 30 frames the host sends a snapshot: scenes, RNG, party positions. Guests compare it with their own state at that frame, correct small position drift in place, and report real divergence as a desync. | `Lockstep/StateSync.cs` |
| Session and transport | Friends-only Steam lobby, Steam Networking Messages (relayed by Valve, NAT handled), star topology through the host, handshake checks (mod protocol, game version, input configuration). | `Net/Session.cs`, `Net/Transport.cs`, `Steam/SteamApi.cs` |
| Steam joins | `+connect_lobby` at launch, overlay invites and friends-list **Join Game** while running (observed from Facepunch's callback dispatcher). | `Steam/SteamInvites.cs` |

### Client-side timing for timed hits and blocks

**The problem.**
- Under lockstep, every press reaches the game exactly *delay* frames after the player made it. At 50–100 ms of ping that is about 3–8 frames.
- Sea of Stars' timing windows are about the same size, so judging presses on arrival would turn timing into a ping lottery. At 8 frames of delay a perfectly timed hit misses.

**The fix.**
- Every timing decision is judged against the moment the player actually pressed, relative to what they saw.
- With lockstep delay D, inputs up to D frames ahead of the current frame are already captured by every peer; they are what players pressed while seeing the current frame. So every peer can grade each press identically from its *origin* frame, with no extra round trip.
- The cost is a short wait on impact frames when a peer's input is still in flight.

| Mechanic | Decision point | How it is judged |
|---|---|---|
| **Timed attack** | `TimedAttackHandler.GetResult` at impact (asynchronous) | The call is deferred by D frames, and the game's after-hit window is stretched by D so late-arriving presses still count. The verdict is replaced with a grade from origin times using the game's own rules. Before impact: the latest press within `beforeWindow`. After impact: the first press within `afterWindow` of the impact. The game's frame quantization is reproduced (a press read in the frame that times out still counts). |
| **Timed block** | `PlayerCombatActor.GetBlockResult` at the enemy hit (synchronous) | Look ahead D frames for the player's presses. Success if the latest press made at or before the hit still had its block up (block duration), with the same frame rule. A press used retroactively is consumed so the block animation doesn't replay late. |
| **Time-to-event QTEs** | `TimeQTEHandler` | Windows are evaluated as of D frames ago. *Not exercised yet: no such QTE in the test battle.* |

**Verified** with timing bots that press relative to what they see on screen (`tests/ab-run.sh`):
- **Attacks:** the game's local rule is success for presses from 7 frames before to 13 frames after impact. At D = 8 with compensation, every edge case matched: −8 fail, −7/−3 before-success, +1/+6/+12/+13 after-success, +14 fail.
- **Blocks:** the local rule is 8 frames before to the hit. At D = 8: −9 fail, −8/−3/0 success, +1 fail.
- **Without compensation:** a press 6 frames after impact fails, a block 3 frames before the hit fails, and a too-early block 9 frames before the hit succeeds.
- **Online:** host and guest each made their own timed hits at D = 8. The timing verdicts were identical on both PCs and 87 of 87 state snapshots matched. Look-ahead waited 49–101 ms in total over the whole battle on loopback.

**Trade-offs:**
- A timed hit resolves up to D frames later than in local play: the damage number appears about 0.1 s later at D = 8.
- A missed one waits that much longer before the game moves on.
- Visual feedback follows the game's own check, so in rare edge cases the success sparkle can disagree with the graded result for one hit. The damage always follows the graded result.

### Drop-in / drop-out

**Drop-out.** A player can leave at any time; the others keep playing.

| How they leave | What happens |
|---|---|
| Pause menu → **Online** → **Leave session** | The menu is driven by synced input, so every PC runs the choice on the same frame. The player's inputs already sent are still used; the slot is empty from frame + delay + 1, on every PC. |
| Quitting, or the title-screen **Leave** | The host picks the leave frame: the first frame after the last input it received from them, which it has already forwarded to everyone. It broadcasts that frame. |
| Crash or lost connection | The host notices after 6 s without input from that player, then does the same as above. |

- On the leave frame, every PC calls the game's own `SinglePlayerPlusPlayer.Leave()`, and their character goes back to the party AI.
- The leaver keeps playing solo on their copy of the host's save.
- Only the host ending the session ends it for everyone.

**Drop-in.** Lockstep syncs inputs, not world state, so a newcomer can't be streamed into a running game. Instead:
- A friend who joins mid-session is admitted as *waiting* ("you'll drop in at the next area change").
- After the party's next area change, once the game itself allows saving (no battle, cutscene or loading), the host autosaves.
- Everyone returns to the title screen, and the session restarts from that autosave with the new roster. This reuses the normal session start: save mirroring, ready-on-title, synchronized start.
- The autosave records the entrance just used, so everyone reappears where they were.
- The host can also choose **Let waiting players in now** from the pause menu.
- After a (re)start, guests take over a party member automatically: the mod presses the game's co-op **Join** for them, through synced input.

**Assign Players.** The game's own screen, made session-aware.
- Offline it is device-driven. A gamepad joins by pressing a button (the screen polls every *local* device), and finishing moves devices between the game's players P1–P3.
- Online:
  - The screen is rebuilt identically on every PC (the players currently playing, plus whoever opened it).
  - Players join and confirm with their synced input. Keyboard players work too, through fallbacks from the regular UI actions.
  - The game's device shuffling is ignored during sessions.
  - On submit, each session slot is remapped to the player number its cursor chose, on every PC. Cursor, ready and the game's character assignment already run on synced input.

**Verified** (loopback, 2–3 instances):

| Scenario | Result |
|---|---|
| Guest leaves | Applied on frame 905; the host keeps playing alone. |
| Guest process killed | The host drops the slot after 6 s and keeps playing. |
| Synced pause-menu leave | Applied on frame 905 on both PCs. |
| Third player joins mid-session | Admitted as waiting; the reload carried the autosave (5 files). All three play, and the 81 snapshots after the reload are identical on all three PCs. |
| Assign Players | Opened online; P2's cursor took the P1 column. After confirm, on both PCs the guest drives P1/Zale and the host P2/Valere. The screen closes through the game's own path; all 57 snapshots match. |

### Native-code safety

IL2CPP folds identical native functions (empty virtuals, trivial getters and setters, shared generic code) into one. Detouring such a function also hooks every method sharing it, with unrelated `this` objects. Two defenses (`NativeCode.cs`):
- **Audit at load.** The plugin enumerates every method of every class in the IL2CPP runtime (80,235 native functions) and removes any of its own patches that land on shared code. The current build audits 103 patched methods with 0 shared.
  - Early on, this caught three such patches: `SteamPlatform.IsInFocus` was shared with Rewired joystick-map getters. All three were replaced.
- **Runtime class guard.** Every instance patch first checks the real IL2CPP class of `this`. This also covers sharing with generic instantiations, which metadata can't enumerate. For example, `InputManager.SetControllerAssignmentMode` turned out to share code with `ObjectPool<T>` methods.

### Native menus

The menus use the game's own widgets, so they look, sound and navigate like the rest of the UI. This follows the sh2-online approach.

- **Title screen:**
  - An **Online** entry, copied from the Options button.
  - Choosing it swaps the title list for the co-op entries, with a status line under them.
  - Entries: **Host co-op session**, **Join a friend** (friends who are hosting), **Start co-op** (pick the save), **Invite friends** (Steam overlay), **Input delay**, **Close/Leave session**, **Back**.
- **Pause menu:**
  - An **Online** item, added with the game's own `PauseMenu.AddPauseMenuItem`.
  - It opens the game's standard two-choice popup.
  - During a session the pause menu is driven by synced input, so its choices act as deterministic commands. *End session* stops co-op on every PC on the same frame, and everyone keeps playing solo from an identical state.
- **Text:** the mod's strings go through the game's localization (`LocalizationManager.GetText` patched for a private `SosOnline` category).
- **F8** opens a small debug panel, and a one-line HUD shows players, ping and sync state during a session.

## Playing

1. Everyone installs the mod (see below) and starts the game.
2. **Host:** Title screen → **Online** → **Host co-op session** → **Invite friends**, or friends can use **Join a friend**.
3. **Guests:** accept the Steam invite, or use Title screen → **Online** → **Join a friend**, or use **Join Game** in the Steam friends list.
4. **Host:** **Start co-op** and pick a save. Everyone loads it together, and guests automatically take over a party member.
5. **Choosing who plays whom:** pause → **Assign Players** (the game's own screen, works online).
6. **Friends joining later** drop in at the party's next area change. **Leaving:** pause → **Online** → **Leave session**.

Use a gamepad or keyboard. The mouse is not synchronized. Saves made during co-op are written by the host as usual. On guests they go to the session folder, so the host keeps the shared progress.

## Install

Requirements: Sea of Stars on Steam (Windows) and the .NET 6+ SDK (to build).

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -GameDir "W:\SteamLibrary\steamapps\common\Sea of Stars"
```

- The first run installs BepInEx 6 IL2CPP (asks first).
- Start the game once so BepInEx can generate its interop assemblies, then run the script again to build and install the plugin.
- To uninstall, delete `winhttp.dll` from the game folder (disables BepInEx), or delete `BepInEx\plugins\SosOnline`.

## Testing without a second PC

`tests\` runs two game instances on one PC over loopback UDP (no Steam networking), with scripted input, screenshots and per-frame state logs in `BepInEx\SosOnline\`.

| Script | What it checks |
|---|---|
| `tests\solo.cmd` | Hosts with no guests, starts from save 0, screenshots, quits. |
| `tests\two-instance.cmd` | Host (UDP 27100) and guest (27101): handshake, save mirroring, synced start. Set `SCRIPT_HOST`/`SCRIPT_GUEST` to input scripts in `tests\scripts\`. |
| `tests\uitest.cmd` | Screenshots of the title screen Online menu, hosts a real Steam lobby, leaves. |
| `tests/ab-run.sh` | Runs solo battle instances in parallel (each `"port\|flags"`), e.g. timing bots with and without `--sosonline-nocomp`. |
| `tests/battle-map.sh`, `tests/map-results.sh` | Maps the game's timed-hit and block grading: one run per press offset. |

Battle flags: `--sosonline-encounter=F` starts the nearest encounter on lockstep frame F (every peer). `--sosonline-bot` confirms menus and presses timed hits `--sosonline-botoffset` frames after the impact it sees; `--sosonline-bot=menu` only confirms menus. `--sosonline-trace` logs the combat timing calls, and `--sosonline-nocomp` disables client-side timing.

Session flags:

| Flag | Effect |
|---|---|
| `--sosonline-leave=F` | Guest leaves over the network on frame F. |
| `--sosonline-kill=F` | Guest's process is killed on frame F. |
| `--sosonline-pauseleave=F` | Synced pause-menu leave of P2 on frame F. |
| `--sosonline-admit=F` | Host lets waiting players in on frame F. |
| `--sosonline-assigntest=F` | Opens Assign Players from the pause menu on frame F. |

`tests/session.sh` launches a host plus guests, staggering late joiners.

Test instances never touch your real saves:
- Each UDP test instance plays from its own sandbox copy (`<saves>\SosOnlineTest-<port>`) and its own mirror folder.
- Delete those folders when done.

Unity stores the `-screen-*` test flags as the game's display preferences, so wrap test runs with `tests/display-prefs.sh save` and `tests/display-prefs.sh restore`.

These scripts need `steam_appid.txt` (containing `1244090`) in the game folder, so the exe can start directly with the test flags.

Compare runs with:

```bash
diff <(cut -d' ' -f2- "BepInEx/SosOnline/state-27100.log") <(cut -d' ' -f2- "BepInEx/SosOnline/state-27101.log")
```

The command-line flags are documented in `SosOnline/TestHarness.cs`.

### Verified (2026-11-13, game build 3.0.60151)

- Plugin loads; all Harmony patches apply, none on shared native code.
- Battles online: tutorial encounter at 8 frames of delay with host and guest each timing hits. Timing verdicts and all 87 state snapshots are identical on both PCs (see *Client-side timing*).
- Guests only report ready once they are on the title screen (a guest still booting used to miss the start).
- Title screen Online button, submenu and status line render with the game's widgets; the pause menu Online item appears.
- Steam lobby creation and leaving (real Steam).
- Two instances over UDP:
  - Connect, mirror 4 save files, and start together from the title screen into the save.
  - Host walks; the guest presses Join and walks Moongirl on their own.
  - **81 of 81 state snapshots identical** (scenes, RNG, every party position) over 2,400 frames.
  - Identical screenshots on both instances.
- Pause menu opened by the host appears on the guest (synced menus).

### Not yet verified

- **Steam P2P between two real accounts.** Only lobby creation was exercised; the messaging path is the same flat API but untested end to end.
- **Combat, beyond the first tutorial battle.** Bosses, combos, ultimate moves, Moonerang deflections and the co-op "additional player" QTEs (`*AdditionalPlayersQTEState`) were not exercised. `TimeQTEHandler` compensation is untested.
- **Long sessions, area transitions with heavy streaming, cutscenes.** Async loading is the most likely source of desyncs.
  - The host-snapshot check reports them in the log and the HUD.
  - Seen in testing: the initial level finished loading 1–2 frames apart on two instances. It converged once the party spawned, but a loading barrier would make it exact.
- **Drop-in triggered by a real area change.** Tests used the host's *let them in now* trigger; the area-change detection itself (level reference changes, then saving allowed) hasn't run in a test.
- **Three players in combat**, and Assign Players with three players.

## Known limitations

- Joining mid-session waits for the next area change (or the host's *let them in now*), and everyone briefly reloads from an autosave.
- If the host leaves, the session ends; the host's save is the shared one.
- On Assign Players, every player's cursor shows the keyboard icon (the stand-in device used online).
- No automatic full resync after a desync yet. End the session from the pause menu and restart it from the latest save (or let someone "join" to force a reload).
- If one PC can't keep 60 fps, everyone slows down with it (lockstep).
- Mod and game versions must match on every PC (checked when joining).

## Layout

```
SosOnline/          plugin source (net6.0, references the game's BepInEx interop assemblies)
  Game/             game wrappers, save mirroring
  Lockstep/         input capture/override, lockstep engine, state sync
  Net/              session, protocol, transports (Steam, UDP)
  Steam/            steam_api64 flat-API bindings, invites
  UI/               native menus, localization bridge, toasts, debug panel
tests/              two-instance test scripts and input scripts
tools/deploy.ps1    install BepInEx + build and deploy
```
