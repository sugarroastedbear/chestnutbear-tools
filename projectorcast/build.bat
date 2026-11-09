@echo off
rem Build with the .NET Framework 4 compiler that ships with Windows (no SDK needed)
rem Source must stay C# 5 compatible (no string interpolation, ?., nameof, => members).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 /out:ProjectorCast.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll ProjectorCast.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo OK: %~dp0ProjectorCast.exe
