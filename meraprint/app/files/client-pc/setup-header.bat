@echo off
rem MeraPrint Client Setup - is PC ke HAR software me Host PC ke printer + NAPS2 scanner + Files
rem Ye file khud MeraPrintClient.exe banati hai (Windows ka apna C# compiler) - kuch download nahi.
rem "/quiet" = apne aap update (koi window / pause nahi)
setlocal
title MeraPrint Client Setup
set "Q=%~1"
set "MPHOSTS=__HOSTS__"
set "MPSELF=%~f0"
set "D=%LOCALAPPDATA%\MeraPrint"
if not exist "%D%" mkdir "%D%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t=[IO.File]::ReadAllText($env:MPSELF); $m='//'+'==CSHARP=='; $i=$t.LastIndexOf($m); [IO.File]::WriteAllText((Join-Path $env:D 'MeraPrintClient.new.cs'), $t.Substring($i), (New-Object Text.UTF8Encoding $true))"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (echo .NET Framework 4 nahi mila - Windows Update chalaiye. & if not "%Q%"=="/quiet" pause & exit /b)
echo MeraPrint Client ban raha hai...
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:"%D%\MeraPrintClient.new.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll "%D%\MeraPrintClient.new.cs"
if errorlevel 1 (
  echo. & echo Naya client nahi bana - purana hi chalta rahega. Upar ki galti ka photo bhejiye.
  if not "%Q%"=="/quiet" pause
  exit /b
)
rem bana gaya -> purana band, naya lagao
taskkill /im MeraPrintClient.exe /f >nul 2>nul
ping -n 3 127.0.0.1 >nul
move /y "%D%\MeraPrintClient.new.exe" "%D%\MeraPrintClient.exe" >nul
move /y "%D%\MeraPrintClient.new.cs" "%D%\MeraPrintClient.cs" >nul
start "" "%D%\MeraPrintClient.exe" /hosts "%MPHOSTS%"
if "%Q%"=="/quiet" exit /b
echo.
echo Ho gaya. Pehli baar ho to ek window khulegi: Host ka pata + PIN daaliye.
echo Uske baad Windows "Yes" poochhega - printers banane ke liye.
echo MeraPrint ab PC chalu hote hi apne aap chalega (ghadi ke paas icon).
timeout /t 8 >nul
exit /b
