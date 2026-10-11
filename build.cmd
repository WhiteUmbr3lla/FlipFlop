@echo off
rem Builds FlipFlop.exe with the C# compiler that ships with .NET Framework 4.8 (no SDK needed).
rem Two passes: the first build draws the logo into flipflop.ico, the second embeds it as the exe icon.
cd /d "%~dp0"
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize /codepage:65001 /out:FlipFlop.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" ^
  /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll" ^
  /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll" ^
  /resource:words\en.txt,en.txt /resource:words\he.txt,he.txt
%CSC% *.cs || exit /b 1
"%~dp0FlipFlop.exe" --write-icon flipflop.ico || exit /b 1
%CSC% /win32icon:flipflop.ico *.cs
