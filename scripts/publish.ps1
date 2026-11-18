<#
.SYNOPSIS
    Builds NotchPC for other people: an installer (NotchPC-<version>-Setup.exe) and a portable zip, in .\publish.

.DESCRIPTION
    By default the app is self-contained (it carries its own .NET), so the people you give it to install nothing
    else. The installer itself runs on .NET Framework 4.7.2, which Windows 10/11 already have, and installs for the
    current user only (no administrator rights). -FrameworkDependent makes a much smaller build that needs the
    .NET 10 Desktop Runtime and ASP.NET Core Runtime installed (fine for this PC, not for friends).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#>
param(
    [switch]$FrameworkDependent,
    [string]$Output = "publish"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$outRoot = Join-Path $root $Output
$app = Join-Path $outRoot "NotchPC"
$sc = if ($FrameworkDependent) { "false" } else { "true" }
$version = (Select-Xml -Path (Join-Path $root "Directory.Build.props") -XPath "//Version").Node.InnerText

New-Item -ItemType Directory -Force $outRoot | Out-Null
if (Test-Path $app) { Remove-Item $app -Recurse -Force }

Write-Host "1/4 NotchPC (self-contained: $sc) ..."
dotnet publish (Join-Path $root "src\NotchPC.App\NotchPC.App.csproj") -c Release -r win-x64 --self-contained $sc -o $app -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (app) failed" }

Write-Host "2/4 Sensors helper ..."
# One compressed file: a second copy of the .NET runtime as loose files would add ~70 MB.
$sensors = Join-Path $app "sensors"
if (Test-Path $sensors) { Remove-Item $sensors -Recurse -Force }
dotnet publish (Join-Path $root "src\NotchPC.Sensors\NotchPC.Sensors.csproj") -c Release -r win-x64 --self-contained $sc -o $sensors -nologo -v q `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=$sc -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (sensors) failed" }

# License notices of the bundled libraries (required when handing the program to anyone).
$licenses = Join-Path $app "licenses"
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.md") $app
$nuget = Join-Path $env:USERPROFILE ".nuget\packages"
foreach ($pkg in @("wpf-ui\4.3.0", "microsoft.web.webview2\1.0.2903.40", "hidsharp\2.6.4")) {
    Get-ChildItem (Join-Path $nuget $pkg) -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(LICENSE|NOTICE|ThirdPartyNotices)' } |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $licenses (($pkg -split '\\')[0] + "-" + $_.Name)) }
}
Get-ChildItem $app -Recurse -Filter *.pdb | Remove-Item -Force

Write-Host "3/4 Portable zip ..."
$suffix = if ($FrameworkDependent) { "-needs-dotnet" } else { "" }
$zip = Join-Path $outRoot "NotchPC-$version-win-x64-portable$suffix.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $app -DestinationPath $zip -CompressionLevel Optimal

Write-Host "4/4 Installer ..."
$setupBuild = Join-Path $outRoot "setup-build"
dotnet build (Join-Path $root "src\NotchPC.Setup\NotchPC.Setup.csproj") -c Release -o $setupBuild -nologo -v q "-p:Payload=$zip"
if ($LASTEXITCODE -ne 0) { throw "dotnet build (setup) failed" }
$setup = Join-Path $outRoot "NotchPC-$version-Setup$suffix.exe"
Copy-Item (Join-Path $setupBuild "NotchPC-Setup.exe") $setup -Force
Remove-Item $setupBuild -Recurse -Force

$size = [math]::Round(((Get-ChildItem $app -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ""
Write-Host "Program folder: $app ($size MB)"
Write-Host "Installer:      $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
Write-Host "Portable zip:   $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
