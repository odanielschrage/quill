@echo off
REM Double-click this to build quill.exe. No terminal, no flags to remember.
REM
REM The published binary is deliberately not committed: it is ~70 MB, it would
REM live in git history forever, and it can't be diffed. Releases carry it
REM instead.

setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo.
    echo   The .NET 8 SDK is not installed.
    echo.
    echo   Install it with:  winget install --id Microsoft.DotNet.SDK.8
    echo   Or download from: https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

echo.
echo   Building quill.exe — this takes a minute the first time.
echo.

REM IncludeAllContentForSelfExtract is load-bearing: the obvious flag
REM (IncludeNativeLibrariesForSelfExtract) bundles whisper.cpp but leaves
REM AppContext.BaseDirectory pointing at the .exe, so every transcription dies
REM looking for a native library that was extracted somewhere else.
dotnet publish Quill.Win\Quill.Win.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeAllContentForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    --nologo ^
    -v quiet

if errorlevel 1 (
    echo.
    echo   Build failed. The output above says why.
    echo.
    pause
    exit /b 1
)

set "OUT=%~dp0Quill.Win\bin\Release\net8.0-windows\win-x64\publish\quill.exe"
if not exist "%OUT%" (
    echo.
    echo   Build reported success but quill.exe is missing:
    echo   %OUT%
    echo.
    pause
    exit /b 1
)

echo.
echo   Done.
echo.
echo   quill.exe is here:
echo   %OUT%
echo.
echo   Copy it somewhere permanent, then run it. It has no installer and no
echo   window — it lives in the notification area, next to the clock.
echo.
echo   Opening the folder...
explorer /select,"%OUT%"
echo.
pause
