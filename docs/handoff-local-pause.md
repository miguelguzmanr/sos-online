# Handoff: local pause screen (tier 1) and the guest Options bug

**Status: done.** See [drop-in-findings.md](drop-in-findings.md) §8 for what was built, the choices made (the host's pause is local too; battles, cutscenes and loading keep the shared pause; guests can't open Options from a shared pause) and the test results. The rest of this file is the original brief.

Read first: [drop-in-findings.md](drop-in-findings.md) §7 (pause and menus online, the banner, the Options bug), then *Independent pause and menus* in [drop-in-plan.md](drop-in-plan.md). Branch `docs/drop-in-plan`, commit `ceb89c7`. Everything so far runs on loopback only.

## Goal

A player who pauses gets a pause screen on their own PC only. The world keeps running for everyone, them included, and nobody else's game stops or shows a menu. Their character gets neutral input (or follows the party, as when a co-op player is idle) until they resume.

The screen has:
- **Resume.**
- **Options:** audio, video and controls, applied on this PC only.
- **Online:** Leave session, plus End session for the host. The existing entries in `UI/NativeMenus.cs` already run as deterministic commands.

This also fixes the guest Options bug, because Options no longer opens inside the shared game.

The main menu (`Player_GameMenu`: party, inventory, relics) stays shared for now (tiers 2 and 3). The banner (`UI/MenuStatus.cs`) keeps reporting it.

## The bug this fixes

**What happens.**
- A guest pauses and opens Options. The game builds that player's controls page from the controllers assigned to that player on each PC.
- On the guest's PC that is its keyboard. On the host and the other guests, that player has no controllers, so the page is empty and ignores the guest's Back.
- Those PCs stay in Options for good; the guest's own PC closes it. The game state stayed identical in the test, but only the UI had diverged so far.
- The host doing the same is in sync.

**Reproduce:** `tests/menu-status.sh` (default scripts).
- *Wrong today:* the host's and the joiner's `[menu]` lines stop at `Pause menu › Options`, while guest 1's go on to `closed` and `P1 paused: Menu`.
- *Screenshots:* `shot-27100-002420.png` (empty Options panel) against `shot-27101-002420.png` (the guest's keyboard bindings).

## How a pause works now (facts you need)

**Inputs.**
- Any player's `UI_Start` opens the pause menu (`PauseManager.TogglePauseMenu(Player)`). `Player_GameMenu` opens the main menu.
- Both come through the synced input: every PC sees the press on the same frame and runs the same thing.
- The game then sets `SinglePlayerPlusManager.uiInputsPlayer` to the presser, and only that player navigates.

**Input path.**
- Local input is captured in `LockstepEngine.Tick` → `InputCapture.Capture` (which reads hardware with `InputOverride.Bypass`), then passed through `Engine.LocalInputFilter` (set in `Session.BeginLockstep`: `AutoJoin`, then the harness filter), then sent.
- That filter is where a local pause can strip this player's presses. `ActionTable.IndexOf("UI_Start")` and `ActionBits` give the bits; `AutoJoin` shows how to edit a snapshot.
- The host also sends the System player channel (menus outside gameplay). Check whether the host's pause press comes through slot 0, the system channel, or both, and strip both.

**Menus from the mod.**
- `NativeMenus.Popup` uses the game's `GenericPopupScreen` through `UIManager.InstantiateViewAsync`. That is a game view: opening it on one PC only changes that PC's UI state, the view stack (`screensByType`) and possibly the pause state.
- So do not use it for the local screen. Build the local screen on the mod's own canvas with `UI/GameUi.cs` (the pause menu's look, already measured). `UI/CatchUpScreen.cs` is a worked example.

