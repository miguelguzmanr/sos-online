#!/usr/bin/env bash
# Test runs pass -screen-* flags, which Unity stores as the game's display preferences (resolution, window mode) when
# an instance quits. This keeps the player's own ones:
#   save     backs up the game's preference key, unless a backup is already there: a backup is only ever taken before
#            any test instance has changed the key, and nested or repeated saves keep it.
#   restore  puts the backup back and deletes it, once no test instance is running. Also turns BepInEx's console
#            window back on if a background test run turned it off (launch.sh).
#   ensure   save, and start the watcher if it isn't running. launch.sh calls it for every instance it starts.
#   watch    (the watcher) waits until no test instance has run for a few seconds, then restores; it keeps waiting
#            while new instances start. So the settings come back after every test, also an interrupted one and one
#            whose script never calls restore.
# Only instances started with --sosonline-* flags count: the player's own game is never waited for.
T="$(cd "$(dirname "$0")" && pwd)"
F="$T/.display-prefs.reg"
C="$T/.console-was-on"
W="$T/.display-prefs.watch"
K='HKCU\Software\Sabotage Studio\Sea of Stars'

# Any test instance of the game running?
tests_running() {
  powershell -NoProfile -Command "if (Get-CimInstance Win32_Process -Filter \"Name='SeaOfStars.exe'\" | Where-Object { \$_.CommandLine -match '--sosonline-' }) { exit 0 } else { exit 1 }" < /dev/null > /dev/null 2>&1
}

save() {
  [ -f "$F" ] && return 0
  reg export "$K" "$(cygpath -w "$F")" //y > /dev/null && echo "saved display prefs"
}

restore() {
  tests_running && { echo "test instances still running: display prefs not restored yet"; return 0; }
  if [ -f "$F" ]; then
    reg import "$(cygpath -w "$F")" > /dev/null 2>&1 && rm -f "$F" && echo "restored display prefs"
  fi
  if [ -f "$C" ]; then
    . "$T/game-dir.sh"
    sed -i '/^\[Logging\.Console\]/,/^\[/ s/^Enabled = false/Enabled = true/' "$GAME/BepInEx/config/BepInEx.cfg" && rm -f "$C" && echo "BepInEx console back on"
  fi
}

watcher_alive() { [ -f "$W" ] && kill -0 "$(cat "$W" 2>/dev/null)" 2>/dev/null; }

case "$1" in
  save) save ;;
  restore) restore ;;
  ensure)
    save > /dev/null
    if ! watcher_alive; then
      nohup bash "$T/display-prefs.sh" watch < /dev/null > /dev/null 2>&1 &
      echo $! > "$W"
    fi ;;
  watch)
    sleep 10 # the instance that asked for the watcher is still starting
    while [ -f "$F" ] || [ -f "$C" ]; do
      sleep 3
      tests_running && continue
      sleep 3 # a script may start its next instance right after the last one quit
      tests_running && continue
      restore > /dev/null
    done
    rm -f "$W" ;;
esac
