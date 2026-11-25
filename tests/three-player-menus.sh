#!/usr/bin/env bash
# Three-player menus, items/relics browsing, Assign Players, and camping in Necromancer's Lair.
# Host (P1) and two guests (P2, P3) join, host opens the Game Menu across tabs (Characters/Inventory/Relics),
# all three players test 3-player Assign Players, and all three camp at the nearby campfire.
# Usage: tests/three-player-menus.sh [extra flags]
cd "$(dirname "$0")/.." || exit 1
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
SAVES="$LOCALAPPDATA/../LocalLow/Sabotage Studio/Sea of Stars"
F1="$(cygpath -m "$PWD/tests/saves/midgame-necromancer/SOS_SaveSlot0.sos")"
F2="$(cygpath -m "$PWD/tests/saves/SOS_SaveDataIndex.sos")"
GEN="$(cygpath -m "$(mktemp -d)")"

cat > "$GEN/host.txt" <<'EOF'
700 MoveVertical 40 -1
1300 Player_GameMenu 4
1360 UI_Confirm 4
1420 UI_Confirm 4
1480 UI_Confirm 4
1540 UIMoveVertical 6 -1
1600 UI_Confirm 4
1660 UI_Back 4
1740 UIMoveVertical 6 -1
1800 UIMoveVertical 6 -1
1860 UI_Confirm 4
1940 UI_NextPage 4
2000 UI_Back 4
2060 UIMoveVertical 6 -1
2120 UIMoveVertical 6 -1
2180 UI_Confirm 4
2240 UI_Confirm 4
2300 UI_CloseGameMenu 4
2610 UIAssignConfirm 4
2670 UIAssignConfirm 4
3720 Interact 4
3810 UI_Confirm 30
3930 UI_Confirm 120
EOF

cat > "$GEN/p2.txt" <<'EOF'
1000 Join 6
1150 MoveHorizontal 40 1
2510 UIAssignMoveHorizontal 10 -1
2560 UIAssignConfirm 4
3150 MoveHorizontal 30 1
EOF

cat > "$GEN/p3.txt" <<'EOF'
1000 Join 6
1150 MoveHorizontal 40 -1
2510 UIAssignMoveHorizontal 10 1
2560 UIAssignConfirm 4
3150 MoveHorizontal 30 -1
EOF

COMMON="--sosonline-delay=3 --sosonline-assigntest=2450 --sosonline-campstart=2850 --sosonline-campstop=3650 --sosonline-shots=1980,2600,3250 --sosonline-quit=${QUIT:-4250} $*"
rm -f "$O/log-27100.txt" "$O/log-27101.txt" "$O/log-27102.txt" "$O/state-27100.log" "$O/state-27101.log" "$O/state-27102.log"
bash tests/display-prefs.sh save > /dev/null 2>&1
tests/session.sh 27100 \
  "--sosonline-start=0 --sosonline-guests=2 --sosonline-seed=777 --sosonline-sandboxfile=$F1,$F2 --sosonline-script=$GEN/host.txt $COMMON" \
  "--sosonline-script=$GEN/p2.txt $COMMON" 4 \
  "--sosonline-script=$GEN/p3.txt $COMMON" 4
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
bash tests/display-prefs.sh restore > /dev/null 2>&1
rm -rf "$SAVES"/SosOnlineTest-27* "$SAVES"/SosOnlineSession-27* "$GEN"

echo "== menu & assign logs"
for p in 27100 27101 27102; do
  echo "--- $p"
  grep -h -E "\[menu\]|\[coop\].*Assign|camping|mismatch" "$O/log-$p.txt" | head -20
done
echo "== snapshots"
for p in 27101 27102; do
  python3 - "$O/state-27100.log" "$O/state-$p.log" "$p" <<'PY'
import sys
def load(p):
    d = {}
    for line in open(p, encoding='utf-8'):
        t = line.rstrip('\n').split(' ', 2)
        if len(t) > 2 and not t[2].startswith('loading'): d[t[1]] = t[2]
    return d
h, g = load(sys.argv[1]), load(sys.argv[2])
common = [f for f in g if f in h]
diff = [f for f in common if h[f] != g[f]]
print(f"P{int(sys.argv[3]) - 27099} ({sys.argv[3]}): {len(common)} snapshots compared, {len(diff)} differ" + (f", first {diff[0]}" if diff else ""))
PY
done
