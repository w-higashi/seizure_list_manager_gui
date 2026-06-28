@echo off
chcp 65001 >nul

echo ================================================
echo   Build - seizure_list_manager.exe
echo ================================================
echo.

set FWDIR=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set CSC=%FWDIR%\csc.exe
set WPFDIR=%FWDIR%\WPF
set SRC=seizure_list_manager.cs
set OUT=seizure_list_manager.exe
set ICO=seizure_list_manager.ico

if not exist "%CSC%" (
    echo Error: csc.exe not found.
    echo   %CSC%
    echo.
    pause
    exit /b 1
)

if not exist "%SRC%" (
    echo Error: source file not found.
    echo   %SRC%
    echo.
    pause
    exit /b 1
)

echo Compiling...
echo.

"%CSC%" /nologo /target:winexe /utf8output /win32icon:"%ICO%" /out:"%OUT%" /lib:"%WPFDIR%" /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll /r:System.dll /r:System.Core.dll /r:System.Xml.dll "%SRC%"

if %errorlevel% neq 0 (
    echo.
    echo Build failed.
    echo.
    pause
    exit /b 1
)

echo.
echo Build succeeded: %OUT%
echo.
pause
