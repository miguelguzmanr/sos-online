#!/usr/bin/env bash
# A guest's connection drops for a while (--sosonline-netdrop=F:S) in the middle of co-op. The host drops it after
# 2 s; the guest notices the silent host, reconnects on its own and rejoins (Rejoin: Auto, a reload at the next save
# chance with --sosonline-dropinanywhere). Compares the guest with the host after the rejoin.
cd "$(dirname "$0")/.."
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
S="$(cygpath -m "$PWD/tests/scripts")"
COMMON="--sosonline-delay=3 --sosonline-quit=${QUIT:-5000} $EXTRA"
tests/session.sh 27100 "--sosonline-dropinanywhere --sosonline-play=0 --sosonline-seed=777 --sosonline-quitruns=2 --sosonline-joinmode=${MODE:-auto} --sosonline-script=$S/host-walk.txt $COMMON $HOSTFLAGS" \
  "--sosonline-netdrop=${DROP:-1600:8} $COMMON" "${G_DELAY:-25}"
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
for p in 27100 27101; do echo "--- $p"; grep -h -E "\[session\] (co-op starts|back to solo|slot|lost|trying|no answer|reconnect|dropping)|\[dropin\] .*(join|joining)|stopped at frame|Couldn't" "$O/log-$p.txt" | cut -c1-170; done
python3 - "$O" <<'PY'
import sys
O = sys.argv[1]
def runs(p):
    out, cur, prev = [], {}, None
    for line in open(p, encoding='utf-8'):
        t = line.rstrip('\n').split(' ', 2)
        if len(t) < 3 or t[2].startswith('loading'): continue
        f = int(t[1].lstrip('f'))
        if prev is not None and f < prev: out.append(cur); cur = {}
        prev = f; cur[f] = t[2]
    out.append(cur)
    return out
host = runs(f"{O}/state-27100.log")
for g in runs(f"{O}/state-27101.log"):
    if not g: continue
    cands = [h for h in host if set(h) & set(g)] or host
    best = min(cands, key=lambda h: (sum(h[f] != g[f] for f in set(h) & set(g)), -len(set(h) & set(g))))
    common = sorted(set(best) & set(g)); diff = [f for f in common if best[f] != g[f]]
    print(f"27101: run {min(g)}..{max(g)}: {len(common)} snapshots compared with host run {host.index(best) + 1}, {len(diff)} differ" + (f", first f{diff[0]}" if diff else ""))
PY
