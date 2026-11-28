#!/usr/bin/env bash
# A level loaded during play: the golem's throw on Evermist Island loads EvermistYeet over the world map (findings §23).
# Host and guest start from SAVE (an autosave on the Evermist Island world map next to the golem that throws the party),
# the guest takes a party member, and at YEET (default 900) both start the throw (--sosonline-yeet). Prints the
# additive barrier's steps and compares every snapshot. DIVERGE=F also makes the guest diverge on frame F, so it
# rejoins by replaying the area across the throw. Pass --sosonline-noadditive in EXTRA to turn the barrier off.
# Usage: SAVE=path/to/Autosave_SOS_SaveSlot0.sos tests/yeet.sh
cd "$(dirname "$0")/.." || exit 1
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
SAVES="$LOCALAPPDATA/../LocalLow/Sabotage Studio/Sea of Stars"
[ -f "$SAVE" ] || { echo "SAVE must name an autosave next to the Evermist Island golem" >&2; exit 1; }
F1="$(cygpath -m "$SAVE")"
INDEX="$(dirname "$SAVE")/SOS_SaveDataIndex.sos"; [ -f "$INDEX" ] || INDEX="$PWD/tests/saves/SOS_SaveDataIndex.sos"
F2="$(cygpath -m "$INDEX")"
SLOT=$(basename "$SAVE" | sed -E 's/.*SaveSlot([0-9]+).*/\1/')
case "$(basename "$SAVE")" in Autosave_*) SLOT="${SLOT}a";; esac
YEET=${YEET:-900}
COMMON="--sosonline-delay=${DELAY:-4} --sosonline-lag=${LAG:-20} --sosonline-jitter=${JITTER:-25} --sosonline-yeet=$YEET --sosonline-quit=${QUIT:-2400} $EXTRA"
GUEST=""; [ -n "$DIVERGE" ] && GUEST="--sosonline-hpdiverge=$DIVERGE"
rm -f "$O/log-27100.txt" "$O/log-27101.txt" "$O/state-27100.log" "$O/state-27101.log"
tests/session.sh 27100 \
  "--sosonline-start=$SLOT --sosonline-guests=1 --sosonline-seed=777 --sosonline-sandboxfile=$F1,$F2 $COMMON" \
  "$GUEST $COMMON" 4
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
rm -rf "$SAVES"/SosOnlineTest-27* "$SAVES"/SosOnlineSession-27*

for p in 27100 27101; do
  echo "--- $p"
  grep -h -E "\[test\] f[0-9]+ yeet|\[additive\]|mismatch|\[resync\]|\[catchup\] (start|caught up|.*diverged|replaying loading)|\[barrier\] load .* release" "$O/log-$p.txt" | cut -c14-220 | head -60
done
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
