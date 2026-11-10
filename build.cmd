@echo off
rem Rebuilds ZoneSnap.exe from ZoneSnap.cs using the C# compiler built into Windows, then restarts it.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find the .NET Framework C# compiler.
  pause
  exit /b 1
)

taskkill /im ZoneSnap.exe /f >nul 2>&1

"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0ZoneSnap.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0ZoneSnap.cs"
if errorlevel 1 (
  echo.
  echo Build FAILED - see the errors above.
  if not "%~1"=="nopause" pause
  exit /b 1
)

echo Build OK.
if not "%~1"=="norun" start "" "%~dp0ZoneSnap.exe"
