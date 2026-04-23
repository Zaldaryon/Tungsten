@echo off
chcp 65001 >nul

echo Building Tungsten mod without obfuscation...
set "BUILD_TYPE=DEOBFUSCATED"
for /f "delims=" %%v in ('powershell -NoProfile -Command "[Console]::Out.Write((Get-Content \"modinfo.json\" -Raw | ConvertFrom-Json).version)"') do set "MOD_VERSION=%%v"
if "%MOD_VERSION%"=="" (
  echo Could not read version from modinfo.json
  exit /b 1
)
set "VERSIONED_ZIP=Tungsten-%MOD_VERSION%.zip"

REM Clean previous builds
if exist bin rmdir /s /q bin

REM Build the project and capture output
dotnet build Tungsten.csproj --configuration Release --verbosity quiet > build_output.txt 2>&1
set BUILD_EXIT=%ERRORLEVEL%

REM Show only warnings and errors
findstr /C:"warning" /C:"error" /C:"Error" /C:"Warning" build_output.txt
del build_output.txt

if %BUILD_EXIT% EQU 0 (
    echo Build successful! [%BUILD_TYPE%]
    if exist "bin\Tungsten.zip" (
        ren "bin\Tungsten.zip" "%VERSIONED_ZIP%"
        REM Remove old Tungsten versions from Mods folder
        del "%APPDATA%\VintagestoryData\Mods\Tungsten*.zip" >nul 2>&1
        
        REM Copy new version to VintagestoryData Mods folder
        copy "bin\%VERSIONED_ZIP%" "%APPDATA%\VintagestoryData\Mods\" >nul
        echo Mod packaged successfully: %VERSIONED_ZIP% [%BUILD_TYPE%]
        echo Saved to: %APPDATA%\VintagestoryData\Mods\%VERSIONED_ZIP%
    ) else (
        echo Warning: Zip package not found
        exit /b 1
    )
) else (
    echo Build failed!
    exit /b 1
)
