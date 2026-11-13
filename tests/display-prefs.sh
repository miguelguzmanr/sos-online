#!/usr/bin/env bash
# Test runs pass -screen-* flags, which Unity stores as the game's display preferences.
# "save" backs up the game's preference key before a test, "restore" puts it back afterwards.
F="$(dirname "$0")/.display-prefs.reg"
K='HKCU\Software\Sabotage Studio\Sea of Stars'
case "$1" in
  save) reg export "$K" "$(cygpath -w "$F")" //y > /dev/null && echo "saved display prefs" ;;
  restore) [ -f "$F" ] && reg import "$(cygpath -w "$F")" > /dev/null 2>&1 && echo "restored display prefs" ;;
esac
