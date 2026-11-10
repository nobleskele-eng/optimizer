@echo off
setlocal
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "REFS=-r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll"
"%CSC%" -nologo -target:winexe -platform:anycpu %REFS% -out:"%~dp0optimizer.exe" "%~dp0src\Optimizer.cs"
if errorlevel 1 echo BUILD FAILED: optimizer.exe
if not errorlevel 1 echo Built "%~dp0optimizer.exe"
"%CSC%" -nologo -target:winexe -platform:anycpu %REFS% -out:"%~dp0stripper.exe" "%~dp0src\Stripper.cs"
if errorlevel 1 echo BUILD FAILED: stripper.exe
if not errorlevel 1 echo Built "%~dp0stripper.exe"
echo.
pause
