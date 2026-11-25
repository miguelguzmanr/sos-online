#!/usr/bin/env bash
# Three-player endgame test (6 unlocked characters, 185 items, 25 moves, 6 relics) starting from endgame-worldmap.
# Host dismisses Hortence's Watchmaker intro to board the Vespertine, P2 and P3 join, and host exercises
# the 6-character Game Menu (Equip / Skills / Items / Order / Relics).
# Usage: tests/three-player-endgame.sh [extra flags]
cd "$(dirname "$0")/.." || exit 1
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
SAVES="$LOCALAPPDATA/../LocalLow/Sabotage Studio/Sea of Stars"
F1="$(cygpath -m "$PWD/tests/saves/endgame-worldmap/SOS_SaveSlot0.sos")"
F2="$(cygpath -m "$PWD/tests/saves/SOS_SaveDataIndex.sos")"
GEN="$(cygpath -m "$(mktemp -d)")"

cat > "$GEN/host.txt" <<'EOF'
950 UI_Confirm 4
995 UI_Confirm 4
1040 UI_Confirm 4
1085 UI_Confirm 4
1130 UI_Confirm 4
1175 UI_Confirm 4
1220 UI_Confirm 4
1265 UI_Confirm 4
1310 UI_Confirm 4
1355 UI_Confirm 4
1400 UI_Confirm 4
1445 UI_Confirm 4
1560 Player_GameMenu 4
1640 UIMoveVertical 6 -1
1700 UIMoveVertical 6 -1
1760 UI_Confirm 4
1840 UI_NextPage 4
1900 UI_Back 4
1960 UIMoveVertical 6 -1
2020 UI_Confirm 4
2100 UI_Back 4
2160 UIMoveVertical 6 -1
2220 UI_Confirm 4
2300 UI_CloseGameMenu 4
EOF

cat > "$GEN/p2.txt" <<'EOF'
1520 Join 6
2360 MoveHorizontal 40 1
EOF

cat > "$GEN/p3.txt" <<'EOF'
1520 Join 6
2360 MoveHorizontal 40 -1
EOF

COMMON="--sosonline-delay=3 --sosonline-shots=1780,2040,2240 --sosonline-quit=${QUIT:-2500} $*"
rm -f "$O/log-27100.txt" "$O/log-27101.txt" "$O/log-27102.txt" "$O/state-27100.log" "$O/state-27101.log" "$O/state-27102.log"
bash tests/display-prefs.sh save > /dev/null 2>&1
tests/session.sh 27100 \
  "--sosonline-start=0 --sosonline-guests=2 --sosonline-seed=777 --sosonline-sandboxfile=$F1,$F2 --sosonline-script=$GEN/host.txt $COMMON" \
  "--sosonline-script=$GEN/p2.txt $COMMON" 4 \
  "--sosonline-script=$GEN/p3.txt $COMMON" 4
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
bash tests/display-prefs.sh restore > /dev/null 2>&1
rm -rf "$SAVES"/SosOnlineTest-27* "$SAVES"/SosOnlineSession-27* "$GEN"

echo "== menu & sync logs"
for p in 27100 27101 27102; do
  echo "--- $p"
  grep -h -E "\[menu\]|\[test\] f[0-9]+ playing:|mismatch|resync\]" "$O/log-$p.txt" | head -20
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
