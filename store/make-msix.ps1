# Lays out the Store package in msix\ from a built FlipFlop.exe and packs dist\FlipFlop-<version>.msix (unsigned:
# the Store signs it). Needs makeappx.exe from the Windows SDK. -LayoutOnly skips packing, for
# Add-AppxPackage -Register msix\AppxManifest.xml (Developer Mode) when testing locally.
param([switch]$LayoutOnly)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
$v = (Get-Item FlipFlop.exe).VersionInfo.FileVersion + '.0'

Remove-Item msix -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory msix\Assets | Out-Null
Copy-Item FlipFlop.exe msix\
(Get-Content store\AppxManifest.xml -Raw) -replace 'Version="0.0.0.0"', "Version=""$v""" | Set-Content msix\AppxManifest.xml -Encoding UTF8
foreach ($a in @(@('Square44x44Logo', 44, 44), @('Square150x150Logo', 150, 150), @('Wide310x150Logo', 310, 150), @('StoreLogo', 50, 50))) {
    & .\FlipFlop.exe --write-png "msix\Assets\$($a[0]).png" $a[1] $a[2] | Out-Null
    if (!(Test-Path "msix\Assets\$($a[0]).png")) { throw "logo $($a[0]) not written" }
}
if ($LayoutOnly) { return }

$makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe' | Sort-Object FullName | Select-Object -Last 1
if (!$makeappx) { throw 'makeappx.exe not found: install the Windows SDK' }
New-Item -ItemType Directory dist -Force | Out-Null
& $makeappx.FullName pack /o /d msix /p "dist\FlipFlop-$($v -replace '\.0$').msix"
if ($LASTEXITCODE) { throw "makeappx failed ($LASTEXITCODE)" }
