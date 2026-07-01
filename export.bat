@echo off
setlocal enabledelayedexpansion

set STUDIO_EXE=C:\Games\KK\KKIRV2\CharaStudio.exe
set URL=http://localhost:8080
set WAIT_SECONDS=40
set OUTPUT_FILE=export_%date:~-4%%date:~-7,2%%date:~-10,2%_%time:~0,2%%time:~3,2%%time:~6,2%.glb
set OUTPUT_FILE=%OUTPUT_FILE: =0%

echo === GLB Auto Export ===
echo.

echo [1/6] Launching Chara Studio...
start "" "%STUDIO_EXE%"
echo       Waiting %WAIT_SECONDS% seconds for Studio to load...
timeout /t %WAIT_SECONDS% /nobreak >nul

echo [2/6] Checking connection...
set CONNECTED=0
for /l %%i in (1,1,5) do (
    if !CONNECTED! equ 0 (
        powershell -Command "try { (Invoke-WebRequest -Uri '%URL%/status' -TimeoutSec 5 -UseBasicParsing).Content } catch { exit 1 }" >nul 2>&1
        if !errorlevel! equ 0 (
            echo       Connected.
            set CONNECTED=1
        ) else (
            echo       Attempt %%i/5 - retrying in 5s...
            timeout /t 5 /nobreak >nul
        )
    )
)
if !CONNECTED! equ 0 (
    echo FAIL: Cannot connect to plugin
    exit /b 1
)

echo [3/6] Adding character (index 0)...
powershell -Command "try { (Invoke-WebRequest -Uri '%URL%/add-character?index=0' -Method POST -TimeoutSec 10 -UseBasicParsing).Content } catch { Write-Output 'ERROR' }"
echo       Waiting 8 seconds for character to load...
timeout /t 8 /nobreak >nul

echo [4/6] Listing characters...
powershell -Command "try { (Invoke-WebRequest -Uri '%URL%/list-characters' -TimeoutSec 5 -UseBasicParsing).Content } catch { Write-Output 'ERROR' }"

echo [5/6] Selecting character (index 0)...
powershell -Command "try { (Invoke-WebRequest -Uri '%URL%/select-character?index=0' -Method POST -TimeoutSec 5 -UseBasicParsing).Content } catch { Write-Output 'ERROR' }"
timeout /t 2 /nobreak >nul

echo [6/6] Exporting GLB with bodyOnly=true + head + hair...
powershell -Command "$r = Invoke-WebRequest -Uri '%URL%/export-glb?filename=%OUTPUT_FILE%&bodyOnly=true&includeHead=true&includeHair=true&resolution=2048' -Method POST -TimeoutSec 120 -UseBasicParsing; $r.Content"

echo.
echo === Done ===
echo Output: %OUTPUT_FILE%
echo.
pause
