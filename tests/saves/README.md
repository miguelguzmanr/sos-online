# Test Save Fixtures (3+ Players)

Standalone `.sos` saves for running sandboxed tests via `--sosonline-sandboxfile` without touching your real save folder (`%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars`).

## Available saves

| Folder | Level / XP | Playtime | Party (`combatParty`) | Inventory / Moves | Location |
|---|---|---|---|---|---|
| `endgame-worldmap/` | Lv 21 (`19470` XP) | ~48.3 h | `VALERE`, `ZALE`, `GARL` (`SERAI`, `RESHAN`, `BST` unlocked) | 185 items, 25 moves, 6 relics, 11 quiz packs, 37 songs | World Map (near Torment Peak / Vespertine / Khukharr) |
| `midgame-necromancer/` | Lv 8 (`2351` XP) | ~12.8 h | `VALERE`, `ZALE`, `SERAI` | 80 items, 5 moves | Necromancer's Lair (save book & campfire nearby) |

Both saves originated as `saveVersion: 1` community saves (`r/seaofstars`) and load cleanly on Sunset Edition (`v3.0.60151`) when paired with `SOS_SaveDataIndex.sos` (verified in 3-player UDP sandboxed runs).

## Scripts using these fixtures

- `tests/three-player-battle.sh`: 3-player Necromancer's Lair battle over a lagged link (`midgame-necromancer`)
- `tests/three-player-combos.sh`: 3-player battle with `--sosonline-botvary` cycling skills, combos, and boosts (`midgame-necromancer`)
- `tests/three-player-menus.sh`: 3-player menus, Assign Players, and camping (`midgame-necromancer`)
- `tests/three-player-fishing.sh`: 3-player Sunglow Lake fishing (`endgame-worldmap` → `FishingLake`)
- `tests/three-player-endgame.sh`: 3-player endgame World Map + 6-character menu (`endgame-worldmap`)
- `tests/rejoin-paused.sh`: Stage 2 drop-in rejoin regression when guest is paused / in menus (`midgame-necromancer`)

## Usage

Pass both the slot file and `tests/saves/SOS_SaveDataIndex.sos` to `--sosonline-sandboxfile` on the host (guests mirror saves from the host):

```bash
# Example: 3-player solo/UDP test from endgame-worldmap
./SeaOfStars.exe -screen-fullscreen 0 -screen-width 640 -screen-height 360 \
  --sosonline-udp=27100 --sosonline-host --sosonline-guests=2 \
  --sosonline-start=0 \
  --sosonline-sandboxfile="$(cygpath -w tests/saves/endgame-worldmap/SOS_SaveSlot0.sos),$(cygpath -w tests/saves/SOS_SaveDataIndex.sos)" \
  --sosonline-quit=1600
```
