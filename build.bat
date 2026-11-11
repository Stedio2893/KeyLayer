@echo off
rem Compila KeyLayer.exe con il compilatore C# incluso in Windows (.NET Framework 4.x): nessun SDK da installare.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Compilatore C# non trovato. Serve .NET Framework 4.x, presente di serie in Windows 10/11.
  exit /b 1
)
if not exist app.ico powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1
if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:dist\KeyLayer.exe /win32icon:app.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\*.cs
if errorlevel 1 (
  echo.
  echo Compilazione FALLITA.
  exit /b 1
)
echo.
echo OK: dist\KeyLayer.exe
