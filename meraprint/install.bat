@echo off
rem MeraPrint Host - kisi bhi PC ko Host banayein (printer / scanner / files / remote)
rem Har Host ka apna Host ID, PIN aur data hota hai. Update apne aap aate hain.
setlocal
title MeraPrint Host Install
set "REPO=CGPANKAJ22/MeraCall"
set "MPSELF=%~f0"
set "DEST=%SystemDrive%\MeraPrint-Host"
echo.
echo   MeraPrint Host install ho raha hai: %DEST%
echo.
where node >nul 2>nul
if errorlevel 1 if exist "%ProgramFiles%\nodejs\node.exe" set "PATH=%PATH%;%ProgramFiles%\nodejs"
where node >nul 2>nul
if errorlevel 1 (
  echo   Node.js nahi mila - install kar rahe hain ^(ek baar^)...
  winget install -e --id OpenJS.NodeJS.LTS --accept-source-agreements --accept-package-agreements
  set "PATH=%PATH%;%ProgramFiles%\nodejs"
)
if not exist "%ProgramFiles%\nodejs\node.exe" (
  where node >nul 2>nul
  if errorlevel 1 (
    echo   Node.js apne aap nahi laga. Browser me nodejs.org khul raha hai - LTS install karke ye file dobara chalaiye.
    start https://nodejs.org/
    pause
    exit /b
  )
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t=[IO.File]::ReadAllText($env:MPSELF); $m='#'+'==PS=='; $i=$t.LastIndexOf($m); Invoke-Expression $t.Substring($i)"
if errorlevel 1 (
  echo.
  echo   Install nahi hua - upar ki galti ka photo bhejiye.
  pause
  exit /b
)
echo.
echo   Install ho gaya. Desktop par "MeraPrint Host" bana hai.
echo   Ab Host chalu ho raha hai - browser me Admin panel khulega (Host ID + PIN).
echo   PC chalu hote hi Host chale: %DEST%\ADD-TO-STARTUP.bat ek baar chalaiye.
echo.
start "MeraPrint Host" /d "%DEST%" "%DEST%\START-HOST.bat"
timeout /t 10 >nul
exit /b
#==PS==
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$repo = $env:REPO; $dest = $env:DEST
$base = "https://raw.githubusercontent.com/$repo/HEAD/meraprint/app"
$m = Invoke-RestMethod ("$base/manifest.json?t=" + [DateTime]::Now.Ticks)
New-Item -ItemType Directory -Force $dest | Out-Null
foreach ($f in $m.files) {
    $p = Join-Path $dest ($f.path -replace '/', '\')
    if ((Test-Path $p) -and ((Get-FileHash $p -Algorithm SHA256).Hash.ToLower() -eq $f.sha256)) { continue }
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    Write-Host ('   ' + $f.path)
    $ok = $false
    for ($k = 0; $k -lt 4 -and -not $ok; $k++) {
        Invoke-WebRequest -UseBasicParsing ("$base/files/" + $f.path + "?t=" + [DateTime]::Now.Ticks) -OutFile $p
        $ok = (Get-FileHash $p -Algorithm SHA256).Hash.ToLower() -eq $f.sha256
        if (-not $ok) { Start-Sleep 8 }
    }
    if (-not $ok) { throw ($f.path + ' sahi download nahi hua (GitHub abhi purani file de raha hai) - 5 minute baad dobara chalaiye') }
}
(@{ version = $m.version } | ConvertTo-Json) | Set-Content -Encoding ASCII (Join-Path $dest 'app-version.json')
$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop') + '\MeraPrint Host.lnk')
$s.TargetPath = (Join-Path $dest 'START-HOST.bat'); $s.WorkingDirectory = $dest; $s.Save()
Write-Host ('   Version ' + $m.version + ' install hua')
