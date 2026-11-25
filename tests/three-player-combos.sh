#!/usr/bin/env bash
# Three-player skills, combos, boosts, and timed hits (Valere, Zale, Serai) in Necromancer's Lair over a lagged link.
# Uses --sosonline-botvary so players cycle Skills/Combos and Live Mana Boosts in addition to timed attacks and blocks.
# Usage: tests/three-player-combos.sh [extra flags for all instances]
cd "$(dirname "$0")/.." || exit 1
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
SAVES="$LOCALAPPDATA/../LocalLow/Sabotage Studio/Sea of Stars"
S="$(cygpath -m "$PWD/tests/scripts")"
F1="$(cygpath -m "$PWD/tests/saves/midgame-necromancer/SOS_SaveSlot0.sos")"
F2="$(cygpath -m "$PWD/tests/saves/SOS_SaveDataIndex.sos")"
NET="--sosonline-lag=${LAG:-20} --sosonline-jitter=${JITTER:-25} --sosonline-spikes=${SPIKES:-3}"
COMMON="--sosonline-delay=${DELAY:-4} --sosonline-encounter=${ENCOUNTER:-1500} --sosonline-bot --sosonline-botvary --sosonline-quit=${QUIT:-4500} $NET $*"
rm -f "$O/log-27100.txt" "$O/log-27101.txt" "$O/log-27102.txt" "$O/state-27100.log" "$O/state-27101.log" "$O/state-27102.log"
bash tests/display-prefs.sh save > /dev/null 2>&1
tests/session.sh 27100 \
  "--sosonline-start=0 --sosonline-guests=2 --sosonline-seed=777 --sosonline-sandboxfile=$F1,$F2 --sosonline-botphase=0 --sosonline-botswitch=2 --sosonline-script=$S/host-walk.txt $COMMON" \
  "--sosonline-botphase=15 --sosonline-botswitch=3 --sosonline-script=$S/guest-join-walk.txt $COMMON" 4 \
  "--sosonline-botphase=30 --sosonline-botswitch=2 --sosonline-script=$S/guest-join-walk.txt $COMMON" 4
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
bash tests/display-prefs.sh restore > /dev/null 2>&1
rm -rf "$SAVES"/SosOnlineTest-27* "$SAVES"/SosOnlineSession-27*

echo "== bot skills / boosts & combat decisions"
grep -c "cycle move" "$O/log-27100.txt" | xargs printf "P1 cycle moves: %s; "
grep -c "boost" "$O/log-27100.txt" | xargs printf "boosts: %s\n"
diff <(grep -o "\[combat\] f[0-9]* .*" "$O/log-27100.txt") <(grep -o "\[combat\] f[0-9]* .*" "$O/log-27101.txt") | head -6
diff <(grep -o "\[combat\] f[0-9]* .*" "$O/log-27100.txt") <(grep -o "\[combat\] f[0-9]* .*" "$O/log-27102.txt") | head -6
echo "== snapshots"
rc=0
for p in 27101 27102; do
  python3 - "$O/state-27100.log" "$O/state-$p.log" "$p" "${QUIT:-4500}" <<'PY' || rc=1
import sys
def load(p, limit):
    d = {}
    for line in open(p, encoding='utf-8'):
        t = line.rstrip('\n').split(' ', 2)
        if len(t) > 2 and not t[2].startswith('loading') and int(t[1].lstrip('f')) < limit: d[t[1]] = t[2]
    return d
limit = int(sys.argv[4]) - 30
h, g = load(sys.argv[1], limit), load(sys.argv[2], limit)
common = [f for f in g if f in h]
diff = [f for f in common if h[f] != g[f]]
print(f"P{int(sys.argv[3]) - 27099} ({sys.argv[3]}): {len(common)} snapshots compared, {len(diff)} differ" + (f", first {diff[0]}" if diff else ""))
sys.exit(1 if diff or not common else 0)
PY
done
echo "battle frames: $(grep -c "combat=\[on" "$O/state-27100.log"); P2 mismatches: $(grep -c mismatch "$O/log-27101.txt"); P3 mismatches: $(grep -c mismatch "$O/log-27102.txt")"
exit $rc
