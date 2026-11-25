#!/usr/bin/env bash
# Regression test for the rejoin loop: guest pauses locally (opening PauseMenu) and diverges on HP before Stage 2
# drop-in reloads in place. Before the fix, PauseManager.isPaused stayed true across ReloadInPlace, causing an
# immediate 'pause differs (local [on])' mismatch loop. Compares snapshots after rejoin.
cd "$(dirname "$0")/.." || exit 1
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
SAVES="$LOCALAPPDATA/../LocalLow/Sabotage Studio/Sea of Stars"
S="$(cygpath -m "$PWD/tests/scripts")"
F1="$(cygpath -m "$PWD/tests/saves/midgame-necromancer/SOS_SaveSlot0.sos")"
F2="$(cygpath -m "$PWD/tests/saves/SOS_SaveDataIndex.sos")"
COMMON="--sosonline-delay=3 --sosonline-quit=${QUIT:-3600} $*"
rm -f "$O/log-27100.txt" "$O/log-27101.txt" "$O/state-27100.log" "$O/state-27101.log"
bash tests/display-prefs.sh save > /dev/null 2>&1
tests/session.sh 27100 \
  "--sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-sandboxfile=$F1,$F2 --sosonline-script=$S/host-walk.txt $COMMON" \
  "--sosonline-script=$S/guest-join-walk.txt --sosonline-nocatchup --sosonline-localpause=1480 --sosonline-hpdiverge=1500 $COMMON" 4
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
bash tests/display-prefs.sh restore > /dev/null 2>&1
rm -rf "$SAVES"/SosOnlineTest-27* "$SAVES"/SosOnlineSession-27*

for p in 27100 27101; do
  echo "--- $p"
  grep -h -E "\[resync\]|localpause|hpdiverge|cleared local|mismatch|\[dropin\] .*(join at|joining at)|stopped at frame" "$O/log-$p.txt" | cut -c1-170
done
python3 - "$O" <<'PY'
import re, sys
O = sys.argv[1]
def load(p):
    d = {}
    for line in open(p, encoding='utf-8'):
        t = line.rstrip('\n').split(' ', 2)
        if len(t) > 2 and not t[2].startswith('loading'): d[int(t[1].lstrip('f'))] = t[2]
    return d
h, g = load(f"{O}/state-27100.log"), load(f"{O}/state-27101.log")
joins = [int(m.group(1)) for l in open(f"{O}/log-27101.txt", encoding='utf-8') for m in [re.search(r'\[(?:dropin\] joining|catchup\] merging) at f(\d+)', l)] if m]
if not joins:
    print("guest never dropped in again"); sys.exit(1)
join = max(joins)
common = [f for f in g if f in h and f > join + 120]
diff = [f for f in common if h[f] != g[f]]
print(f"after paused rejoin (join f{join}): {len(common)} snapshots compared, {len(diff)} differ" + (f", first f{diff[0]}" if diff else ""))
sys.exit(1 if diff or not common else 0)
PY
