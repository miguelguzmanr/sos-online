#!/usr/bin/env bash
# Three-player fishing minigame test in Sunglow Lake (endgame-worldmap -> FishingLake).
# Loads FishingLake on frame 450, starts fishing on frame 900, and casts/reels with synced input across 3 PCs.
# Usage: tests/three-player-fishing.sh [extra flags for all instances]
cd "$(dirname "$0")/.." || exit 1
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
SAVES="$LOCALAPPDATA/../LocalLow/Sabotage Studio/Sea of Stars"
S="$(cygpath -m "$PWD/tests/scripts")"
F1="$(cygpath -m "$PWD/tests/saves/endgame-worldmap/SOS_SaveSlot0.sos")"
F2="$(cygpath -m "$PWD/tests/saves/SOS_SaveDataIndex.sos")"
GEN="$(cygpath -m "$(mktemp -d)")"

cat > "$GEN/fish.txt" <<'EOF'
1100 Interact 20
1160 Interact 45
1260 MoveHorizontal 30 -1
1320 Interact 60
1420 MoveHorizontal 30 1
1480 Interact 90
1620 Interact 60
1720 MoveHorizontal 25 -1
1780 Interact 80
1920 Interact 50
2020 Interact 40
2150 Interact 60
2300 Interact 50
EOF

COMMON="--sosonline-delay=4 --sosonline-level=450:268236710c948574bb314f93530600d2 --sosonline-fish=900 --sosonline-quit=${QUIT:-2600} $*"
rm -f "$O/log-27100.txt" "$O/log-27101.txt" "$O/log-27102.txt" "$O/state-27100.log" "$O/state-27101.log" "$O/state-27102.log"
bash tests/display-prefs.sh save > /dev/null 2>&1
tests/session.sh 27100 \
  "--sosonline-start=0 --sosonline-guests=2 --sosonline-seed=777 --sosonline-sandboxfile=$F1,$F2 --sosonline-script=$GEN/fish.txt $COMMON" \
  "--sosonline-script=$S/guest-join-walk.txt $COMMON" 4 \
  "--sosonline-script=$S/guest-join-walk.txt $COMMON" 4
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
bash tests/display-prefs.sh restore > /dev/null 2>&1
rm -rf "$SAVES"/SosOnlineTest-27* "$SAVES"/SosOnlineSession-27* "$GEN"

echo "== fishing logs"
for p in 27100 27101 27102; do
  echo "--- $p"
  grep -h -E "\[test\] f[0-9]+ (loading level|started fishing)|FishingScreen|Fisherman|mismatch|resync\]" "$O/log-$p.txt" | head -20
done
echo "== snapshots"
rc=0
for p in 27101 27102; do
  python3 - "$O/state-27100.log" "$O/state-$p.log" "$p" <<'PY' || rc=1
import re, sys
def load(p):
    d = {}
    for line in open(p, encoding='utf-8'):
        t = line.rstrip('\n').split(' ', 2)
        if len(t) > 2 and not t[2].startswith('loading') and 'lvl=[268236710c948574bb314f93530600d2]' in t[2]:
            d[t[1]] = re.sub(r'/\d+(?=[ \]])', '', t[2])
    return d
h, g = load(sys.argv[1]), load(sys.argv[2])
common = [f for f in g if f in h]
diff = [f for f in common if h[f] != g[f]]
print(f"P{int(sys.argv[3]) - 27099} ({sys.argv[3]}): {len(common)} snapshots compared, {len(diff)} differ" + (f", first {diff[0]}" if diff else ""))
sys.exit(1 if diff or not common else 0)
PY
done
exit $rc
