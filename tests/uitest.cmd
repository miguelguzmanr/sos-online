@echo off
rem Native menu smoke test: screenshots the title screen with the Online button and the Online popup, then quits.
set GAME=W:\SteamLibrary\steamapps\common\Sea of Stars
start "" /D "%GAME%" "%GAME%\SeaOfStars.exe" -screen-fullscreen 0 -screen-width 960 -screen-height 540 --sosonline-uitest %*
