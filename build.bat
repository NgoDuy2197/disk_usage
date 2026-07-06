@echo off
setlocal
cd /d "%~dp0"

rem ===== Tim csc.exe co san trong .NET Framework (khong can cai SDK) =====
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [LOI] Khong tim thay csc.exe - can .NET Framework 4.x co san tren Windows 10/11.
    exit /b 1
)

echo Dang bien dich DiskUsage.exe ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
    /win32manifest:app.manifest ^
    /out:DiskUsage.exe Program.cs

if errorlevel 1 (
    echo [LOI] Bien dich that bai.
    exit /b 1
)

echo [OK] Da tao DiskUsage.exe thanh cong.
if /i not "%~1"=="nopause" pause
