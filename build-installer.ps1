# ThermalWrong by soybean_alien
# https://github.com/soybean-alien/ThermalWrong · MIT License
#
# Publishes the app self-contained (no .NET install needed) and compiles the Inno Setup installer
# into artifacts\installer. Needs the .NET 8 SDK and Inno Setup 6.4+.
#   powershell -File build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\ThermalWrong.App\ThermalWrong.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:SatelliteResourceLanguages=en -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found.' }
& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\ThermalWrong.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
Get-ChildItem (Join-Path $root 'artifacts\installer') -Filter *.exe | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
