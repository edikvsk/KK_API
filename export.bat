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

:: Generate output filename
for /f "usebackq delims=" %%d in (`powershell -NoProfile -NonInteractive -Command "Get-Date -Format 'yyyyMMdd_HHmmss'"`) do set OUTPUT_FILE=export_%%d.glb

echo === KK GLB Auto Export ===
echo.

:: === Step 1: Build plugin if source is newer than DLL ===
echo [1/7] Checking plugin build...

set NEED_BUILD=0
if not exist "%PLUGIN_SRC%\StudioHTTPAPI.cs" goto :skip_build
if not exist "%PLUGIN_DLL%" (
    set NEED_BUILD=1
    goto :do_build
)
powershell -NoProfile -NonInteractive -Command "try { if ((Get-Item '%PLUGIN_SRC%\StudioHTTPAPI.cs').LastWriteTime -gt (Get-Item '%PLUGIN_DLL%').LastWriteTime) { exit 1 } else { exit 0 } } catch { exit 1 }" >nul 2>&1
if !errorlevel! equ 1 set NEED_BUILD=1

:do_build
if !NEED_BUILD! equ 0 goto :skip_build
echo       Source changed, building plugin...
dotnet build "%PLUGIN_SRC%\StudioHTTPAPI.csproj" -c Release --nologo -v q >nul 2>&1
if !errorlevel! neq 0 (
    echo       Build FAILED!
    echo.
    pause
    exit /b 1
)
copy /Y "%PLUGIN_SRC%\bin\Release\net46\StudioHTTPAPI.dll" "%PLUGIN_DLL%" >nul
echo       Plugin built and deployed.
set RESTART_STUDIO=1
goto :check_studio

:skip_build
echo       Plugin is up to date.
set RESTART_STUDIO=0

:check_studio
:: === Step 2: Check if CharaStudio is running, restart if plugin was rebuilt ===
echo [2/7] Checking CharaStudio...

set STUDIO_RUNNING=0
powershell -NoProfile -NonInteractive -Command "try { Get-Process -Name CharaStudio -ErrorAction Stop; exit 0 } catch { exit 1 }" >nul 2>&1
if !errorlevel! equ 0 set STUDIO_RUNNING=1

if !RESTART_STUDIO! equ 0 goto :studio_check_done
if !STUDIO_RUNNING! equ 0 goto :start_studio
echo       Stopping CharaStudio (plugin updated)...
powershell -NoProfile -NonInteractive -Command "Stop-Process -Name CharaStudio -Force -ErrorAction SilentlyContinue" >nul 2>&1
timeout /t 3 /nobreak >nul
set STUDIO_RUNNING=0

:start_studio
if !STUDIO_RUNNING! equ 1 goto :studio_check_done
echo       Starting CharaStudio...
start "" "%STUDIO_EXE%"
echo       Waiting %WAIT_SECONDS% seconds for Studio to load...
timeout /t %WAIT_SECONDS% /nobreak >nul
goto :connect_plugin

:studio_check_done
echo       CharaStudio is running.

:connect_plugin
:: === Step 3: Wait for plugin HTTP server ===
echo [3/7] Connecting to plugin...
set CONNECTED=0
for /l %%i in (1,1,10) do (
    if !CONNECTED! equ 1 goto :connected
    powershell -NoProfile -NonInteractive -Command "try { $null = Invoke-WebRequest -Uri '%URL%/status' -TimeoutSec 3 -UseBasicParsing; exit 0 } catch { exit 1 }" >nul 2>&1
    if !errorlevel! equ 0 (
        echo       Connected.
        set CONNECTED=1
    ) else (
        echo       Waiting... (attempt %%i/10)
        timeout /t 5 /nobreak >nul
    )
)
:connected
if !CONNECTED! equ 0 (
    echo FAIL: Cannot connect to plugin after 50 seconds
    echo.
    pause
    exit /b 1
)

:: === Step 4: Add character ===
echo [4/7] Adding character (index %CHAR_INDEX%)...
powershell -NoProfile -NonInteractive -Command "try { $null = Invoke-WebRequest -Uri '%URL%/add-character?index=%CHAR_INDEX%' -Method POST -TimeoutSec 10 -UseBasicParsing; exit 0 } catch { exit 1 }" >nul 2>&1
echo       Waiting 8 seconds for character to load...
timeout /t 8 /nobreak >nul

:: === Step 5: List characters ===
echo [5/7] Listing characters...
powershell -NoProfile -NonInteractive -Command "try { (Invoke-WebRequest -Uri '%URL%/list-characters' -TimeoutSec 5 -UseBasicParsing).Content } catch { Write-Output '{\"error\":\"failed\"}' }"

:: === Step 6: Select character ===
echo [6/7] Selecting character (index %CHAR_INDEX%)...
powershell -NoProfile -NonInteractive -Command "try { $null = Invoke-WebRequest -Uri '%URL%/select-character?index=%CHAR_INDEX%' -Method POST -TimeoutSec 5 -UseBasicParsing; exit 0 } catch { exit 1 }" >nul 2>&1
timeout /t 2 /nobreak >nul

:: === Step 7: Export GLB ===
echo [7/7] Exporting GLB...
echo       Options: bodyOnly + head + hair + eyes + eyebrows, resolution=%RESOLUTION%
set EXPORT_RESULT=
for /f "delims=" %%r in ('powershell -NoProfile -NonInteractive -Command "try { $r = Invoke-WebRequest -Uri '%URL%/export-glb?filename=%OUTPUT_FILE%&bodyOnly=true&includeHead=true&includeHair=true&includeEyes=true&includeEyebrows=true&resolution=%RESOLUTION%' -Method POST -TimeoutSec 180 -UseBasicParsing; $r.Content } catch { Write-Output '{\"error\":\"export failed\"}' }"') do set EXPORT_RESULT=%%r

echo.
echo %EXPORT_RESULT%

:: Check if export succeeded
echo %EXPORT_RESULT% | find /i "status" >nul 2>&1
if !errorlevel! neq 0 goto :export_failed
echo %EXPORT_RESULT% | find /i "ok" >nul 2>&1
if !errorlevel! neq 0 goto :export_failed
echo %EXPORT_RESULT% | find /i "error" >nul 2>&1
if !errorlevel! equ 0 goto :export_failed

echo.
echo === Export SUCCESS ===
echo Output: %KK_DIR%\UserData\export\%OUTPUT_FILE%
echo.
echo Closing in 2 seconds...
timeout /t 2 /nobreak >nul
exit /b 0

:export_failed
echo.
echo === Export FAILED ===
echo.
pause
exit /b 1
