#!/usr/bin/env bash
# Screenshots for the README, from loopback test sessions at 1280x720 with test player names.
# Every instance takes screenshots on fixed frames; `tests/readme-shots.sh picks` then copies the README's picks
# to docs/screenshots. Output: BepInEx/SosOnline/readme/<run>-shot-<port>-<frame>.png in the game folder. About 12 minutes.
# Usage: tests/readme-shots.sh [run...]   runs: online party dropin assign battle pause turns timing resync (default: all)
# Close the game first. Tests play from sandbox copies of the saves (--sosonline-start / --sosonline-play).
cd "$(dirname "$0")/.." || exit 1
. tests/game-dir.sh; . tests/launch.sh; O="$GAME/BepInEx/SosOnline"; OUT="$O/readme"; mkdir -p "$OUT"
S="$(cygpath -m "$PWD/tests/scripts")"
SAVES="$LOCALAPPDATA/../LocalLow/Sabotage Studio/Sea of Stars"
export WIN_W=1280 WIN_H=720
every() { seq -s, "$1" 50 "$2"; }
every10() { seq -s, "$1" 10 "$2"; }
clean() { rm -f "$O"/shot-*.png; rm -rf "$SAVES"/SosOnlineTest-* "$SAVES"/SosOnlineSession-27*; }
waitgames() { sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done; }
keep() { for f in "$O"/shot-*.png; do [ -f "$f" ] && mv "$f" "$OUT/$1-$(basename "$f")"; done; clean; }
names=("--sosonline-name=Alex" "--sosonline-name=Sam" "--sosonline-name=Robin")

online() { # The title screen's Online page, hosting (UDP, no Steam lobby), Host Settings with the players' default Rejoin, the version line.
NO_TILE=1 start_game 0 -screen-fullscreen 0 -screen-width 1280 -screen-height 720 --sosonline-udp=27100 --sosonline-joinmode=ask --sosonline-teamtiming=original --sosonline-onlineshot
waitgames; mv "$O"/ui-online-*.png "$OUT/" 2>/dev/null
}

party() { # Three players from the game's Autosave Slot 1 (file 0a; a sandbox copy), walking together.
P="--sosonline-delay=3 --sosonline-quit=2600 --sosonline-shots=$(every 1200 2550)"
tests/session.sh 27100 "--sosonline-dropinanywhere --sosonline-start=0a --sosonline-guests=2 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt ${names[0]} $P"   "--sosonline-script=$S/guest-join-walk.txt ${names[1]} $P" 4 "--sosonline-script=$S/guest-join-walk.txt ${names[2]} $P" 4
waitgames; keep party
}

dropin() { # Drop-in and the local pause: the host plays its own save; Sam drops in, walks, pauses on their PC only (1700),
#    then leaves; Robin drops in later.
C="--sosonline-delay=3 --sosonline-quit=2600 --sosonline-shots=$(every 300 2550)"
tests/session.sh 27100 "--sosonline-dropinanywhere --sosonline-play=0 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt ${names[0]} $C" \
  "--sosonline-script=$S/guest-stage1-leave.txt --sosonline-pausechoose=1790:Leave_Co-op,1830:Leave ${names[1]} $C" 25 \
  "${names[2]} $C" 30
waitgames; keep dropin
}

assign() { # Assign Players: Sam moves to Alex's character.
printf '1690 UIAssignMoveHorizontal 10 -1\n' > "$OUT/assign-guest.txt"
A="--sosonline-delay=3 --sosonline-assigntest=1640 --sosonline-shots=$(every 1650 1800) --sosonline-quit=1820"
tests/session.sh 27100 "--sosonline-dropinanywhere --sosonline-play=0 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt ${names[0]} $A" \
  "--sosonline-script=$(cygpath -m "$OUT/assign-guest.txt") ${names[1]} $A" 25
waitgames; keep assign
}

battle() { # A battle together, the bot timing hits.
B="--sosonline-delay=3 --sosonline-encounter=1500 --sosonline-bot --sosonline-quit=2700 --sosonline-shots=$(every 1550 2650)"
tests/session.sh 27100 "--sosonline-dropinanywhere --sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt ${names[0]} $B" \
  "--sosonline-script=$S/guest-join-walk.txt ${names[1]} $B" 4
waitgames; keep battle
}

