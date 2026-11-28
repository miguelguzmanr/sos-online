#!/usr/bin/env bash
# A battle that ends in a level up, two players: who chooses each character's bonus.
# Both PCs get the XP the party lacks for its next level (minus 1) on AWARD, a battle starts on ENCOUNTER and the bots
# win it (--sosonline-onehit: every enemy dies on its next hit). On the bonus choice the host's bot only confirms
# (the first star) and the guest's bot moves right once
# first, so the star chosen shows whose input the screen took. With Battle Turns on Custom (OWN=1, the default)
# every character's owner chooses; with OWN=0 the game's UI player chooses all of them.
# Pass: both PCs' [levelup] lines identical, each "chosen by" the owner (OWN=1), 0 guest mismatches.
# Usage: tests/level-up.sh [extra flags for both]   env: OWN AWARD ENCOUNTER QUIT DELAY LAG JITTER
cd "$(dirname "$0")/.."
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
SAVES="$LOCALAPPDATA/../LocalLow/Sabotage Studio/Sea of Stars"
S="$(cygpath -m "$PWD/tests/scripts")"
NET="--sosonline-lag=${LAG:-20} --sosonline-jitter=${JITTER:-10}"
COMMON="--sosonline-delay=${DELAY:-4} --sosonline-ownturns=${OWN:-1} --sosonline-awardxp=${AWARD:-1400} --sosonline-encounter=${ENCOUNTER:-1500} --sosonline-bot --sosonline-onehit --sosonline-quit=${QUIT:-9000} $NET $*"
rm -f "$O/log-27100.txt" "$O/log-27101.txt" "$O/state-27100.log" "$O/state-27101.log"
tests/session.sh 27100 "--sosonline-start=${START:-0} --sosonline-guests=1 --sosonline-seed=777 --sosonline-botphase=0 --sosonline-botlevelup=0 --sosonline-script=$S/host-walk.txt $COMMON" \
  "--sosonline-botphase=22 --sosonline-botlevelup=1 --sosonline-script=$S/guest-join-walk.txt $COMMON" 4
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
rm -rf "$SAVES"/SosOnlineTest-27* "$SAVES"/SosOnlineSession-27*

echo "== owners (host)"
grep -o "\[turns\] f[0-9]* P[0-9] owns .*" "$O/log-27100.txt" | tail -4
echo "== XP"
grep -o "\[test\] f[0-9]* awarded .*" "$O/log-27100.txt" "$O/log-27101.txt"
echo "== level ups (host | guest)"
paste -d'|' <(grep -o "\[levelup\] .*" "$O/log-27100.txt") <(grep -o "\[levelup\] .*" "$O/log-27101.txt") | cut -c1-240
echo "== differences"
# The frame each PC got the level-up scene differs (that's what the barrier is for): left out.
same() { grep -o "\[levelup\] .*" "$1" | grep -vE "the scene is here|: scene at "; }
diff <(same "$O/log-27100.txt") <(same "$O/log-27101.txt") | head -6
echo "== notices (host)"
grep -o "\[menu\] f[0-9]* .*Level Up.*" "$O/log-27100.txt" | head -4
echo "== guest sync"
grep -E "mismatch|resync\]|corrected" "$O/log-27101.txt" | head -5 | cut -c1-220
python3 - "$O/state-27100.log" "$O/state-27101.log" <<'PY'
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
print(f"guest: {len(common)} snapshots compared, {len(diff)} differ" + (f", first {diff[0]}" if diff else ""))
PY
echo "level ups chosen: $(grep -c "\[levelup\].*chosen by" "$O/log-27100.txt") host, $(grep -c "\[levelup\].*chosen by" "$O/log-27101.txt") guest; guest mismatches: $(grep -c mismatch "$O/log-27101.txt"); errors: $(grep -ciE "\[error|exception" "$O/log-27100.txt" "$O/log-27101.txt" | tr '\n' ' ')"
