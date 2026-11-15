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
| Shared game clock | Unity's time getters (`time`, unscaled time, `smoothDeltaTime`, `frameCount`) are hooked at the native level and read the same on every PC: local time plus an offset re-based on every level load's release. Each PC's own clock made 0.1 s timers end a frame apart (float rounding at a different magnitude), which drifted every battle. | `Lockstep/GameClock.cs` |
| Device routing | During a session your pads and keyboard drive your slot's player. The game's automatic controller (re)assignment and its pause on focus loss or Steam overlay are suspended, because they would only happen on one PC. | `Patches/Patches.cs` |
| Save mirroring | The host sends the save the session plays from (that slot and the index) to the guests. Guests redirect the game's save I/O to `SosOnlineSession\`, so their own saves are never read or overwritten. Options stay each PC's own. | `Game/SaveMirror.cs` |
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
| Crash or lost connection | The host notices at once when Steam reports the connection lost, or after 2 s without input from that player, then does the same as above. |

- On the leave frame, every PC calls the game's own `SinglePlayerPlusPlayer.Leave()`, and their character goes back to the party AI.
- The leaver keeps playing solo on their copy of the host's save.
- Only the host ending the session ends it for everyone.

**Drop-in.** Lockstep syncs inputs, not world state, so a newcomer can't be streamed into a running game. It rebuilds the game instead, by replay if it can, by a short reload for everyone if not.

*Catching up (only the joiner waits).*
- On every level load's last frame (the area entry), every PC autosaves. The host keeps a copy of that save, the inputs of every frame since, and its state snapshots (`Net/AreaLog.cs`).
- A joiner gets that save, loads it, and replays the inputs since the area entry with rendering and sound off (about 800 frames per second on loopback), checking the host's snapshots as it goes. Then it follows the live inputs.
- Once it is level with the session, the host picks a merge frame: every PC adds the player there and the joiner takes a party member. The players already in never stop.
- If a player joined or left since the area entry, the replay would be over 15 minutes, it disagrees with a host snapshot, or it takes over 2 minutes, the joiner drops in by a reload instead (below).

*Reload in place (fallback).* Everyone reloads a fresh autosave in place, behind the game's own fade, without leaving gameplay:
- A friend who joins mid-session is admitted as *waiting*. Once the game itself has allowed saving for half a second (no battle, cutscene or loading), every PC in the session autosaves on an agreed frame. Lockstep keeps running.
- The host sends its saves to the joiner, who loads them into its mirror folder wherever it is (title screen or its own game, once the game has finished booting) and reports ready. If that took more than 5 s, the host saves again first, so little progress is undone.
- The host picks a join frame a few frames ahead and sends it, with where every party member stands, to everyone. The joiner starts lockstep on that frame with the inputs the host has buffered for it.
- On the join frame every PC, the joiner included, loads the autosave in place (`GameBridge.ReloadInPlace`): the leader spawns where it stood, the loading barrier ends the load on one frame everywhere, and two frames later every party member is put back on its own position.
- Co-op players aren't in the save, so on the join frame every PC lets the guests leave co-op, and each guest takes a party member again automatically: the mod presses the game's co-op **Join** for them, through synced input. The same happens after a session start.
- Progress made between the autosave and the join frame (usually 2 s) is undone, except positions.
- If a level load or battle is running on the join frame, every PC sees it on that frame and the join is postponed: the drop-in starts over with a fresh autosave once it is over.

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
| Guest process killed | The host drops the slot after 2 s and keeps playing. |
| Synced pause-menu leave | Applied on frame 905 on both PCs. |
| Third player joins mid-session (drop-in in place) | The joiner is booting when it joins; the first autosave goes stale and is replaced. Existing players: 92–95 frames (about 1.6 s) from the join frame to the load's release, never via the title screen; both guests play again 5 frames later and the joiner walks its character. After the join, the joiner's state matches the host's exactly (73–81 of 73–81 snapshots, four runs); guest 1 differs only by up to 1 cm in positions, within the 2 cm correction, with no mismatch reported. |
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

## Install

Everyone in a session needs **the same build of the mod** and the same game version (both are checked when a friend joins). Install it once from a release; after that it **updates itself** (see *Automatic updates*).

### Friends: install from the zip

