#Requires -Version 7.4
# Publishes the Promin apps into artifacts\publish and builds their setups into artifacts\setup, for copying to a
# USB stick. Needs the .NET SDK and Inno Setup 6.

# The release workflow passes the version from the tag.
param([string]$Version = '0.1.0')

$ErrorActionPreference = 'Stop'
# A native command that exits non-zero stops the script too, the same as a failing cmdlet.
$PSNativeCommandUseErrorActionPreference = $true

$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish'
$setup = Join-Path $root 'artifacts\setup'

$iscc = Get-ChildItem "$env:ProgramFiles\Inno Setup*\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup*\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup*\ISCC.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup is not installed: winget install JRSoftware.InnoSetup' }

# A setup takes every file in its publish folder, so files left from an earlier build must not survive.
foreach ($dir in $publish, $setup) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

dotnet publish "$root\src\Promin.Agent" -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o "$publish\agent"
# The .NET SDK leaves a WinUI app's .pri, which holds its compiled XAML, out of a publish; only the Windows App SDK's
# MSIX tooling adds it. The layouts stay unpackaged.
dotnet publish "$root\src\Promin.Agent.Window" -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:EnableMsixTooling=true -o "$publish\agent-window"
dotnet publish "$root\src\Promin.Master" -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:EnableMsixTooling=true -o "$publish\console"

& $iscc "/DAppVersion=$Version" "/O$setup" "$PSScriptRoot\agent.iss"
& $iscc "/DAppVersion=$Version" "/O$setup" "$PSScriptRoot\console.iss"