**Options.**
- `OptionsScreen` (tabs: general, audio, video, gamepad controls, keyboard controls) is also a game view with its own state machine (`pausedState`, `optionState`). Opening it on one PC is the same problem.
- Options to weigh, cheapest first:
  1. A mod-built options page for the per-PC settings: volumes (Wwise RTPCs or the game's own setters), display mode and resolution, brightness. Look for the setters behind `OptionsScreen.masterVolumeBtn` etc. and `OptionsSaveData`.
  2. Find whether the game's `OptionsScreen` can run without pausing or touching shared state. Unlikely, so check with `--sosonline-tracecalls=OptionsScreen,PauseManager`.
- Settings that may affect the simulation must stay out of a local page, or be synced as a command. Check each one, for example "Timed Hit HUD feedback" (`singlePlayerTimedHitUIBtn`) and language.
- Settings are written to `SOS_Options.sos`. The session mirrors the host's files to guests (`SaveMirror`), so check whether options are part of that mirror and whether a local change is overwritten or leaks into the session copy.

**Banner.** `MenuStatus` reads only the shared game's state, so a local pause won't appear there. To let others see "P2 is in their pause screen", send a small status message (a new `Msg`; the protocol is version 6). Keep it display-only, never input.

## Suggested design

1. **Local pause state** (`UI/LocalPause.cs`). While open:
   - The local slot's outgoing snapshot is neutral (strip everything, or keep only what must pass, such as `Leave` if that ever matters).
   - Pause and game-menu presses never reach the synced input.
   - Hardware input drives the local screen instead (read with `InputOverride.Bypass` like `InputCapture`, or from the captured snapshot before it is neutralised).
2. **Opening it:** a local `UI_Start` press (the one captured for this PC's slot) opens the local screen instead of being sent.
   - Decide whether the host's pause is local too. Probably yes, for symmetry.
   - Keep the host's ability to pause the shared game where the game itself needs it (cutscenes and dialogues use the pause menu for skip?). Check what `UI_Start` does in a cutscene or dialogue before stripping it there; possibly only intercept during free gameplay (no battle, cutscene or loading).
3. **Resume:** close the screen; input flows again from the next captured frame. Mind the press that closed it: don't forward that button's held or released state as a new press.
4. **Online entries** call the existing deterministic paths: `Session.LeaveSlotDeterministic` must go through synced input or a deterministic schedule, as today. Read how `NativeMenus` triggers it and keep that path.
5. **Options:** one of the paths above.
6. **Battles:** decide whether a local pause during a battle is allowed. During your turn nobody can act for you, so the battle would wait on your input anyway. Simplest: allow it; the battle just waits.

## Done when

- `tests/menu-status.sh` with guest 1 pausing (`guest-pause.txt`):
  - The host's and the joiner's logs show no `[menu]` pause lines.
  - Guest 1's log shows its local screen opening and closing (add `[localpause]` log lines).
  - All three PCs' state snapshots are identical (0 differ).
  - The screenshots show the world still moving on the host while guest 1's screen shows the local pause.
- The same with the host pausing.
- Guest 1 changing a volume or display setting in the local Options changes only guest 1's PC and stays after a session restart.
- `tests/dropin.sh` and `tests/dropin-two.sh` still pass (a joiner catching up replays nothing of a local pause, because a local pause sends only neutral input).

## Testing notes

- **Harness actions on a frame** (`--sosonline-encounter`, `--sosonline-reload`): give them to the joiner too, because it replays those frames.
- **Harness scripts inject input** through `LocalInputFilter` → the synced stream. A scripted `UI_Start` in `guest-pause.txt` must reach the local pause logic before it is neutralised, so make sure the local-pause hook runs after the script filter, or open the local screen from the script's press.
- **Builds:** don't build while a test game runs (the build copies the DLL into the game folder).
- **Display prefs:** run `tests/display-prefs.sh save` before a test session and `restore` after.
- **Cleanup:** delete the `SosOnlineTest-*` and `SosOnlineSession-<port>` folders in the save folder afterwards (`%USERPROFILE%/AppData/LocalLow/Sabotage Studio/Sea of Stars`). Leave `SosOnlineSession` and `SosOnlineBackup` alone.
- **Decompiling:** `ilspycmd -t <Class> "<game>/BepInEx/interop/Assembly-CSharp.dll"` decompiles a game class (signatures only; IL2CPP has no method bodies). Use `--sosonline-tracecalls` to see what actually runs.
