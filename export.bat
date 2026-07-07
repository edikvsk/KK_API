@echo off
setlocal enabledelayedexpansion

:: === Configuration ===
set KK_DIR=C:\Games\KK\KKIRV2
set STUDIO_EXE=%KK_DIR%\CharaStudio.exe
set PLUGIN_SRC=%KK_DIR%\BepInEx\plugins\StudioHTTPAPI
set PLUGIN_DLL=%KK_DIR%\BepInEx\plugins\StudioHTTPAPI.dll
set URL=http://localhost:8080
set WAIT_SECONDS=40
set CHAR_INDEX=0
set RESOLUTION=2048

:: Generate output filename (locale-independent via PowerShell)
for /f "usebackq delims=" %%d in (`powershell -NoProfile -NonInteractive -Command "Get-Date -Format 'yyyyMMdd_HHmmss'"`) do set OUTPUT_FILE=export_%%d.glb

echo === KK GLB Auto Export ===
echo.

:: === Step 1: Build plugin if source is newer than DLL ===
echo [1/7] Checking plugin build...

:: Check if .cs source exists and if build is needed
set NEED_BUILD=0
if exist "%PLUGIN_SRC%\StudioHTTPAPI.cs" (
    if not exist "%PLUGIN_DLL%" (
        set NEED_BUILD=1
    ) else (
        :: Compare timestamps using PowerShell
        powershell -NoProfile -NonInteractive -Command "if ((Get-Item '%PLUGIN_SRC%\StudioHTTPAPI.cs').LastWriteTime -gt (Get-Item '%PLUGIN_DLL%').LastWriteTime) { exit 1 } else { exit 0 }" >nul 2>&1
        if !errorlevel! equ 1 set NEED_BUILD=1
    )
)

if !NEED_BUILD! equ 1 (
    echo       Source changed, building plugin...
    dotnet build "%PLUGIN_SRC%\StudioHTTPAPI.csproj" -c Release --nologo -v q 2>&1 | findstr /i "error"
    if !errorlevel! equ 0 (
        echo       Build failed!
        pause
        exit /b 1
    )
    :: Copy DLL to BepInEx plugins root (where BepInEx loads it)
    copy /Y "%PLUGIN_SRC%\bin\Release\net46\StudioHTTPAPI.dll" "%PLUGIN_DLL%" >nul
    echo       Plugin built and deployed.
    set RESTART_STUDIO=1
) else (
    echo       Plugin is up to date.
    set RESTART_STUDIO=0
)

:: === Step 2: Check if CharaStudio is running, restart if plugin was rebuilt ===
echo [2/7] Checking CharaStudio...

set STUDIO_RUNNING=0
tasklist /FI "IMAGENAME eq CharaStudio.exe" 2>nul | find /i "CharaStudio.exe" >nul
if !errorlevel! equ 0 set STUDIO_RUNNING=1

if !RESTART_STUDIO! equ 1 (
    if !STUDIO_RUNNING! equ 1 (
        echo       Stopping CharaStudio (plugin updated)...
        taskkill /F /IM CharaStudio.exe >nul 2>&1
        timeout /t 3 /nobreak >nul
    )
)

if !STUDIO_RUNNING! equ 0 (
    echo       Starting CharaStudio...
    start "" "%STUDIO_EXE%"
    echo       Waiting %WAIT_SECONDS% seconds for Studio to load...
    timeout /t %WAIT_SECONDS% /nobreak >nul
) else (
    if !RESTART_STUDIO! equ 0 (
        echo       CharaStudio is running.
    )
)

:: === Step 3: Wait for plugin HTTP server ===
echo [3/7] Connecting to plugin...
set CONNECTED=0
for /l %%i in (1,1,10) do (
    if !CONNECTED! equ 0 (
        powershell -NoProfile -NonInteractive -Command "try { (Invoke-WebRequest -Uri '%URL%/status' -TimeoutSec 3 -UseBasicParsing).Content } catch { exit 1 }" >nul 2>&1
        if !errorlevel! equ 0 (
            echo       Connected.
            set CONNECTED=1
        ) else (
            echo       Waiting... (attempt %%i/10)
            timeout /t 5 /nobreak >nul
        )
    )
)
if !CONNECTED! equ 0 (
    echo FAIL: Cannot connect to plugin after 50 seconds
    pause
    exit /b 1
)

:: === Step 4: Add character ===
echo [4/7] Adding character (index %CHAR_INDEX%)...
powershell -NoProfile -NonInteractive -Command "try { (Invoke-WebRequest -Uri '%URL%/add-character?index=%CHAR_INDEX%' -Method POST -TimeoutSec 10 -UseBasicParsing).Content } catch { Write-Output 'ERROR' }"
echo       Waiting 8 seconds for character to load...
timeout /t 8 /nobreak >nul

:: === Step 5: List characters ===
echo [5/7] Listing characters...
powershell -NoProfile -NonInteractive -Command "try { (Invoke-WebRequest -Uri '%URL%/list-characters' -TimeoutSec 5 -UseBasicParsing).Content } catch { Write-Output 'ERROR' }"

:: === Step 6: Select character ===
echo [6/7] Selecting character (index %CHAR_INDEX%)...
powershell -NoProfile -NonInteractive -Command "try { (Invoke-WebRequest -Uri '%URL%/select-character?index=%CHAR_INDEX%' -Method POST -TimeoutSec 5 -UseBasicParsing).Content } catch { Write-Output 'ERROR' }"
timeout /t 2 /nobreak >nul

:: === Step 7: Export GLB ===
echo [7/7] Exporting GLB...
echo       Options: bodyOnly + head + hair + eyes + eyebrows, resolution=%RESOLUTION%
powershell -NoProfile -NonInteractive -Command "$r = Invoke-WebRequest -Uri '%URL%/export-glb?filename=%OUTPUT_FILE%&bodyOnly=true&includeHead=true&includeHair=true&includeEyes=true&includeEyebrows=true&resolution=%RESOLUTION%' -Method POST -TimeoutSec 120 -UseBasicParsing; $r.Content"

echo.
echo === Export Complete ===
echo Output: %KK_DIR%\UserData\export\%OUTPUT_FILE%