You need Sea of Stars on Steam (Windows) and the mod zip, `SosOnline-<version>.zip`, from the [latest release](https://github.com/miguelguzmanr/sosonline/releases/latest).

1. **Install BepInEx 6 (IL2CPP).** Download [BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793](https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip) and extract it into the game folder, next to `SeaOfStars.exe`. To find that folder: Steam → right-click Sea of Stars → *Manage* → *Browse local files*.
2. **Start the game once from Steam** and wait for the title screen. The first start takes a minute or two longer while BepInEx prepares itself; a console window opens next to the game. Then quit.
3. **Install the mod.** Extract the mod zip into the same game folder. It adds `BepInEx\plugins\SosOnline\SosOnline.dll`.
4. Start the game from Steam. The title screen has an **Online** entry.

Updates install themselves (below). To update by hand, extract the new zip over the old one. To uninstall, delete `BepInEx\plugins\SosOnline`; to remove BepInEx too, delete `winhttp.dll` from the game folder.

### Host: build it

Requirements: Sea of Stars on Steam (Windows) and the .NET 6+ SDK.

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -GameDir "W:\SteamLibrary\steamapps\common\Sea of Stars"
```

- The first run installs BepInEx 6 IL2CPP (it asks first). Start the game once so BepInEx can generate its interop assemblies, quit, then run the script again to build and install the plugin.
- To pack a zip without releasing it (needs PowerShell 7):

```powershell
pwsh -File tools\package.ps1 -GameDir "W:\SteamLibrary\steamapps\common\Sea of Stars"
```

  It writes `dist\SosOnline-<version>.zip`, signed when the release key exists.

### Automatic updates

At every game start the mod asks GitHub for the [latest release](https://github.com/miguelguzmanr/sosonline/releases/latest). If it is newer, the mod downloads its zip in the background. It installs the DLL only if the DLL's signature matches the release key built into the mod. A toast then says *Update … downloaded. Restart the game to use it*, and the next start runs it. Nothing downloaded is loaded in the running game.

- To turn it off, set `AutoUpdate = false` under `[Updates]` in `BepInEx\config\sosonline.coop.cfg`.
- Builds you made yourself with uncommitted changes (`…-modified`) and test runs never update.
- If someone joins with a different version, the host refuses them and names both versions. Restarting the game picks up the update.
- The check is one request to `api.github.com` at start, plus the download when there is an update. No account is needed and nothing personal is sent.

### Releasing a build (maintainer)

Needs PowerShell 7, the GitHub CLI (`gh`, logged in), and the release signing key.

1. Once: `pwsh -File tools\new-signing-key.ps1` creates the key in `%APPDATA%\SosOnline\release-signing-key.txt` and prints the public key, which goes in `SosOnline\Update\AutoUpdate.cs`. **Back up the private key and never commit it.** Without it, players need one manual install of a build signed with a new key.
2. For each release, commit everything on `main`, then run:

```powershell
pwsh -File tools\publish.ps1 -GameDir "W:\SteamLibrary\steamapps\common\Sea of Stars"
```

   It pushes `main`, builds and signs `dist\SosOnline-0.1.<count>+<commit>.zip`, and creates release `v0.1.<count>`. The count is the number of commits, so versions always increase. Players get the release at their next game start.

## Playing

**Start from your own game (no title screen).**
1. **Host:** pause → **Online** → **Open to friends**. A Steam lobby stays open while you play (also on the next start: it's a setting, toggled in the title screen's **Online** menu). **Invite friends** opens the Steam overlay.
2. **Friend:** accept the invite, or use **Join Game** in the Steam friends list. From the title screen, **Online** → **Join a friend** works too. Accepting while in your own game asks once, then replaces your game with the host's.
3. The friend drops in at the next moment the game allows saving (no battle, cutscene or loading): either they catch up on their own PC while the host keeps playing, or everyone sees a short fade. Then they take a party member automatically.

**Or start together from the title screen:** Title screen → **Online** → **Host co-op session**, friends join, then **Start co-op** and pick a save.

**During a session:**
- **Pause** (Start / Esc) opens a pause screen on your PC only; the game keeps running for everyone and the others see "P2 is paused". It has **Options** (volumes, brightness, display: this PC only), **Pause for everyone** and **Leave session** (the host: **End session** for everyone). In battles and cutscenes the game's own pause still stops everyone.
- **Pause for everyone** opens the game's own pause menu on every PC, as in local co-op: the game stops for everyone and only you move in it. Use it for **Assign Players**, **Return to title** and the like.
- **Choosing who plays whom:** pause → **Pause for everyone** → **Assign Players**; each player moves their own cursor. A friend who rejoins gets the character they had back, if it's free.
- **Leaving:** pause → **Leave session**. Your character goes back to the party AI and the others keep playing. When the last friend leaves, the host simply keeps playing; the lobby stays open.
- If a friend's game gets out of sync, it re-syncs by itself with a short reload on their PC.

Use a gamepad or keyboard; the mouse is not synchronized. Saves made during co-op are written by the host as usual. On guests they go to a separate session folder, so the guests' own saves are never touched and the host keeps the shared progress. Options stay each PC's own.

### Trying it with a friend over Steam (first time)

Nothing Steam-specific has been tested end to end yet (see *Not yet verified*). Worth checking, in this order:
1. The friend sees the invite and joins from the title screen; both PCs show the friend in the HUD line (F8 opens the debug panel).
2. The friend drops in and takes a party member; both walk around for a few minutes. The HUD should keep saying *synced*.
3. Pause on one PC: only that PC shows the pause screen, the other shows "P… is paused".
4. A battle with timed hits from both players.
5. The friend leaves and rejoins (gets the same character back); the host walks into a new area first so the rejoin catches up instead of reloading.
6. Accept an invite while in your own game (one confirmation, then the join).

### If something goes wrong: share the logs

Every start of the game writes its own log, so nothing is lost when you restart. **Both players** should send theirs, from the same session:

1. Open the logs folder: press **F8** in game → **Open logs folder**, or title screen → **Online** → **Open logs folder**. It is `BepInEx\SosOnline\logs` in the game folder.
2. Send the newest `sosonline-<date-time>.log` and `state-<date-time>.log` (the file names carry when the game was started). If you restarted the game since the problem, take the pair from that earlier start; the last 10 starts are kept.
3. Say roughly what happened and when (the log lines carry the time of day).
4. If the game crashed, also send the game's own log from `%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars\`: `Player.log` if you haven't started the game since, otherwise `Player-prev.log`. It is gone after two more starts.

What's in them: everything the mod logged (joins, drop-ins, desyncs, pauses, leaves), the game's own errors, the mod build (the commit, e.g. `0.1.0+e3d3ca0`), the game version, Windows version and graphics card, and a session summary every minute (players, ping, input delay, sync checks). The state file has a snapshot of the game state every half second, so the host's and a guest's can be compared frame by frame. The logs contain the Steam names of the players in the session and Steam lobby ids (sometimes a player's Steam id); nothing else personal.

If the friend runs a different build than the host, the host gets a message when they join: use the same zip on both PCs.

## Testing without a second PC

`tests\` runs two game instances on one PC over loopback UDP (no Steam networking), with scripted input, screenshots and per-frame state logs in `BepInEx\SosOnline\`.

| Script | What it checks |
|---|---|
| `tests\solo.cmd` | Hosts with no guests, starts from save 0, screenshots, quits. |
| `tests\two-instance.cmd` | Host (UDP 27100) and guest (27101): handshake, save mirroring, synced start. Set `SCRIPT_HOST`/`SCRIPT_GUEST` to input scripts in `tests\scripts\`. |
| `tests\uitest.cmd` | Screenshots of the title screen Online menu, hosts a real Steam lobby, leaves. |
| `tests/ab-run.sh` | Runs solo battle instances in parallel (each `"port\|flags"`), e.g. timing bots with and without `--sosonline-nocomp`. |
| `tests/battle-map.sh`, `tests/map-results.sh` | Maps the game's timed-hit and block grading: one run per press offset. |
| `tests/battle-live.sh` | Two instances, a battle on both from frame 1500: prints the guest's corrections and mismatches (finding 8). |
| `tests/dropin.sh` | Three instances: a third player joins 30 s in (`JOIN_DELAY`). `HOSTFLAGS`/`JOINFLAGS` add flags, e.g. `--sosonline-nocatchup` (reload in place), `--sosonline-loadatjoin`, `--sosonline-play=0` (join from its own game). Compares both guests' states with the host's. |
| `tests/spike-b.sh` | Records a run, replays it from the area entry autosave, compares every frame. |

Battle flags: `--sosonline-encounter=F` starts the nearest encounter on lockstep frame F (every peer). `--sosonline-bot` confirms menus and presses timed hits `--sosonline-botoffset` frames after the impact it sees; `--sosonline-bot=menu` only confirms menus. `--sosonline-trace` logs the combat timing calls, and `--sosonline-nocomp` disables client-side timing.

Session flags:

| Flag | Effect |
|---|---|
| `--sosonline-leave=F` | Guest leaves over the network on frame F. |
| `--sosonline-kill=F` | Guest's process is killed on frame F. |
| `--sosonline-pauseleave=F` | Synced pause-menu leave of P2 on frame F. |
| `--sosonline-assigntest=F` | Opens Assign Players from the pause menu on frame F. |

`tests/session.sh` launches a host plus guests, staggering late joiners, and tiles their windows side by side, host on the left (`tests/place-window.ps1`; `NO_TILE=1` turns it off).

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
- **Drop-in from a guest's own gameplay.** Tested joiners were on the title screen or still booting. Joining a level load already in progress on the join frame is not handled (the window is a few frames).
- **Three players in combat**, and Assign Players with three players.

## Known limitations

- Joining mid-session waits for a moment the game allows saving. If the party entered its current area recently (and nobody joined, left or swapped characters since), only the joiner waits while it catches up; otherwise everyone briefly reloads an autosave in place (a fade of about 1.5 s) and about 2 s of progress other than positions is undone.
- Options are not available from the pause screen in battles and cutscenes for guests (the shared pause there would put the PCs' menus out of step).
- If the host leaves, the session ends; the host's save is the shared one.
- On Assign Players, every player's cursor shows the keyboard icon (the stand-in device used online).
- After a desync, the guest that diverged re-syncs with a reload in place on its PC; a desync of the host's own game can't be detected (the host is the reference).
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
  Update/           automatic updates from signed GitHub releases
tests/              two-instance test scripts and input scripts
tools/deploy.ps1    install BepInEx + build and deploy
tools/package.ps1   build a (signed) zip in dist/
tools/publish.ps1   release main on GitHub (automatic updates pick it up)
tools/new-signing-key.ps1  create the release signing key (once)
```
