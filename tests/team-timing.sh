#!/usr/bin/env bash
# Team Timing: whose presses count for timed hits and blocks. A battle with two players: the host's bot only confirms
# menus and never presses for a hit or block; the guest's bot presses for every party member's hits and blocks
# (--sosonline-botall), as a friend helping out would. With MODE=original the guest's presses count on the host's
# character too; with hitsonly they count for hits but not for the host's character's blocks; with none they count
# only on the guest's own turns and character. Prints both PCs' Team Timing lines and the verdicts the guest's presses
# got, and checks that both PCs agree.
# Usage: MODE=none tests/team-timing.sh [extra flags for both]   env: MODE LAG JITTER QUIT ENCOUNTER DELAY BLOCK MANUAL
# MANUAL=1 (with VISIBLE=1): you play the host's window yourself (keyboard or pad: menus, hits, blocks). The friend's
# window only confirms its battle menus (its turns go on) and never presses for a timed hit or block; it ignores your
# keyboard and pads. It runs until you close the windows.
cd "$(dirname "$0")/.."
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
S="$(cygpath -m "$PWD/tests/scripts")"
MODE="${MODE:-none}"
NET="--sosonline-lag=${LAG:-20} --sosonline-jitter=${JITTER:-10}"
if [ -n "$MANUAL" ]; then
  QUIT="${QUIT:-216000}"   # one hour
  HOSTBOT=""
  GUESTBOT="--sosonline-bot=menu --sosonline-botphase=22 --sosonline-background"
else
  HOSTBOT="--sosonline-bot=menu --sosonline-botphase=0 --sosonline-script=$S/host-walk.txt"
  GUESTBOT="--sosonline-bot --sosonline-botall --sosonline-botblock=${BLOCK:-86} --sosonline-botphase=22 --sosonline-script=$S/guest-join-walk.txt"
fi
COMMON="--sosonline-delay=${DELAY:-4} --sosonline-encounter=${ENCOUNTER:-1500} --sosonline-quit=${QUIT:-7000} --sosonline-ownturns=1 $NET $*"
rm -f "$O/log-27100.txt" "$O/log-27101.txt" "$O/state-27100.log" "$O/state-27101.log"
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-teamtiming=$MODE $HOSTBOT $COMMON" \
  "$GUESTBOT $COMMON" 4
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done

echo "== mode $MODE: rule and moves (host)"
grep -o "\[teamtiming\] .*" "$O/log-27100.txt" | head -20
echo "== turns (host)"
grep -o "\[turns\] f[0-9]* turn of .*" "$O/log-27100.txt" | head -12
echo "== presses that didn't count (host | guest)"
paste -d'|' <(grep -o "\[timing\] [a-z]* f[0-9]*: P[0-9]'s .*" "$O/log-27100.txt" | head -20) <(grep -o "\[timing\] [a-z]* f[0-9]*: P[0-9]'s .*" "$O/log-27101.txt" | head -20) | cut -c1-220
echo "== verdicts (host), first 30"
grep -o "\[timing\] \(attack\|block\) .*" "$O/log-27100.txt" | head -30 | cut -c1-200
echo "== timing verdict differences host vs guest"
diff <(grep -o "\[timing\] .*" "$O/log-27100.txt") <(grep -o "\[timing\] .*" "$O/log-27101.txt") | head -6
echo "== guest sync"
grep -E "mismatch|resync\]" "$O/log-27101.txt" | head -5 | cut -c1-220
echo "mode $MODE: host snapshots in battle $(grep -c "combat=\[on" "$O/state-27100.log"); guest mismatches $(grep -c mismatch "$O/log-27101.txt"); guest presses $(grep -c "\[bot\] \(impact\|block\)" "$O/log-27101.txt"); not counted host/guest $(grep -c "doesn't count" "$O/log-27100.txt")/$(grep -c "doesn't count" "$O/log-27101.txt")"
