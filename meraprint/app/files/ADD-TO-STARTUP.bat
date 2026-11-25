@echo off
rem PC chalu hote hi MeraPrint Host apne aap chalu ho (Startup folder me shortcut)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Startup')+'\MeraPrint Host.lnk'); $s.TargetPath='%~dp0START-HOST.bat'; $s.WorkingDirectory='%~dp0'; $s.WindowStyle=7; $s.Save()"
echo Ho gaya. Ab PC chalu hote hi MeraPrint Host apne aap chalega (chhoti window me).
echo Hatana ho to: Win+R -^> shell:startup -^> "MeraPrint Host" shortcut delete karein.
pause
