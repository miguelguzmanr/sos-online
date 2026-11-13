@echo off
rem Solo lockstep smoke test: hosts over UDP with no guests, loads save slot 0, takes screenshots, quits.
set GAME=W:\SteamLibrary\steamapps\common\Sea of Stars
start "" /D "%GAME%" "%GAME%\SeaOfStars.exe" -screen-fullscreen 0 -screen-width 960 -screen-height 540 --sosonline-udp=27100 --sosonline-host --sosonline-guests=0 --sosonline-start=0 --sosonline-shots=1,300,900,1500 --sosonline-quit=1600 %*
