#!/usr/bin/env bash
# Launches one host and its guests (loopback UDP). Usage:
#   session.sh HOSTPORT "host flags" [ "guest flags" [delay_s] ]...
# Each guest gets port HOSTPORT+n and joins HOSTPORT; delay_s staggers late joiners. Returns immediately.
GAME="/w/SteamLibrary/steamapps/common/Sea of Stars"
COMMON="-screen-fullscreen 0 -screen-width 640 -screen-height 360"
hp=$1; hflags=$2; shift 2
(cd "$GAME" && ./SeaOfStars.exe $COMMON --sosonline-udp=$hp --sosonline-host $hflags > /dev/null 2>&1 &)
n=1
while [ $# -gt 0 ]; do
  gflags=$1; wait_s=${2:-4}; shift; [ $# -gt 0 ] && shift
  sleep "$wait_s"
  (cd "$GAME" && ./SeaOfStars.exe $COMMON --sosonline-udp=$((hp+n)) --sosonline-join=$hp $gflags > /dev/null 2>&1 &)
  n=$((n+1))
done