pause() { # A pause screen of your own, and joining: Robin asks to join; Alex pauses on their PC only (Sam sees
#    "[P1] Paused"), lets Robin in, and at the next area entry (a synced door) everyone holds while Robin joins.
P="--sosonline-delay=3 --sosonline-quit=3500 --sosonline-shots=$(every10 2000 3450)"
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-joinmode=ask --sosonline-door=2900 --sosonline-script=$S/host-walk.txt --sosonline-pauseopen=2650 --sosonline-pausechoose=2760:Let_Robin_In ${names[0]} $P" \
  "--sosonline-door=2900 --sosonline-script=$S/guest-join-walk.txt ${names[1]} $P" 4 \
  "${names[2]} $P" 30
waitgames; keep pause
}

turns() { # Battle turns over a lagged link (Waiting in the player bar): whose turn it is on both PCs, and the host's
#    battle pause page (Take This Turn, Pass Turn to P2).
T="--sosonline-delay=4 --sosonline-encounter=1500 --sosonline-bot --sosonline-lag=40 --sosonline-jitter=30 --sosonline-spikes=10 --sosonline-quit=2700 --sosonline-shots=$(every10 1600 2650)"
tests/session.sh 27100 "--sosonline-dropinanywhere --sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt --sosonline-pauseopen=1876 ${names[0]} $T" \
  "--sosonline-script=$S/guest-join-walk.txt ${names[1]} $T" 4
waitgames; keep turns
}

timing() { # Team Timing None: Sam's bot presses for every hit, Alex's never does; the timed-hit panel shows only the row
#    of whoever's press counts (Alex's attack ~1784, Sam's ~1984). The same flags as tests/team-timing.sh.
T="--sosonline-delay=4 --sosonline-encounter=1500 --sosonline-ownturns=1 --sosonline-lag=20 --sosonline-jitter=10 --sosonline-quit=2100 --sosonline-shots=$(every10 1760 2000)"
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-teamtiming=none --sosonline-bot=menu --sosonline-botphase=0 --sosonline-script=$S/host-walk.txt ${names[0]} $T"   "--sosonline-bot --sosonline-botall --sosonline-botblock=86 --sosonline-botphase=22 --sosonline-script=$S/guest-join-walk.txt ${names[1]} $T" 4
waitgames; keep timing
}

resync() { # Out of sync: Sam's game diverges at 1500 and rejoins by replay.
R="--sosonline-delay=3 --sosonline-quit=2400 --sosonline-shots=$(every 1450 2350)"
tests/session.sh 27100 "--sosonline-dropinanywhere --sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt ${names[0]} $R" \
  "--sosonline-script=$S/guest-join-walk.txt --sosonline-hpdiverge=1500 ${names[1]} $R" 4
waitgames; keep resync
}

picks() { # The README's picks from the runs above (frames fixed by the runs' flags) into docs/screenshots, with
#    2x close-ups of the player bar. Needs Python with Pillow.
D=docs/screenshots
pick() { cp "$OUT/$1" "$D/$2" && echo "$D/$2"; }
crop() { python tests/crop-shot.py "$OUT/$1" $2 $3 $4 $5 2 "$D/$6"; }
pick ui-online-page-27100.png online-menu.png
pick ui-online-options-help-27100.png online-options.png
crop pause-shot-27100-003040.png 8 8 86 128 bar-play.png          # in play: icons only, Robin joining
crop pause-shot-27100-002700.png 8 8 270 128 bar-names.png        # on a pause screen: names, Paused, Wants to join
crop turns-shot-27100-001880.png 8 8 210 92 bar-waiting.png       # a lagged link: Waiting
pick pause-shot-27100-002700.png pause-host.png
pick pause-shot-27101-002700.png pause-friend.png
pick turns-shot-27100-001750.png turn-host.png
pick turns-shot-27101-001750.png turn-friend.png
pick turns-shot-27100-001880.png battle-menu-host.png
pick turns-shot-27101-001880.png battle-menu-friend.png
pick pause-shot-27100-003040.png join-hold.png
pick pause-shot-27102-003240.png join-joined.png
pick assign-shot-27100-001750.png assign-players.png
crop timing-shot-27100-001790.png 1080 470 200 110 timing-host.png    # Team Timing None: Alex's attack, only P1's row
crop timing-shot-27100-001990.png 1080 470 200 110 timing-friend.png  # Sam's attack, only P2's row
pick party-shot-27100-001300.png three-players.png
pick dropin-shot-27100-001350.png two-players.png
}
[ "$1" = picks ] && { picks; exit; }

bash tests/display-prefs.sh save
clean
for run in ${@:-online party dropin assign battle pause turns timing resync}; do echo "run $run"; "$run"; done
bash tests/display-prefs.sh restore
ls "$OUT" | grep -c png
