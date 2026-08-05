@echo off
setlocal

set "PROJ_DIR=D:\Users\HAO\source\repos\T0Prototype"
set "CSJ=%PROJ_DIR%\T0Prototype.csproj"
set "DLL=%PROJ_DIR%\bin\Release\net8.0-windows\T0Prototype.dll"

:: --- Check .NET 8 SDK ---
dotnet --list-sdks 2>nul | findstr /r "^8\.">nul
if %errorlevel% equ 0 (
    echo === .NET 8 SDK detected, skipping install ===
) else (
    echo === Installing .NET 8 SDK one-time ~2 min ===
    "%PROJ_DIR%\dotnet-sdk-8-installer.exe" /install /quiet /norestart
    if %errorlevel% neq 0 (
        echo INSTALLER FAILED code %errorlevel%
        pause
        exit /b 1
    )
)

:: --- Ensure global.json locks .NET 8 ---
echo {"sdk": {"version": "8.0.414"}} > "%PROJ_DIR%\global.json"

:: --- Build ---
echo.
echo === Restoring ===
call dotnet restore "%CSJ%"
if errorlevel 1 (
    echo RESTORE FAILED
    pause
    exit /b 1
)

echo.
echo === Building (WPF + .NET 8, Release) ===
call dotnet build "%CSJ%" --configuration Release
if errorlevel 1 (
    echo BUILD FAILED
    pause
    exit /b 1
)

:: --- Run (detached: closing this CMD will NOT kill the cube) ---
echo.
echo === Launching T0 Prototype (WPF) ===
echo If a transparent cube window appears == PASS
start "" dotnet exec "%DLL%"
if errorlevel 1 (
    echo LAUNCH FAILED
    pause
    exit /b 1
)
echo Cube is now running in its own process. You can close this window safely.
pause
