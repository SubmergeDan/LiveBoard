@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if not exist "%MSBUILD%" (
    echo ERROR: .NET Framework MSBuild was not found.
    echo Install Visual Studio Build Tools with .NET desktop build tools.
    exit /b 1
)

if not exist "dist" mkdir "dist"
if exist "dist\LiveBoard.exe" del /q "dist\LiveBoard.exe"
if exist "dist\LiveBoard.pdb" del /q "dist\LiveBoard.pdb"

"%MSBUILD%" "LiveBoard.csproj" /t:Build /p:Configuration=Release /p:Platform=AnyCPU /p:OutputPath=dist\ /p:IntermediateOutputPath=obj\ReleaseBuild\ /v:minimal
if errorlevel 1 (
    echo.
    echo ERROR: Build failed. Install the .NET Framework 4.8 Targeting Pack if it is missing.
    exit /b %errorlevel%
)

if exist "dist\LiveBoard.pdb" del /q "dist\LiveBoard.pdb"
if not exist "dist\LiveBoard.exe" (
    echo ERROR: Build completed without LiveBoard.exe.
    exit /b 1
)

echo.
echo Build succeeded: "%CD%\dist\LiveBoard.exe"
exit /b 0

