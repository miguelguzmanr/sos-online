#!/usr/bin/env bash
# Test runs pass -screen-* flags, which Unity stores as the game's display preferences.
# "save" backs up the game's preference key before a test, "restore" puts it back afterwards.
# "restore" also turns BepInEx's console window back on if a background test run turned it off (launch.sh).
F="$(dirname "$0")/.display-prefs.reg"
C="$(dirname "$0")/.console-was-on"
K='HKCU\Software\Sabotage Studio\Sea of Stars'
case "$1" in
  save) reg export "$K" "$(cygpath -w "$F")" //y > /dev/null && echo "saved display prefs" ;;
  restore)
    [ -f "$F" ] && reg import "$(cygpath -w "$F")" > /dev/null 2>&1 && echo "restored display prefs"
    if [ -f "$C" ]; then
      . "$(dirname "$0")/game-dir.sh"
      sed -i '/^\[Logging\.Console\]/,/^\[/ s/^Enabled = false/Enabled = true/' "$GAME/BepInEx/config/BepInEx.cfg" && rm -f "$C" && echo "BepInEx console back on"
    fi ;;
esac
