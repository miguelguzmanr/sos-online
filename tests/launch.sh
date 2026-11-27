# Sourced by the test scripts, after game-dir.sh. start_game INDEX ARGS... starts one game instance from the game
# folder with ARGS and sets GAME_PID to its Windows process id. INDEX is its place (0 = the host).
#
# Background (the default): tests run behind your work. start-game.ps1 starts the game on a hidden desktop, where its
# windows never show and can't take the focus; the game gets --sosonline-background (muted, pads ignored). BepInEx's console window is turned off
# (it would take the focus); tests/display-prefs.sh restore turns it back on.
# Every instance first makes sure the game's display preferences are backed up and come back once the last test
# instance has quit (display-prefs.sh ensure): the -screen-* flags would otherwise stay as the player's resolution.
# VISIBLE=1: windows on screen, tiled side by side on the primary screen, host on the left (NO_TILE=1 leaves them
# where Unity puts them), with sound; the PC's keyboard and pads reach them.

LAUNCH_TESTS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Logging.Console in BepInEx.cfg: off for background runs, noted in tests/.console-was-on for display-prefs.sh.
console_off() {
  local cfg="$GAME/BepInEx/config/BepInEx.cfg"
  [ -f "$cfg" ] || return 0
  if sed -n '/^\[Logging\.Console\]/,/^\[/p' "$cfg" | grep -q '^Enabled = true'; then
    sed -i '/^\[Logging\.Console\]/,/^\[/ s/^Enabled = true/Enabled = false/' "$cfg" && touch "$LAUNCH_TESTS/.console-was-on"
  fi
}

start_game() {
  local index=$1; shift
  GAME_PID=
  bash "$LAUNCH_TESTS/display-prefs.sh" ensure
  if [ -n "$VISIBLE" ]; then
    local here=$PWD
    cd "$GAME" || return 1
    ./SeaOfStars.exe "$@" > /dev/null 2>&1 &
    GAME_PID=$(cat /proc/$!/winpid 2>/dev/null)
    cd "$here"
    if [ -z "$NO_TILE" ] && [ -n "$GAME_PID" ]; then
      powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$LAUNCH_TESTS/place-window.ps1")" \
        -ProcessId "$GAME_PID" < /dev/null > /dev/null 2>&1 &
    fi
    return 0
  fi
  console_off
  # One Windows command line; arguments with spaces are quoted, as bash does when it starts a Windows program.
  local a args=
  for a in "$@" --sosonline-background; do
    case "$a" in *" "*) args+=" \"$a\"" ;; *) args+=" $a" ;; esac
  done
  local pidfile log="$GAME/BepInEx/SosOnline/launch-$index.txt"
  pidfile="$(mktemp)"; rm -f "$pidfile"
  mkdir -p "$GAME/BepInEx/SosOnline"
  SOS_GAME_ARGS="${args# }" powershell -NoProfile -ExecutionPolicy Bypass \
    -File "$(cygpath -w "$LAUNCH_TESTS/start-game.ps1")" -Exe "$(cygpath -w "$GAME/SeaOfStars.exe")" \
    -PidFile "$(cygpath -w "$pidfile")" < /dev/null > "$log" 2>&1 &
  local t=0
  while [ ! -s "$pidfile" ] && [ $t -lt 150 ]; do sleep 0.2; t=$((t+1)); done
  GAME_PID=$(tr -dc '0-9' < "$pidfile" 2>/dev/null); rm -f "$pidfile"
  [ -n "$GAME_PID" ] || { echo "launch.sh: instance $index didn't start (see $log)" >&2; return 1; }
}
