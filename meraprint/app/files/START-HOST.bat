@echo off
chcp 65001 >nul
title MeraPrint Host - Printer + Scan + Remote
cd /d "%~dp0"
where node >nul 2>nul || (echo Node.js nahi mila. https://nodejs.org se LTS install karein. & pause & exit /b)
if not exist "host\tools" mkdir "host\tools"

rem ---- Internet ke liye Cloudflare (MeraCall wala mil gaya to wahi use hoga) ----
if not exist "host\tools\cloudflared.exe" if not exist "D:\call apk\pc-data\cloudflared.exe" (
  echo cloudflared.exe download ho raha hai ^(free, ek baar^)...
  curl -L -o "host\tools\cloudflared.exe" https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe
)

rem ---- PDF / photo print ke liye SumatraPDF (free, ek baar) ----
if not exist "host\tools\SumatraPDF.exe" (
  echo SumatraPDF download ho raha hai ^(free, ek baar^)...
  for %%V in (3.6.1 3.6 3.5.2) do (
    if not exist "host\tools\SumatraPDF.exe" (
      curl -L -f -s -o "host\tools\sumatra.zip" "https://www.sumatrapdfreader.org/dl/rel/%%V/SumatraPDF-%%V-64.zip"
      if exist "host\tools\sumatra.zip" (
        tar -xf "host\tools\sumatra.zip" -C "host\tools"
        del /q "host\tools\sumatra.zip"
        if exist "host\tools\SumatraPDF-%%V-64.exe" ren "host\tools\SumatraPDF-%%V-64.exe" SumatraPDF.exe
      )
    )
  )
  if not exist "host\tools\SumatraPDF.exe" echo SumatraPDF download nahi hua - sumatrapdfreader.org se portable 64-bit lekar host\tools\SumatraPDF.exe naam se rakhiye.
)

echo.
echo  MeraPrint Host chalu ho raha hai. Ye window khuli rakhein.
echo  Admin panel browser me khul jayega: http://localhost:8791
echo  Windows Firewall poochhe to "Private networks" par Allow dabaiye.
echo.
start "" /min cmd /c "timeout /t 4 >nul & start http://localhost:8791"
:run
node host\host.js
rem 99 = naya update laga -> dobara chalu
if %errorlevel%==99 (
  echo.
  echo  Naya update laga - Host dobara chalu ho raha hai...
  timeout /t 3 >nul
  goto run
)
pause
