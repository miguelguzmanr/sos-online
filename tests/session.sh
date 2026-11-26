#!/usr/bin/env bash
# Launches one host and its guests (loopback UDP). Usage:
#   session.sh HOSTPORT "host flags" [ "guest flags" [delay_s] ]...
# Each guest gets port HOSTPORT+n and joins HOSTPORT; delay_s staggers late joiners. Returns immediately.
# Instances run in the background on a hidden desktop, muted and deaf to this PC's devices; VISIBLE=1 tiles them side by side
# instead (host on the left; NO_TILE=1 leaves them where Unity puts them). See launch.sh.
. "$(dirname "$0")/game-dir.sh"
. "$(dirname "$0")/launch.sh"
# WIN_W/WIN_H change the window size (e.g. 1280x720 for readme-shots.sh).
COMMON="-screen-fullscreen 0 -screen-width ${WIN_W:-640} -screen-height ${WIN_H:-360}"

# launch INDEX ARGS...: starts an instance in place INDEX.
launch() {
  local index=$1; shift
  start_game "$index" $COMMON "$@"
  [ "$index" = 0 ] && HOST_WINPID=$GAME_PID
}

hp=$1; hflags=$2; shift 2
launch 0 --sosonline-udp=$hp --sosonline-host $hflags
n=1
while [ $# -gt 0 ]; do
  gflags=$1; wait_s=${2:-4}; shift; [ $# -gt 0 ] && shift
  sleep "$wait_s"
  # A joiner for a host that already quit would wait at the title screen until its 300 s timeout.
  if [ -n "$HOST_WINPID" ] && ! tasklist //FI "PID eq $HOST_WINPID" 2>/dev/null | grep -qi SeaOfStars; then
    echo "session.sh: the host has quit; not starting guest $n" >&2
    n=$((n+1)); continue
  fi
  launch $n --sosonline-udp=$((hp+n)) --sosonline-join=$hp $gflags
  n=$((n+1))
done
