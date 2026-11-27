@echo off
rem Native menu smoke test: screenshots the title screen with the Online button and the Online popup, then quits.
for /f "usebackq delims=" %%g in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\tools\game-dir.ps1"`) do set "GAME=%%g"
if not defined GAME exit /b 1
rem Keep the player's resolution: back up the display preferences, restore them once the test instances quit.
if exist "%ProgramFiles%\Git\bin\bash.exe" "%ProgramFiles%\Git\bin\bash.exe" "%~dp0display-prefs.sh" ensure
start "" /D "%GAME%" "%GAME%\SeaOfStars.exe" -screen-fullscreen 0 -screen-width 960 -screen-height 540 --sosonline-uitest %*
