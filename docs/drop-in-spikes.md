# Drop-in spikes: instructions

Three short experiments that decide whether Stages 2 and 3 of the [drop-in plan](drop-in-plan.md) are possible. Each has a pass condition and a time box. Stop at the time box and record what blocked you.

| Spike | Question | Gates | Time box |
|---|---|---|---|
| A | Can a save be loaded from gameplay, without the title screen, under lockstep? | Stage 2 | 1 day |
| B | Does loading the area-entry autosave reproduce the live state at area entry? | Stage 3 | 2 days |
| C | How fast can the game replay recorded input? | Stage 3 | ½ day |

Run B only after the prerequisite below. Run C only if B passes.

## Setup

- Game build 3.0.60151, mod built and deployed (`tools\deploy.ps1`).
- `steam_appid.txt` containing `1244090` in the game folder.
- Wrap every run: `tests/display-prefs.sh save` before, `tests/display-prefs.sh restore` after.
- Outputs land in `<game>\BepInEx\SosOnline\`: `log-<port>.txt`, `state-<port>.log`, screenshots.
- Test instances play from sandbox copies of the saves. Delete `SosOnlineTest-*` and `SosOnlineSession-*` in the save folder afterwards.

### Finding game calls

The interop assemblies list every game class. To print a class's members:

```powershell
$g='W:\SteamLibrary\steamapps\common\Sea of Stars\BepInEx'
[AppDomain]::CurrentDomain.add_AssemblyResolve({param($s,$e) $n=($e.Name -split ',')[0]; foreach($d in "$g\interop","$g\core"){ $p=Join-Path $d "$n.dll"; if(Test-Path $p){ return [Reflection.Assembly]::LoadFrom($p) } }; $null})
$a=[Reflection.Assembly]::LoadFrom("$g\interop\Assembly-CSharp.dll")
try{$t=$a.GetTypes()}catch{$t=$_.Exception.InnerException.Types | ? {$_}}
($t | ? Name -eq 'SaveManager').GetMembers('Public,Instance,Static,DeclaredOnly') | % Name | sort -Unique
```

To see what a game method calls, add it to `Targets` in `SosOnline/XrefProbe.cs` and start the game with `--sosonline-xref`. The call list is written to the log and the game quits.

Calls confirmed to exist in this build (signatures only; behaviour is what the spikes test):

| Class | Members |
|---|---|
| `SaveManager` | `AutoSave()`, `LoadSaveSlot(SaveGameSlot)`, `LoadSaveSlotAndSetLastIndex(SaveGameSlot)`, `GetAutoSaveSlot(int)`, `CreateTemporarySaveSlot()`, `UnloadSaveSlotData()`, `saving`, `CanSave()` |
| `SaveGameSlot` | `checkpointData`, `lastUsedEntranceId`, `PartyData`, `InventorySaveData`, `currencySaveData`, `characterStatsSaveData` |
| `CheckpointData` | `level`, `position`, `playerOrientation`, `savepointId` |
| `LevelManager` | `LoadLevel(LevelLoading)`, `GetInitialLevelInfo()`, `UpdateCheckpointData(ESavepointId, Vector3, int)`, `LoadingLevel`, `onLevelLoaded`, `EndCurrentLevelLoading()` |
| `TitleScreen` | `OnContinue()`, `WaitAndContinueGame()` |
| `SinglePlayerPlusPlayer` | `Join()`, `Leave()`, `BeginPlaying()`, `playing` |
| `SinglePlayerPlusManager` | `RemoveAllActivePlayers()`, `AssignCharactersToPlayers(bool, bool)`, `SnapCharactersOnCharacter(...)` |

## Prerequisite — a state log that can see divergence

The current snapshot compares scenes and party positions only; its RNG probe always matches (see finding 1 in the plan). The spikes need more.

1. In `Lockstep/StateSync.cs`, extend `StateSnapshot` with separately logged components:
   - level id (`GameBridge.CurrentLevelId`),
   - per party member: HP, MP, position, facing,
   - gold and an inventory item count,
   - whether an encounter is running, and each combat actor's HP,
   - every enemy or NPC in the level that moves: name and position (sorted by name).
2. Remove `RngProbe`.
3. Make `ToString()` print one `key=value` group per component, so `diff` shows which one differs.
4. Add a harness flag `--sosonline-stateevery=N` (default 30) that sets the snapshot interval. Spike B needs `1`.

**Check.** Run `tests\two-instance.cmd` with the walk scripts. The two `state-*.log` files must still match. Then add a throwaway flag that changes one PC's party HP on a given frame, and confirm the next snapshot differs in the HP component only.

## Spike A — reload in place

**Question.** While lockstep runs, can every PC load an autosave on an agreed frame and resume, without the title screen?

### Steps

1. **Learn the load path.** Add to `XrefProbe.Targets` and run `--sosonline-xref`:
   - `TitleScreen.OnContinue`, `TitleScreen.WaitAndContinueGame`
   - `LevelManager.GetInitialLevelInfo`, `LevelManager.LoadLevel`
   - `SaveManager.LoadSaveSlot`, `SaveManager.UnloadSaveSlotData`

   Write down the sequence the title screen uses to go from a loaded save slot to a `LevelLoading`.
2. **Add `GameBridge.ReloadInPlace(int slotIndex)`.** Reproduce that sequence from gameplay: load the autosave slot, build the same `LevelLoading`, call `LevelManager.LoadLevel`. Keep `showTransition = true` so the game's own fade covers it.
3. **Add a harness flag `--sosonline-reload=F`** in `TestHarness.OnFrame`, on every PC:
   - frame F: `GameBridge.AutoSave()`. Each PC saves its own copy; guests write to their mirror folder.
   - frame F+120: log an error if `GameBridge.Saving` is still true, then call `ReloadInPlace`. Use a fixed frame, not "when saving finishes": completion time differs between PCs.
   - Lockstep keeps running throughout. Do not stop the engine.
4. **Re-join the guest.** After the load, the guest's co-op player is probably gone. Re-arm the auto-join (`Session._autoJoin`), or call `SinglePlayerPlusPlayer.Join()` on a fixed frame on every PC.
5. **Log** on every PC: the frame `LoadingLevel` becomes true, the frame it becomes false, and the frame the guest's `playing` becomes true.

### Run

```bash
tests/display-prefs.sh save
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-delay=3 --sosonline-script=tests/scripts/host-walk.txt --sosonline-reload=1300 --sosonline-shots=1290,1500,2300 --sosonline-quit=2400" "--sosonline-delay=3 --sosonline-script=tests/scripts/guest-join-walk.txt --sosonline-reload=1300 --sosonline-shots=1290,1500,2300 --sosonline-quit=2400"
```

Script paths are relative to the game folder; copy the scripts there or pass absolute paths. Frame 1300 is after the guest has joined (frame 1000) and walked. `--sosonline-reload` goes to **every** instance: each PC saves and reloads itself. Add `--sosonline-reloadpos` to both for the position-restore variant.

Compare:

```bash
diff <(cut -d' ' -f2- "BepInEx/SosOnline/state-27100.log") <(cut -d' ' -f2- "BepInEx/SosOnline/state-27101.log")
```

### Pass

- No title screen in any screenshot.
- State logs identical on both PCs from the first snapshot after the reload to the end.
- The guest controls a character again without manual input.
- From frame F+120 to the party being controllable: under 5 s (300 frames).

### Also record

- The gap, in frames, between the two PCs' "loading finished" frames. This sizes the loading barrier in Stage 0.
- Where the party reappears: the area entrance, the last save point, or where it stood.

### Then, if it passed — position restore

Before the autosave at frame F, try `LevelManager.UpdateCheckpointData(savepointId, position, orientation)` with the leader's current position, and check whether the reload spawns the party there. If not, record the party's positions at frame F and move the party to them on a fixed frame after the load, on every PC.

Pass: the party is within 0.1 m of its pre-reload positions and the logs still match.

### If it fails

Record which call failed or what state was left behind (stale co-op players, missing camera, input disabled). Stage 2 then falls back to the automated title-screen route.

## Spike B — does a save reproduce the live state?

**Question.** Take PC "A", which walked into an area. Take PC "B", which loaded the autosave A made on arriving there. Fed the same inputs from the moment each finished loading, do they stay identical?

If yes, a joiner can catch up by replay and nobody else has to reload.

### What to build

1. **Input recording.** Harness flag `--sosonline-record=FILE`. After each frame is applied, append the frame number and `InputOverride.Current` for the slot 0 and system channels (`InputSnapshot.Write`).
2. **Input replay.** Harness flag `--sosonline-replay=FILE` with `--sosonline-replayfrom=N`. In `TestHarness.Filter`, replace the local and system snapshots for target frame *f* with the recorded snapshots of frame *f − offset*, where the offset aligns the two "loading finished" frames (see below). Solo lockstep (`--sosonline-guests=0`) has only those two channels, so the filter is enough.
3. **Area-entry markers.** Log the first frame `LoadingLevel` is false after the level id changed (call it **L**), and the first later frame `GameBridge.CanSaveNow` is true (**C**).
4. **Autosave at C.** Harness flag `--sosonline-saveatentry`: on frame C call `GameBridge.AutoSave()`, and when saving ends copy the autosave file to `BepInEx\SosOnline\spike-b-autosave.sos`.

### Prepare

- A save standing a few steps from an area transition, with a visible patrolling enemy on the far side. Put it in slot 0.
- An input script that walks through the transition, **stands still until after frame C** (so nothing persistent changes between L and C), then walks around for about 50 s, including into the enemy to start a battle. Add `--sosonline-bot` to play the battle.

### Run A (records)

```bash
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=0 --sosonline-delay=3 --sosonline-stateevery=1 --sosonline-script=<walk script> --sosonline-bot --sosonline-saveatentry --sosonline-record=BepInEx/SosOnline/spike-b-inputs.bin --sosonline-quit=<L + 3600>"
```

Keep `state-27100.log` as `state-A.log`. Note **L_A** from the log.

### Run B (replays)

Copy `spike-b-autosave.sos` over the slot 0 autosave in a fresh sandbox, then:

```bash
tests/session.sh 27200 "--sosonline-start=0a --sosonline-guests=0 --sosonline-delay=3 --sosonline-stateevery=1 --sosonline-replay=BepInEx/SosOnline/spike-b-inputs.bin --sosonline-replayfrom=<L_A> --sosonline-quit=<L_B + 3600>"
```

**L_B** is B's first frame with `LoadingLevel` false after loading the save. The replay maps A's frame L_A + k to B's frame L_B + k.

The sandbox is rebuilt from your real saves at each launch (`SaveMirror.UseSandbox`), so for this run add a harness flag that copies a given file into the sandbox after it is created.

### Compare

Shift frame numbers so L_A and L_B are both 0, drop the hash column, and diff the two state logs.

### Pass

Every component identical from frame L+60 at the latest, for 3,000 frames, including the battle.

### Partial results and what they mean

| What differs | Meaning |
|---|---|
| Party position or facing in the first frames only | Patchable. Record the party's positions at L on A, apply them on B at L, and rerun. |
| Enemy or NPC positions | A arrived through a door and B spawned from a save; if their patrols started at different times, this is not patchable by a simple transfer. Try aligning on the frame the level's objects activate instead of `LoadingLevel`. If still different: fail. |
| HP, inventory, gold | The save was taken after something changed. Check the script stood still until C, then rerun. |
| Battle outcomes only | Timing-sensitive state outside the hash. Add `--sosonline-trace` and compare the combat timing lines. |

### If it fails

Stage 3 is dropped. Record the first differing component and frame. Stage 2 becomes the end state.

## Spike C — replay speed

Run only if B passed.

**Question.** How many simulated frames per second can a PC replay?

### Steps

1. Add `--sosonline-ff` to the replay mode: set `QualitySettings.vSyncCount = 0` and `Application.targetFrameRate = -1`, and leave `Time.captureDeltaTime` at 1/60. The simulation then advances one fixed step per rendered frame, as fast as the PC can go.
2. Mute audio while fast-forwarding.
3. Second variant: also disable the level cameras while fast-forwarding, and re-enable them at the end.
4. Log simulated frames per wall-clock second every 5 s, tagged with free roaming, battle, or loading.
5. Rerun spike B's run B with `--sosonline-ff` and confirm the state log still matches A's.

### Pass

- State log still identical to A's.
- At least 5× real time while roaming (300 simulated frames per second).

### Record

- Speed in each of the three situations, with and without cameras.
- The resulting rule for Stage 3: catch-up time = frames since area entry ÷ speed. Propose the cut-off above which the mod uses Stage 2 instead (starting point: 60 s).

## Reporting

For each spike, add a short section to this file:

- pass / partial / fail,
- the numbers asked for under *Record*,
- the log lines or diff that show it,
- anything the next stage must handle.

Keep the harness flags you added; later stages reuse them as regression tests.

## Results

### Prerequisite — state log (2026-11-15): pass

- Components logged: `lvl`, `scenes`, `party` (name/facing angle), `stats` (HP/MP per current party member), `gold` (currency amounts), `inv` (kinds/total), `combat` (`off`, or each actor's HP), `npcs` (every active `NPCController`, name and position to 0.1 m), `pos` (party positions, compared with the existing 2 cm drift correction). `RngProbe` is gone.
- Walk scripts, two instances: the 61 snapshots up to frame 1800 match. `--sosonline-hpdiverge=1800` on the guest (ZALE 12 → 11) is reported on the next snapshot, with that component only: `frame 1830 mismatch #1: stats differs (local [ZALE:11/3,VALERE:11/2,GARL:15/2])`.
- Cost: 3.4 ms per snapshot on average, 25.6 ms at worst (81 snapshots). Mostly `FindObjectsOfType<NPCController>`. At the default interval that is 0.1 ms per frame; Stage 0's rotating subset is not needed yet.
- `gold` is empty in the test save (the early-game party owns no currency yet). Spike B should use a save that has some.

