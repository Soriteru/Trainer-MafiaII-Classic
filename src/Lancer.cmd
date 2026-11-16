@echo off
cd /d "%~dp0"
start "" "%SystemRoot%\System32\wscript.exe" //B //Nologo "%~dp0Lancer.vbs"
exit /b
