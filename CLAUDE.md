# CLAUDE.md

Guidance for Claude Code (and any developer) working in this repository. The README is for players; everything technical lives here.

**Sea of Stars Online Co-op** is a BepInEx 6 (IL2CPP) plugin for **Sea of Stars: Sunset Edition** (Steam, Windows) that turns the game's local co-op (up to three players) into online co-op between Steam friends, by deterministic lockstep over the game's own input. The game ships Steamworks (`steam_api64.dll`, initialized by the game through Facepunch.Steamworks); the mod calls Steam's flat API directly.

## Start here

1. **Read [STATUS.md](STATUS.md) first.** It holds the current state (branch, latest release, what is installed on the user's PC, what is committed/pushed/published), the open items by priority, and what has and hasn't been verified.
2. Then, as needed:
   - [docs/FINDINGS.md](docs/FINDINGS.md): causes and fixes found so far, in detail (numbered sections; other docs cite them as "findings §N"). New findings get the next number.
   - [docs/PLAN.md](docs/PLAN.md): the drop-in / drop-out plan, its stages, progress and risks, with the spike instructions as an appendix.
   - [docs/messages.csv](docs/messages.csv): every player-facing string, with where it shows, its length and its source line.
3. Before ending a session with meaningful changes, update STATUS.md (state, what changed, open items). Don't add handoff files to the repo; a handoff for a fresh chat goes through the `handoff` skill (see *Long sessions*).

Determinism is the whole game: anything a PC reads that isn't synced input or synced state (clocks, hardware, focus, random, async I/O, UI timing) eventually splits the PCs.

## Long sessions

A Stop hook (`.claude/hooks/context-handoff.js`, set up in `.claude/settings.json`) checks the chat's size at the end of every turn. From about 375K tokens, approaching the 400K limit, it asks Claude to finish the current step, update STATUS.md, call the `handoff` skill (`.claude/skills/handoff`, from mattpocock/skills), and post a short message starting with "Handoff from the previous chat". The handoff document goes to `%TEMP%`, not the repo. It asks again every further 25K tokens until that message is posted. The user can also type `/handoff` at any time.

## Layout

```
SosOnline/          plugin source (net6.0, references the game's BepInEx interop assemblies)
  Game/             game wrappers, save mirroring
  Lockstep/         input capture/override, lockstep engine, state sync
  Net/              session, protocol, transports (Steam, UDP)
  Steam/            steam_api64 flat-API bindings, invites
  UI/               native menus, localization bridge, toasts, debug panel
  Update/           manual updates from signed GitHub releases
tests/              two-instance test scripts and input scripts
tools/install-to-game.ps1   build and install into your own game folder (installs BepInEx first if missing)
tools/make-release-zip.ps1  build the signed zip players install, in dist/ (nothing uploaded)
tools/publish-release.ps1   push main and create the GitHub release (runs make-release-zip.ps1)
tools/new-signing-key.ps1   create the release signing key (once)
```

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
| Levels loaded during play | The golem's throw and other levels the game loads next to the current one (`LevelLoader`, outside a level load) activate their scenes and start on frames the host picks, with game time held in between; replays take those frames from the area log. | `Lockstep/AdditiveBarrier.cs`, `Net/SessionAdditive.cs` |
| Save backups | Before the game writes or deletes a save file, and before a new session replaces a guest's mirror, the old file goes to `SosOnlineBackup\Saves\<file>\` (own saves) or `SosOnlineBackup\Session saves\<file>\` (the mirror), named by when it was written; identical content is skipped; the last 50 per file plus one per day for 30 days are kept. When co-op starts, ends, a guest joins, or a desync is found, the session's saves are copied to `SosOnlineBackup\Sessions\<date-time> <what>\` (last 30). Test runs keep their backups inside their sandbox and may only write saves inside `SosOnlineTest-*` or `SosOnlineSession-*`: anything else is redirected to `SosOnlineTest-blocked` with an error. | `Game/SaveBackup.cs` |
| Save mirroring | The host sends the save the session plays from (that slot and the index) to the guests. Guests redirect the game's save I/O to `SosOnlineSession\`, so their own saves are never read or overwritten. Options stay each PC's own. | `Game/SaveMirror.cs` |
| Host fixes | Every 30 frames the host sends a snapshot: scenes, RNG, party positions. Guests compare it with their own state at that frame, correct small position drift in place, and report real divergence as a desync. | `Lockstep/StateSync.cs` |
| Session and transport | Friends-only Steam lobby, Steam Networking Messages (relayed by Valve, NAT handled), star topology through the host, handshake checks (mod protocol, game version, input configuration). | `Net/Session.cs`, `Net/Transport.cs`, `Steam/SteamApi.cs` |
| Steam joins | `+connect_lobby` at launch, overlay invites and friends-list **Join Game** while running (observed from Facepunch's callback dispatcher). | `Steam/SteamInvites.cs` |
| DLC | During a session a DLC counts only if every player owns it. The game checks DLC per PC (`PlatformManager.IsDLCUsable`), so an owner played other scenes and got other new-game items than a friend from the same save. Each PC sends the DLC it owns in its hello; the host fixes the session's set (at the start, narrowed on a joiner's join frame, which then always reloads in place) and every PC answers from it. A save in the DLC can't be played with a friend who lacks it. | `Game/DlcGate.cs` |

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
| Pause screen → **Leave Co-op**, or pause menu → **Online** → **Leave** | The game's pause menu is driven by synced input, so every PC runs the choice on the same frame. The player's inputs already sent are still used; the slot is empty from frame + delay + 1, on every PC. |
| Quitting, or the title-screen **Leave** | The host picks the leave frame: the first frame after the last input it received from them, which it has already forwarded to everyone. It broadcasts that frame. |
| Crash or lost connection | The host notices at once when Steam reports the connection lost, or after 2 s without input from that player, then does the same as above. |

- On the leave frame, every PC calls the game's own `SinglePlayerPlusPlayer.Leave()`, and their character goes back to the party AI.
- The leaver keeps playing solo on their copy of the host's save.
- Only the host ending co-op ends it for everyone. The host then leaves the Steam lobby 1.5 s after the end frame (guests stop on that frame first), so a friend who was still joining can't start a new session. **Open on Start** stays as the host set it.
- **Back to solo.** Whenever lockstep stops on a PC (a leave, the end, a lost connection), that PC moves its keyboard and pads to the game's P1, who controls the leader, and makes every other co-op player leave, so the party AI takes their character. If the game refuses a leave (a battle, a cutscene) it retries every second. A guest that is re-syncing is left alone: it drops in again.

**Drop-in.** Lockstep syncs inputs, not world state, so a newcomer can't be streamed into a running game. It rebuilds the game instead: by a short reload for everyone, or by a replay on the joiner's PC.

*Rejoin* (host setting, **Host Settings** → **Rejoin**; `Settings.Rejoin`, `Rejoin` in `sosonline.coop.cfg`). It covers every reload that affects everyone: a new friend, a guest reconnecting, a guest that got out of sync and can't catch up.
- **Require Approval (Safest)**, the default: the friend waits until the host lets them in. The host gets a toast (*Robin wants to join. Pause to let them in.*, or *Robin needs a reload to rejoin. Pause to let them in.*), a held *[P3] Wants to join* notice and, right under **Resume** on its own pause screen (also its battle and Return to Area Start pages), **Let Robin In** (`Session.LetIn`). Then everyone reloads in place (below) at the next area entry. The friend sees *Waiting for Alex to let you in…*, then *Alex let you in. You'll join in the next area.* (`Msg.Welcome` is sent again).
- **Auto (Experimental)**: the same reload without asking.
- **Off**: friends join only while the host is on the title screen (`Can't join: Alex is playing. Join when they're on the title screen.`). A guest that gets out of sync and can't catch up is let go (*your game went out of sync. Regroup at the title screen.*), and guests don't reconnect.
- A re-syncing guest (a desync) first tries a quiet catch-up replay (below) in every mode but Off's let-go; only when that can't work does it need the reload, and the setting decides.
- **Catch Up** (tests only, `--sosonline-joinmode=catchup`, the default of test runs): the replay first for new friends too, the reload as its fallback. Not in the menus.
- Choosing **Return to Area Start** (host's pause screen, while someone waits) lets everyone who waits in at once: the area starts over from its entry save.

*Reconnecting.* A guest that loses the host (Steam reports the connection lost, or 10 s without its input; the host drops a silent guest after 2 s) while co-op runs, with Rejoin not Off and co-op not ended by the host, doesn't go back to the title screen: it shows *Lost connection to Alex. Reconnecting…* and tries the same lobby (or UDP address) every 5 s for 60 s, giving up an attempt the host hasn't answered (`Session.LostHost`, `TryReconnect`). The host has dropped the slot by then, so the guest comes back as a rejoin, under the Rejoin setting. A guest the host removed, or one whose host ended co-op, doesn't retry. After 60 s: *Couldn't reconnect to Alex*.

*Catching up (only the joiner waits).*
- On every level load's last frame (the area entry), every PC autosaves. The host keeps a copy of that save, the inputs of every frame since, and its state snapshots (`Net/AreaLog.cs`).
- A joiner gets that save, loads it, and replays the inputs since the area entry with rendering and sound off (about 800 frames per second on loopback), checking the host's snapshots as it goes. Then it follows the live inputs.
- Once it is level with the session, the host picks a merge frame: every PC adds the player there and the joiner takes a party member. The players already in never stop.
- If a player joined or left since the area entry, the replay would be over 5 minutes, it disagrees with a host snapshot, or it takes over 2 minutes, the joiner drops in by a reload instead (below).

*Reload in place.* Everyone reloads the area entry autosave in place, behind the game's own fade, without leaving gameplay. Only at an area entry: the save doesn't hold a room's own state, so a reload mid-room undid it (a placed stone and the wall it raised came back unsolved; findings §20).
- A friend who joins mid-session is admitted as *waiting*. Their game tells the host once it has finished starting (`Msg.Booted`). At the next level load the host schedules (`BeginDropInAtEntry`, when it picks the release frame), the drop-in comes along: every PC autosaves on the release frame anyway (`AreaLog.SaveAtEntry`), and that save is the drop-in's. If the game doesn't save there (a scene at the entry), the next area entry tries again. Lockstep keeps running.
- **Players hold still** from that frame until the join frame (`LockstepEngine.HoldPlayers`): every player's input is applied as empty (the system channel still runs), so nothing done in between, such as pulling a lever, is undone by the reload. Everyone sees *Robin is joining. Hold on…*. The hold ends (`Msg.PlayersRelease`, scheduled like other host messages) if the joiner isn't ready within 20 s, the area changes or a battle or scene starts; the next area entry tries again. The area log records the held (empty) input, so a replay crosses a hold.
- The host sends its saves to the joiner, who loads them into its mirror folder wherever it is (title screen or its own game, once the game has finished booting) and reports ready.
- The host picks a join frame a few frames ahead and sends it, with where every party member stands, to everyone. The joiner starts lockstep on that frame with the inputs the host has buffered for it.
- On the join frame every PC, the joiner included, loads the autosave in place (`GameBridge.ReloadInPlace`): the leader spawns where it stood, the loading barrier ends the load on one frame everywhere, and two frames later every party member is put back on its own position.
- Co-op players aren't in the save, so on the join frame every PC lets the guests leave co-op, and each guest takes a party member again automatically: the mod presses the game's co-op **Join** for them, through synced input. The same happens after a session start.
- Nothing happens between the autosave and the join frame (the hold), so no progress is undone.
- If a level load or battle is running on the join frame, every PC sees it on that frame and the join is postponed until the next area entry.
- Test scripts that walk inside one area keep the old trigger with `--sosonline-dropinanywhere` (a fresh autosave once the game has allowed saving for half a second); `tests/dropin.sh` with `DOOR=F` tests the real path through a synced test door (`--sosonline-door=F`).

**Assign Players.** The game's own screen, made session-aware.
- Offline it is device-driven. A gamepad joins by pressing a button (the screen polls every *local* device), and finishing moves devices between the game's players P1–P3.
- Online:
  - The screen is rebuilt identically on every PC (the players currently playing, plus whoever opened it).
  - Players join and confirm with their synced input. Keyboard players work too, through fallbacks from the regular UI actions.
  - The game's device shuffling is ignored during sessions.
  - On submit, each session slot is remapped to the player number its cursor chose, on every PC. Cursor, ready and the game's character assignment already run on synced input.
  - Every online player stands in with the keyboard icon. Each icon shows the player's name under it and is tinted in their colour (P1 cyan, P2 amber, P3 pink), so a player's keyboard reads as theirs even in another player's column. Two players can pick the same character, as in the game; their icons stack in that column.

**Verified** (loopback, 2–3 instances):

| Scenario | Result |
|---|---|
| Guest leaves | Applied on frame 905; the host keeps playing alone. |
| Guest process killed | The host drops the slot after 2 s and keeps playing. |
| Synced pause-menu leave | Applied on frame 905 on both PCs. |
| Third player joins mid-session (drop-in in place) | The joiner is booting when it joins; the first autosave goes stale and is replaced. Existing players: 92–95 frames (about 1.6 s) from the join frame to the load's release, never via the title screen; both guests play again 5 frames later and the joiner walks its character. After the join, the joiner's state matches the host's exactly (73–81 of 73–81 snapshots, four runs); guest 1 differs only by up to 1 cm in positions, within the 2 cm correction, with no mismatch reported. |
| Assign Players | Opened online; P2's cursor took the P1 column. After confirm, on both PCs the guest drives P1/Zale and the host P2/Valere. The screen closes through the game's own path; all 57 snapshots match. |

### Battle turns

**The game's way** (traced 2026-11-26): a battle starts by unassigning every co-op character. For each player turn the game takes the next character from the turn queue (`EncounterController.GetNextActor`), picks a player at random from the seeded RNG (one turn per player per round), makes every party actor answer to that player (`PlayerCombatActor.SetPlayer`) and calls `SetCurrentPlayer`. That player can move the cursor to any character that still acts. The game waits for them: an idle player stalls the battle.

**Your character, your turn** (`Lockstep/BattleTurns.cs`, host setting **Battle Turns**: **Custom** by default; **Original** is the game's own way, fixed when the session starts and sent with every lockstep start):
- Outside battles every PC records which character each slot controls, on the applied frame (synced state).
- `SetCurrentPlayer` gets the owner of the character the turn starts on; when nobody playing owns it, the game's pick (or, if that player has nothing left this round, the owner of the first character still to act). The actors are set to answer to that player.
- The cursor starts on the current player's own character (or one nobody owns), and moving it skips characters other players own.

**Host commands** (host pause screen, which opens in battles for the host only; the game has no pause there): **Take This Turn** and **Pass Turn to P2** go out as `Msg.TurnCommand` on a frame every PC can still schedule; on it every PC calls `SetCurrentPlayer` for the receiver, who may then control any character until that turn ends. Nothing happens if no player is choosing a command by then. **Players** → a player → **Remove** (also outside battles) sends `Msg.Removed` and the usual scheduled leave. A catch-up replay doesn't see turn commands (they aren't in the area log): a joiner replaying across one diverges and falls back to a reload.

**Level ups** (`Lockstep/LevelUps.cs`, the same **Battle Turns** setting). The game's way (traced 2026-11-28): after a won battle `LevelUpSequence` fades out, loads its own `LevelUp` scene, and `LevelUpSceneController` levels up one character at a time, each with a `LevelUpScreen` of stars (the bonus choices). The cursor moves with the UI player's input (`SinglePlayerPlusManager.GetUIInputsPlayer`), but the screen's `Update` confirms with the game's first player (`InputManager.FirstPlayer`, P1): in local co-op P1 picks every bonus.
- With **Custom**, as a character's level up starts (`LevelUpCharacter`, and again at `LevelUpScreen.Init`), every PC makes its owner (the same record as battle turns) the UI player, and the screen's `Update` checks the owner's synced confirm instead of P1's. A character nobody playing owns keeps the game's way. The UI player goes back to the game's own at `EndLevelUp`.
- *[P2] Level Up* shows on every PC while the screen is open (`MenuStatus`).
- **Level-up barrier.** The `LevelUp` scene loads asynchronously and arrived a frame apart on two PCs, so the whole level up ran a frame apart, the character stood half a step away and the cursor started on another star: the same confirm picked another bonus (split). Now game time stands still from the end of the fade (`LevelUpSequence.OnTransitionInDone`, the same frame everywhere); `LevelUpSceneController.DoLevelUp` waits until every PC has the scene (`Msg.LevelUpReady`), and every PC starts it on the release frame the host picks (`Msg.LevelUpRelease`, in the area log for replays; protocol 19). The hold starts and ends on the same frames everywhere, so the clock needs no new base; state isn't compared meanwhile (`LevelUps.Holding`).
- Test: `tests/level-up.sh` (`OWN=1` Custom, `OWN=0` Original), with `--sosonline-awardxp=F` (XP one short of the next level), `--sosonline-onehit` (enemies die on their next hit) and `--sosonline-botlevelup=N` (the bot moves the cursor N stars right, then confirms).

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
  - Entries: **Host**, **Join** (friends who are hosting; *No friends are hosting right now* when none), **Load Game** (pick the save), **Invite Friends** (Steam overlay), **Host Settings**, **Ask for Logs**, **Mod Settings**, **Stop Hosting** / **Leave Co-op**, **Back** (each only when it applies). **Host Settings** holds **Open on Start**, **Rejoin**, **Battle Turns** and **Input Delay** (Auto, Low, Medium, High: 0, 3, 5, 8 frames); **Mod Settings** holds **Check for Updates**, **Send Logs to Host**, **Status Bar** and **Open Logs Folder**. Settings cycle in place (`UI/SettingLabels.cs` holds their names, values and help, shared with the pause screen); the status line explains the highlighted one.
  - **Open on Start** opens the game to friends every time it starts, title screen included. Opening by hand (**Host**, or pause → **Online** → **Open**) and ending co-op never change it. The status under the list is one line (the players are on the player bar), so the list stays above the game's copyright row (the status is hidden on a page long enough to reach it). Entries are in Title Case. B puts the cursor on **Back**; B on **Back** goes back, and the cursor lands on the entry that opened the page. Once an update is downloaded, **Restart to Update (0.45 -> 0.46)** comes first.
- **Pause menu:**
  - An **Online** item, added with the game's own `PauseMenu.AddPauseMenuItem`.
  - It opens the game's standard two-choice popup.
  - During a session the pause menu is driven by synced input, so its choices act as deterministic commands. *End Co-op* stops co-op on every PC on the same frame, and everyone keeps playing solo from an identical state. Its choices may depend only on synced state, so **Restart** (which depends on this PC's download) is offered there only outside a running session.
  - The game's popup buttons are sized for "Yes"/"No"; the mod widens each to its label, up to 200 reference pixels (about 25 characters), and cuts a longer one with an ellipsis.
- **Text:** the mod's strings go through the game's localization (`LocalizationManager.GetText` patched for a private `SosOnline` category).
  - Every player-facing string is listed in [docs/messages.csv](docs/messages.csv): where it shows, its length and its source line. Wording changes are reviewed there first.
  - Toasts (the notices at the top) hold at most 80 characters: one short sentence about what happened, at most what to do. Longer text is cut at a word and logged as a warning. They stay 2.5 s + 0.06 s per character (3.5–12 s).
  - Player names are cleaned where they enter the mod (`UI/PlayerNames.cs`): no control, zero-width or line-break characters, `<` and `>` replaced so a name can't inject text markup, at most 24 characters, and "Player N" when the game's fonts can't draw most of it.
- **Player bar** (`UI/PlayerBar.cs`): a column at the top left, in the frame of the game's co-op notices (no ornament). One row per player: their P icon (the game's `icn-player-square-N`, by the game's player number) and, only when it matters, a status icon: *Joining* or *Waiting* (this PC waited over 0.1 s for their input in the last 1.5 s) with the game's clock, *Paused* with its paused clock, *Re-syncing* or the ping above 150 ms with its warning sign. In play it shows icons only, all the time. On the title screen, the pause screens (the mod's own and the game's), Assign Players and the save screen it stays and adds names and the status words; over the game's other menus (main menu, shops, map), which have a panel there, it hides. Each column is as wide as its widest entry, so rows never move when a status comes or goes.
- **Player notices** (`UI/PlayerNotice.cs`): the look and place of the game's own co-op notice ("[P3] LEFT", right side; measured with `--sosonline-uiprobetext`), stacked above the game's own. *[P2]'s turn* / *[P1] Your turn* for 2.5 s when a battle turn goes to another player; *[P2] Paused* on the other PCs while that player has their own pause screen open; *[P2] Assign Players* (the menu in use) on every PC while a player has the game's menu open for everyone. The game's own join and leave notices stay; during a session they get the player's name in front: *[P3] ROBIN LEFT*. The top toasts are for session events (co-op started or ended, a lost connection, updates).
- **Pause screen** (`UI/LocalPause.cs`, this PC only). Host: **Resume**, **Let Robin In** (one per friend waiting), **Return to Area Start** (while someone waits), **Options**, **Pause for Everyone**, **Players** (each player → **Remove**; **Ask for Logs**), **Host Settings** (**Open on Start** and **Rejoin** change here; **Battle Turns** and **Input Delay** show locked, *Set when co-op starts*), **End Co-op**. Guest: Resume, Options, Pause for Everyone, **Send Logs to Host**, **Leave Co-op**. Battle page (host): Resume, Let Robin In, **Take This Turn**, **Pass Turn to P2**, Players. Where saving isn't allowed while a friend waits: Resume, Let Robin In, **Return to Area Start**, Pause for Everyone. The cursor stays on the same entry by label when the list changes; Confirm or left/right cycle a setting.
- **Status Bar** (Mod Settings, or the pause screen's Options; this PC only): **On** (default) shows the player bar; **Off** hides it in play but not on the title screen or the mod's pause screen; **Advanced** also shows the old one-line status (players, ping, sync, waits) at the top right. The old `AdvancedStatusBar` setting is migrated to Advanced.
- **F8** opens a small debug panel.

### Writing player-facing text

Every string a player sees (toasts, errors, status lines, popups and their choices, menu and pause entries, settings and their help, notices, loading screens) should read like Sea of Stars' own UI: as short as it can be, with only what helps the player decide or understand what happened.

**Workflow:**
1. **Look the string up first**, in the code and in `docs/messages.csv`. A string that already exists gets its own row updated (same `id`, `current_text` as the code shows it now, `text` the proposal), never a duplicate row. Rows marked *Removed* in `review_notes` were taken out on purpose; say why before bringing one back.
2. **Match the neighbours:** read the rows of the same `kind` and `where`. Where the user's edits (their `review_notes`) differ from these notes, the edits win.
3. **Measure** with placeholders filled at their longest: player names are up to 24 characters (12 on Assign Players tags). A `{names}` list must read for one name and for several ("Robin, Sam wants to join" doesn't), and a fallback like "the host" must read too.
4. **Write the CSV rows before the code**, with `review_notes` saying what changed and the date, and list them for review. The file is UTF-8 with a byte order mark.

| Kind | Limit |
|---|---|
| Toast, or an error shown as one | 80 characters filled (`Toast.MaxChars`); aim for 30–50 |
| Popup choice | about 25 characters (`NativeMenus.MaxChoiceWidth`) |
| Menu and pause entries, settings | 1–3 words, like the game's own entries |
| Status line under the Online list | one line |

**Wording:**
- **Players' words, not ours:** "co-op", not "session"; "join", not "drop in"; "out of sync", not "desync"; "rejoin", not "re-sync". No lockstep, catch-up, peer, slot, mirror, snapshot, frame or protocol.
- **Only what changes the decision.** Keep consequences that matter ("Any progress since your last save will be lost."), and drop trailing explanations.
- **Buttons lean on the question:** "End co-op for everyone?" → **End** / **Back**.
- **Name players** ("Robin joined"), use "you" for the reader and "they" for a player, and use game terms as the game writes them (*Title screen*, *party*, *area*, *[P2]*).
- **A little warmth when things go right** ("You're in!", "Welcome back!"), and none in errors: say what happened and what to do.
- **Contractions as in speech:** "can't", never "cannot". No noun-verb contractions: "Robin is joining", not "Robin's joining".

**Punctuation and capitals:**
- **Toasts, errors and status lines:**
  - one sentence has no final period ("Join a friend first");
  - two or more sentences get periods ("Co-op ended. You may continue playing.");
  - "!" stays;
  - anything in progress ends with `…` ("Joining…").
- **Popup text and pause hints** are ordinary sentences with periods.
- **Entries, choices, titles and notices** have no final punctuation, except "?" on a confirming title ("Remove P2 Robin?").
- **No em dashes** in UI text; use a colon or a second short sentence.
- **Title Case** for menu and pause entries, popup choices, page titles and setting names (**Let Robin In**, **Return to Area Start**). **Sentence case** for sentences and for option rows that mirror the game's (*Master volume*). "Co-op" is capitalized only at the start of a sentence or in a Title Case label.

## Build and install

**Game folder.** Every tool, test script and the build find Sea of Stars through `tools/game-dir.ps1`: `-GameDir`, else the `SOS_GAME_DIR` environment variable, else `game-dir.txt` in the repository root, else Steam's library folders. The folder it finds is written to `game-dir.txt`, which is gitignored: never put a PC's own path in a committed file. Test scripts get it as `$GAME` from `tests/game-dir.sh` (the `.cmd` scripts call the resolver directly).

Requirements: Sea of Stars on Steam (Windows) and the .NET 6+ SDK.

```powershell
powershell -ExecutionPolicy Bypass -File tools\install-to-game.ps1
```

- The first run installs BepInEx 6 IL2CPP (it asks first). Start the game once so BepInEx can generate its interop assemblies, quit, then run the script again to build and install the plugin.
- To pack a zip without releasing it (needs PowerShell 7):

```powershell
pwsh -File tools\make-release-zip.ps1
```

  It writes `dist\SosOnline-<version>.zip`, signed when the release key exists.

## Updates (manual)

Updates are manual since 2026-11-17 (`Update/AutoUpdate.cs`); before, the mod downloaded and installed every newer release at start.

- **At start** the mod only looks up the [latest release](https://github.com/miguelguzmanr/sos-online/releases/latest) (one request to `api.github.com`; not in test runs). If it's newer, `AutoUpdate.Available` is set.
- **Title screen version line** (`UI/VersionLabel.cs`): *sos-online 0.56*, plus *(0.57 available)*, bottom-left. It's a copy of the game's own version text (`VersionInfo`), moved to the root canvas (the game's line sits in a layout group) and mirrored: as far from the left edge as the game's drawn text is from the right edge, same bottom.
- **Versions as players see them:** `AutoUpdate.Display` shows 0.1.56 as "0.56" (releases are always 0.1.&lt;commit count&gt;). Logs and assembly versions keep the full 0.1.56.
- **Check for Updates** (title screen → Online → Settings) looks up the release and asks: *Up to date (0.56).*, or *Update 0.57 is available.* → Install / Back. It downloads the zip and installs the DLL only if its signature matches the release key built into the mod (the running DLL is renamed to `.old`; nothing downloaded is loaded in the running game). On a locally modified build (`…-modified`) it offers the published release even when that isn't newer.
- **Restart to update (0.56 -> 0.57)** then appears in the Online menu (and **Restart** in the pause menu's Online popup, outside a running session). It quits the game; a hidden PowerShell waits for it to exit and starts it again through Steam (`steam://rungameid/1244090`).
- If someone joins with a different version, the host refuses them and names both versions.
- The old `AutoUpdate` setting in `BepInEx\config\sosonline.coop.cfg` is no longer read.

## Releasing

Needs PowerShell 7, the GitHub CLI (`gh`, logged in), and the release signing key.

1. Once: `pwsh -File tools\new-signing-key.ps1` creates the key in `%APPDATA%\SosOnline\release-signing-key.txt` and prints the public key, which goes in `SosOnline\Update\AutoUpdate.cs`. **Back up the private key and never commit it.** Without it, players need one manual install of a build signed with a new key.
2. For each release, commit everything on `main`, then run:

```powershell
pwsh -File tools\publish-release.ps1
```

   It pushes `main`, builds and signs `dist\SosOnline-0.1.<count>+<commit>.zip`, and creates release `v0.1.<count>`. The count is the number of commits, so versions always increase. Players see *(Update available)* at their next game start and install it from the Online menu.

## Testing

`tests\` runs two game instances on one PC over loopback UDP (no Steam networking), with scripted input, screenshots and per-frame state logs in `BepInEx\SosOnline\`.

| Script | What it checks |
|---|---|
| `tests\solo.cmd` | Hosts with no guests, starts from save 0, screenshots, quits. |
| `tests\two-instance.cmd` | Host (UDP 27100) and guest (27101): handshake, save mirroring, synced start. Set `SCRIPT_HOST`/`SCRIPT_GUEST` to input scripts in `tests\scripts\`. |
| `tests\uitest.cmd` | Screenshots of the title screen Online menu, hosts a real Steam lobby, leaves. |
| `tests/ab-run.sh` | Runs solo battle instances in parallel (each `"port\|flags"`), e.g. timing bots with and without `--sosonline-nocomp`. |
| `tests/battle-map.sh`, `tests/map-results.sh` | Maps the game's timed-hit and block grading: one run per press offset. |
| `tests/battle-live.sh` | Two instances, a battle on both from frame 1500: prints the guest's corrections and mismatches (finding 8). |
| `tests/dropin.sh` | Three instances: a third player joins 30 s in (`JOIN_DELAY`). `HOSTFLAGS`/`JOINFLAGS` add flags, e.g. `--sosonline-nocatchup` (reload in place), `--sosonline-loadatjoin`, `--sosonline-play=0` (join from its own game). Reloads join anywhere (`--sosonline-dropinanywhere`) unless `DOOR=F`: then at a synced test door on frame F. Compares both guests' states with the host's. |
| `tests/dropin-two.sh` | Host alone, then two joiners in one area; both catch up from the same area entry. With `DOOR=F`, a short `JOIN2_DELAY` and reloads (as in the sweep's `dropin-two-door`), both are waiting at a synced test door and join with one reload. |
| `tests/stage1.sh` | The host plays its own save with the lobby open; guest A joins, leaves from the pause screen (co-op to solo), guest B joins later. |
| `tests/menu-status.sh` | Pause screen and menu notice while a third player catches up. |
| `tests/battle-turns.sh` | A battle where both players take turns over a simulated lagged link (`LAG`, `JITTER`, `SPIKES`); prints both PCs' turn decisions and the first differing snapshot. Run it 3–4 times: battle splits are intermittent. |
| `tests/level-up.sh` | Two players win a battle that ends in a level up (enemies die on one hit). Prints who chose each bonus on both PCs: with `OWN=1` (Custom) each character's owner, with `OWN=0` (Original) P1, as in the game. Pass: identical `[levelup]` lines, 0 snapshots differ. |
| `tests/save-sync.sh` | A manual save (`SAVER=host` or `guest`) over a lagged link; both PCs must open and close the question on the same frames. |
| `tests/resync.sh` | A forced divergence (`DIVERGE`): the guest rejoins by replay (or a reload); compares it with the host afterwards. `EXTRA=--sosonline-hpdivergeagain` makes the replay diverge too (the reload fallback). |
| `tests/reconnect.sh` | A guest's network drops for 8 s (`DROP=F:S`) mid-session; it reconnects by itself and rejoins (`MODE`, default `auto`). Compares it with the host after the rejoin. |
| `tests/sweep.sh` | Every test above that checks the session, one after another (about 50 minutes), plus leaving, ending, Assign Players, camping and the title-screen menus: the check before a release. Results and logs per test in `BepInEx/SosOnline/sweep`. |
| `tests/yeet.sh` | A level loaded during play: from `SAVE` (an autosave next to the Evermist Island golems) both PCs start the golem's throw at `YEET`; prints the additive barrier's steps and compares the snapshots. `DIVERGE=F` makes the guest rejoin by replay across it. |
| `tests/spike-b.sh` | Records a run, replays it from the area entry autosave, compares every frame. |

Battle flags: `--sosonline-encounter=F` starts the nearest encounter on lockstep frame F (every peer). `--sosonline-bot` confirms menus and presses timed hits `--sosonline-botoffset` frames after the impact it sees; `--sosonline-bot=menu` only confirms menus. `--sosonline-botblock=N` blocks N frames after the block window opens on this PC (the window opens about 90 frames before the hit). `--sosonline-trace` logs the combat timing calls, and `--sosonline-nocomp` disables client-side timing.

Session flags:

| Flag | Effect |
|---|---|
| `--sosonline-leave=F` | Guest leaves over the network on frame F. |
| `--sosonline-kill=F` | Guest's process is killed on frame F. |
| `--sosonline-pauseleave=F` | Synced pause-menu leave of P2 on frame F. |
| `--sosonline-assigntest=F` | Opens Assign Players on frame F (from the game's pause menu, or directly when it isn't open; pass it to every instance). |
| `--sosonline-endsession=F` | The host ends co-op from its pause screen on frame F. |
| `--sosonline-pausechoose=F:Label,…` | Chooses a local pause screen entry by its label on frame F (`_` for a space), e.g. `1760:Leave_Co-op,1800:Leave`. |
| `--sosonline-campat=F` | Uses the nearest campfire as if P1 pressed Interact (pass it to every instance). An early save only gets "The campfire feels cozy". |
| `--sosonline-campstart=F`, `--sosonline-campstop=F` | The campfire's own camping toggle, past the story unlock: intro, camp, and the Rest choice when stopped. |
| `--sosonline-yeet=F` | Starts the throw of the golem nearest the leader on frame F (pass it to every instance): its level loads during play. |
| `--sosonline-noadditive` | No barrier for levels loaded during play (reproduces findings §23). |
| `--sosonline-fakeupdate=V` | Acts as if update V was downloaded (shows **Restart to update**). |
| `--sosonline-fakeavailable=V` | Acts as if the lookup at start found V (the version line says *(Update available)*). |
| `--sosonline-versionshot` | Title screen: screenshot `ui-version` with the version line, then quit. Never opens a save. |
| `--sosonline-titlebarshot` | Title screen: opens the Online page; once another player is in the session, logs the player bar's state (`[test] player bar: …`) and screenshots `ui-titlebar`, then on the host Load Game (`ui-titlebar-saves`); leaves and quits. Pass it to the host (`--sosonline-host`, no `--sosonline-start`) and a guest. Never opens a save. |
| `--sosonline-sendlogs=F` | A guest chooses **Send Logs to Host** once lockstep reaches frame F. |
| `--sosonline-guardtest` | Logs where the test-run save guard sends a few paths (real folder, sandbox, mirrors); writes nothing. |
| `--sosonline-nodlc` | This instance acts as a PC without any DLC, in and out of sessions. |
| `--sosonline-name=TEXT` | A test player name; escapes like `\n` and `\u0020` for characters a command line can't carry. |
| `--sosonline-ownturns=0\|1` | Overrides the host's **Battle Turns** setting (1: Custom, 0: Original). |
| `--sosonline-joinmode=ask\|auto\|off\|catchup` | The host's **Rejoin** setting (test runs default to `catchup`, the replay first). |
| `--sosonline-door=F,…` | Every PC loads its current area again on frame F: a synced door, so a waiting friend joins (pass it to every instance in the session). |
| `--sosonline-netdrop=F:S` | From frame F this PC sends and receives nothing for S seconds, once (UDP): a lost connection. |
| `--sosonline-dropinanywhere` | Host: drop-ins by a reload happen wherever the game allows saving, not only at an area entry (for test scripts that stay in one area). |
| `--sosonline-pauseopen=F,…` | Opens this PC's own pause screen on frame F, as Start would, and logs its entries. With `--sosonline-pausechoose`, e.g. `2700` and `2720:Let_Robin_In`. |
| `--sosonline-turncmd=F:1\|2,…` | The host takes over (1) or passes (2) the current battle turn on frame F. |
| `--sosonline-removeat=F:SLOT` | The host removes that slot's player on frame F. |
| `--sosonline-spritedump=F,…` | Logs every loaded sprite's name, texture and size (once per name) on those frames. |
| `--sosonline-background` | Runs behind your work: muted, this PC's keyboard, mouse and pads ignored. `tests/launch.sh` passes it unless `VISIBLE=1`. |

`tests/session.sh` launches a host plus guests, staggering late joiners. It doesn't start a late joiner once the host's game has quit.

**Tests run in the background** by default, so the PC stays usable during a run (sweep included). Every bash script starts its instances through `tests/launch.sh` (`start_game`):

| What | How |
|---|---|
| No window, no focus, none of your typing | `tests/start-game.ps1` starts the game on a hidden Win32 desktop (`CreateDesktop` "SosOnlineTests", never switched to). Its windows never show on yours, it can't take the foreground there (Unity activates its window on start), and keyboard and mouse input only go to the desktop on screen. Unity renders as usual there: screenshots work. Moving the window off-screen wasn't enough: Unity shows its window centred for about 0.4 s before it can be moved, and Windows often refuses a background process handing the focus back. |
| No console | BepInEx's console window (`Logging.Console` in `BepInEx\config\BepInEx.cfg`) would open for every instance: background runs turn it off; it comes back on with the display preferences (below). |
| No sound, no pads | The game gets `--sosonline-background` (`SosOnline/BackgroundRun.cs`): Wwise output muted, Rewired's keyboard, mouse and joysticks disabled, and lockstep input capture returns nothing (pads are read on any desktop). Scripts and bots still drive it. |
| Full speed | `Application.runInBackground` stays on, and the launcher opts the process out of Windows 11 background throttling (EcoQoS and timer resolution). `menu-status.sh` took 94 s in the background against 102 s on screen, with the same results. |

To look at a background instance, a tool that switches desktops (e.g. Sysinternals Desktops) would have to open "SosOnlineTests"; usually the screenshots are enough.

`VISIBLE=1` brings back the old way: windows on screen, tiled side by side, host on the left (`tests/place-window.ps1`; `NO_TILE=1` leaves them where Unity puts them), with sound and your keyboard and pads. Use it to watch a run. The `.cmd` scripts (`solo`, `two-instance`, `uitest`) still start visible windows; `sweep.sh` starts the uitest instance through `launch.sh`.

Test instances never touch your real saves:
- Each UDP test instance plays from its own sandbox copy (`<saves>\SosOnlineTest-<port>`) and its own mirror folder.
- Delete those folders when done.

Unity stores the `-screen-*` test flags as the game's display preferences (resolution, window mode) when an instance quits. Every test instance, from `launch.sh` and the `.cmd` scripts, first runs `tests/display-prefs.sh ensure`: it backs up the game's registry key unless a backup already exists (so a later save never takes test values) and starts a watcher. Once no instance started with `--sosonline-*` flags has run for a few seconds, the watcher puts the key back, deletes the backup and turns BepInEx's console back on. That covers interrupted runs and scripts that never restore; the player's own game is never waited for. `save` and `restore` still work by hand (`restore` waits while test instances run).

These scripts need `steam_appid.txt` (containing `1244090`) in the game folder, so the exe can start directly with the test flags.

Compare runs with:

```bash
diff <(cut -d' ' -f2- "BepInEx/SosOnline/state-27100.log") <(cut -d' ' -f2- "BepInEx/SosOnline/state-27101.log")
```

The command-line flags are documented in `SosOnline/TestHarness.cs`.

### Practical notes

Close the game first; builds deploy into the game folder, and a build during a test replaces the mod under it. `steam_appid.txt` must be in the game folder. Tests run in the background, on a hidden desktop; `VISIBLE=1` to watch them.

| Command | Use |
|---|---|
| `bash tests/sweep.sh` | Everything, about 50 minutes; results in `BepInEx/SosOnline/sweep` |
| `ENCOUNTER=650 QUIT=5000 bash tests/battle-turns.sh --sosonline-start=0a` | Battle from the newest autosave over a lagged link. Pass: `guest mismatches 0`, identical turn lines. Run 3–4 times. Add `--sosonline-nolockfix` to reproduce the old Bosslug split |
| `QUIT=1500 bash tests/save-sync.sh --sosonline-saveat=600` | P1 saves; `SAVER=guest ... --sosonline-saveby=1` for P2 |
| `DIVERGE=1600 QUIT=9500 EXTRA="--sosonline-encounter=1500 --sosonline-bot --sosonline-lag=20 --sosonline-jitter=25" bash tests/resync.sh` | Forced re-sync mid-battle (rejoin by replay); add `--sosonline-hpdivergeagain` to `EXTRA` for the reload fallback |
| `tests\uitest.cmd --sosonline-fakeupdate=0.1.99` (cmd or PowerShell) | Title screen menus, *Online > Start*, the restart popup; screenshots `ui-*-steam.png` |

Notes:
- Battle bugs need `--sosonline-lag`/`--sosonline-jitter`: loopback hides real-time dependence.
- **Save slot numbers count from 0.** `--sosonline-start=SLOT[a]` and `--sosonline-play=SLOT` take the file number, not the number the game shows: the game's "Slot 1" is `0` (`SOS_SaveSlot0.sos`; with `a`, `Autosave_SOS_SaveSlot0.sos`), "Slot 2" is `1`, and so on. A slot with no file fails with `Save slot N (auto=…) not found` and the test starts nothing. When the user names a slot, subtract one, and check the file exists in `%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars` first.
- Tests run from the user's newest save (`--sosonline-start=0a`, `--sosonline-play=0`), so what they reach changes when the user plays. STATUS.md notes where that save currently is (area, encounter frame, nearest campfire).
- Run hygiene: display preferences come back by themselves (above); delete `SosOnlineTest-*` and `SosOnlineSession-<port>` in `%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars` afterwards, never `SosOnlineSession` or `SosOnlineBackup`.
- Choose local pause entries by label (`--sosonline-pausechoose=1760:Leave_Co-op,1800:Leave`), not by cursor moves: the entry list changes.
- Message wording: `docs/messages.csv` lists every player-facing string; the user reviews it there. See *Writing player-facing text* above.

## Investigation techniques that worked

- Interop assemblies (`BepInEx\interop\*.dll`) can be reflected from PowerShell (copy them to a scratch folder, `Assembly.LoadFrom` with an `AssemblyResolve` handler) to list types, methods and properties. No method bodies: behaviour has to be found by logging.
- `--sosonline-callprobe=TypeA,TypeB` logs the first call per frame of every method on those types; diffing two PCs' call sequences finds the first divergent call.
- `--sosonline-uiprobe=F` logs every visible screen's images and texts (sprites, fonts, colours): how the Assign Players colours and the game's save screen layout were found.
- `NativeCode.IsFolded`: never patch a method that shares native code with others; prefer postfixes on methods already patched.
- Il2CppInterop rejects `null` for object parameters of native calls (`Il2CppObjectBaseToPtrNotNull`).

## Player-facing behaviour, in full

The README has the short version for players; this is the complete one.

**Start from your own game (no title screen).**
1. **Host:** pause → **Online** → **Open**. A Steam lobby stays open while you play. To open it every time the game starts, turn on **Host Settings** → **Open on Start**. **Invite Friends** opens the Steam overlay.
2. **Friend:** accept the invite, or use **Join Game** in the Steam friends list. From the title screen, **Online** → **Join** works too. Accepting while in your own game asks once, then replaces your game with the host's.
3. With **Rejoin** on **Require Approval** (the default) the host pauses and chooses **Let Robin In**. When the party next enters an area, everyone holds still for a moment and sees a short fade. To let them in at once, the host chooses **Return to Area Start** (the area starts over). Then the friend takes a party member automatically.

**Or start together from the title screen:** Title screen → **Online** → **Host**, friends join, then **Load Game** and pick a save.

**During a session:**
- **Pause** (Start / Esc) opens a pause screen on your PC only; the game keeps running for everyone and the others see "[P2] PAUSED" on the right. It has **Options** (volumes, brightness, display, Status Bar: this PC only), **Pause for Everyone** and **Leave Co-op** (the host: **Players**, **Host Settings** and **End Co-op** for everyone). In battles and cutscenes the game's own pause still stops everyone.
- **Pause for Everyone** opens the game's own pause menu on every PC, as in local co-op: the game stops for everyone and only you move in it. Use it for **Assign Players**, **Return to title** and the like.
- **Choosing who plays whom:** pause → **Pause for Everyone** → **Assign Players**; each player moves their own cursor. A friend who joins a save they played before gets the character they last played in it back, if it's free; the host remembers it per save slot and Steam id, across game starts (`SosOnlineCharacters.json` next to the saves, `Game/CharacterMemory.cs`).
- **Leaving:** pause → **Leave Co-op**. Your character goes back to the party AI and the others keep playing; your own game is yours again (your pad and keyboard drive the leader). When the last friend leaves, the host simply keeps playing; the lobby stays open.
- **Ending:** the host's pause → **End Co-op**. Everyone keeps playing on their own; the lobby closes.
- **Lost connection:** a friend's game tries to reconnect for a minute (*Reconnecting…*) and comes back like a rejoin, so nobody has to go back to the title screen.
- **Camping** works as in local co-op (tested on loopback: intro, camp, Rest and the end of camp on the same frames on both PCs).
- If a friend's game gets out of sync, it fixes itself: small differences (HP, MP, combo points, positions) are corrected in place; otherwise their PC rejoins by replaying the area behind a loading screen (a few seconds, even mid-battle), or, if that can't work, with a short reload for everyone at the next area entry (with Require Approval, once the host lets them in).
- **Check for Updates** (title screen, Online → Mod Settings): looks for a newer release now and asks before installing it; on a locally modified build it offers the published release instead, so the PC is back on a build everyone has. Then Restart (through Steam) or Later.
- **Return to Area Start** (host): while a friend waits to join, the host's pause screen offers it (where saving isn't allowed, a page with only Resume, Let Robin In, this and, outside battles, Pause for Everyone). Everyone goes back to how things were on entering the area, and the friend joins right away; progress since then is lost.

Use a gamepad or keyboard; the mouse is not synchronized. Saves made during co-op are written by the host as usual. On guests they go to a separate session folder, so the guests' own saves are never touched and the host keeps the shared progress. Options stay each PC's own.

### Checklist for a session with a friend over Steam

Nothing Steam-specific has been tested end to end yet (see *Not yet verified* in STATUS.md). Worth checking, in this order:
1. The friend sees the invite and joins from the title screen; both PCs show the friend in the HUD line (F8 opens the debug panel).
2. The friend drops in and takes a party member; both walk around for a few minutes. The HUD should keep saying *synced*.
3. Pause on one PC: only that PC shows the pause screen, the other shows "[P…] PAUSED" on the right.
4. A battle with timed hits from both players.
5. The friend leaves and rejoins (gets the same character back); the host walks into a new area first so the rejoin catches up instead of reloading.
6. Accept an invite while in your own game (one confirmation, then the join).

## Logs

Every start of the game writes its own log, so nothing is lost when you restart.

**Friends: press a button.** While connected to the host's session (also after a crash: rejoin first), choose **Send Logs to Host**. It's on the pause screen (Esc, outside battles and cutscenes) and in the title screen's **Online** → **Mod Settings**. Your game zips your last two starts' logs, BepInEx's `LogOutput.log` (which also has the game's own messages) and the session's save files (`saves/`, about 24 KB each: the state a desync report needs; only from the session folder, never your own saves), and sends them to the host only. If the game crashed since, the zip also includes the newest Windows crash dump of the game (`%LOCALAPPDATA%\CrashDumps\SeaOfStars.exe.*.dmp`, a snapshot of the game's memory at the crash). The host gets a toast and the zip in `BepInEx\SosOnline\logs\received`. The host can also choose **Ask for Logs** (pause → **Players**, or the title screen's Online page), which shows friends a reminder; they still decide.

**By hand** (the host's own logs, or without a session), from the same session on **both PCs**:

1. Open the logs folder: press **F8** in game → **Open logs folder**, or title screen → **Online** → **Mod Settings** → **Open Logs Folder**. It is `BepInEx\SosOnline\logs` in the game folder.
2. Send the newest `sosonline-<date-time>.log` and `state-<date-time>.log` (the file names carry when the game was started). If you restarted the game since the problem, take the pair from that earlier start; the last 10 starts are kept. Test runs (any `--sosonline-*` flag) write their timestamped logs to `BepInEx\SosOnline\logs\tests` instead (last 40 starts, rotated on their own), so they never push real logs out; their **Send logs** and **Open logs folder** use that folder too. The per-instance `log-<port>.txt` and `state-<port>.log` the test scripts read stay in `BepInEx\SosOnline`.
3. Say roughly what happened and when (the log lines carry the time of day).
4. If the game crashed, also send `BepInEx\LogOutput.log` from the game folder (it has the game's own messages) and the newest `SeaOfStars.exe.*.dmp` from `%LOCALAPPDATA%\CrashDumps\` (Windows' crash dump of the game). Sea of Stars writes no `Player.log`.

What's in them: everything the mod logged (joins, drop-ins, desyncs, pauses, leaves, and `[coop]` lines saying who controls which character with which devices, written when that changes), the game's own errors, the mod build (the commit, e.g. `0.1.0+e3d3ca0`), the game version, Windows version and graphics card, and a session summary every minute (players, ping, input delay, sync checks). The state file has a snapshot of the game state every half second, so the host's and a guest's can be compared frame by frame. The logs contain the Steam names of the players in the session and Steam lobby ids (sometimes a player's Steam id); nothing else personal.

If the friend runs a different build than the host, the host gets a message when they join: use the same zip on both PCs.