### Spike A — reload in place (2026-11-15): pass

How: `GameBridge.ReloadInPlace` calls `SaveManager.LoadSaveSlotAndSetLastIndex(slot)` (its `onSaveSlotLoaded` restores the managers), then `LevelManager.LoadLevel` with `showTransition = true`, `removeTransitionWhenDone = true` and a `GameplayLevelInitializerParams` built from the slot's `checkpointData`. Lockstep keeps running; nothing goes through the title screen.

| Check | Result |
|---|---|
| Title screen in screenshots | None. The game's fade covers the load (frame 1500 mid-fade, 1600 fully visible). |
| State logs after the reload | Identical on both PCs: 31 of 31 snapshots, in each of four runs. |
| Guest controls a character again | Yes, with no new Join press. The co-op player survives the reload (`P2 playing` never goes false); the re-armed auto-join finds it already playing. |
| F+120 to party controllable | 76–84 frames of loading (1.3–1.4 s), then the fade. The guest's scripted walk at frame 1500 moves its character. Well under 300 frames. |
| Loading-finished gap between PCs | Without the barrier: 0 frames for the in-place reloads (1497/1497, 1499/1499, 1496/1496); 1–3 frames for the title-screen load at session start (333/335, 331/330, 327/327). One such gap produced `frame 330 mismatch: npcs differs`. With Stage 0's barrier, both PCs end every load on the same frame (333/333, 1504/1504); the hold costs 8–9 frames on loopback. |
| Where the party reappears | At `checkpointData.position`, the point where the party entered the area (here (9.08, 1.00, 13.83)), not where it stood. Without initializer params the party lands at a level default ((14.67, −1.00, 67.28)) and the screen stays black. |

Position restore: `UpdateCheckpointData` is not needed. `GameplayLevelInitializerParams.spawnPosition` (with `spawnPositionSet = true`) and `playerOrientation` place the leader exactly: 0.00 m from where it stood at the reload frame. The game re-forms the followers around the leader, so `--sosonline-reloadpos` also moves every party member back to its own position on fixed frame F+240. After that, the leader and P2 are within 0.01 m, and Garl (AI follower) is 0.47 m from where the game placed him before resuming his follow. The logs still match.

For Stage 2:
- Take the positions on the reload frame, not the save frame; the party keeps moving in between (2.5 m in this test).
- The position fix-up frame has to come after the load ends. With the barrier, the release frame is known on every PC, so use release + a few frames instead of a fixed offset.
- Inputs a player makes between the reload and the fix-up are undone for their character. Either block movement until the fix-up, or apply it on the release frame.
- `--sosonline-reload` and `--sosonline-reloadpos` remain as regression tests.

