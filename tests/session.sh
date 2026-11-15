#!/usr/bin/env bash
# Launches one host and its guests (loopback UDP). Usage:
#   session.sh HOSTPORT "host flags" [ "guest flags" [delay_s] ]...
# Each guest gets port HOSTPORT+n and joins HOSTPORT; delay_s staggers late joiners. Returns immediately.
# Windows are tiled side by side (host on the left); set NO_TILE=1 to leave them where Unity puts them.
GAME="/w/SteamLibrary/steamapps/common/Sea of Stars"
COMMON="-screen-fullscreen 0 -screen-width 640 -screen-height 360"
PLACE="$(cygpath -w "$(cd "$(dirname "$0")" && pwd)/place-window.ps1")" # absolute: launch() changes directory

# launch INDEX ARGS...: starts an instance and moves its window to tile slot INDEX.
launch() {
  local index=$1; shift
  cd "$GAME" || exit 1
  ./SeaOfStars.exe $COMMON "$@" > /dev/null 2>&1 &
  local winpid
  winpid=$(cat /proc/$!/winpid 2>/dev/null)
  if [ -z "$NO_TILE" ] && [ -n "$winpid" ]; then
    powershell -NoProfile -ExecutionPolicy Bypass -File "$PLACE" -ProcessId "$winpid" -Index "$index" < /dev/null > /dev/null 2>&1 &
  fi
}

hp=$1; hflags=$2; shift 2
launch 0 --sosonline-udp=$hp --sosonline-host $hflags
n=1
while [ $# -gt 0 ]; do
  gflags=$1; wait_s=${2:-4}; shift; [ $# -gt 0 ] && shift
  sleep "$wait_s"
  launch $n --sosonline-udp=$((hp+n)) --sosonline-join=$hp $gflags
  n=$((n+1))
done
