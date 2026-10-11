@echo off
rem Rebuilds FlipFlop, runs the self-test and zips the exe with package\README.txt into dist\FlipFlop-<version>.zip.
cd /d "%~dp0"
call "%~dp0build.cmd" || exit /b 1
"%~dp0FlipFlop.exe" --selftest > nul || (echo Self-test failed, not packaging. & exit /b 1)
if not exist dist mkdir dist
powershell -NoProfile -Command "$v = (Get-Item FlipFlop.exe).VersionInfo.FileVersion; Compress-Archive -Force -Path FlipFlop.exe, package\README.txt -DestinationPath dist\FlipFlop-$v.zip; 'Packaged dist\FlipFlop-' + $v + '.zip'" || exit /b 1
