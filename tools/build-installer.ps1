# Builds releases\MarkdownViewer-<version>-setup.msi from publish\MarkdownViewer.exe.
# Run after `dotnet publish -c Release -o publish` (tools/release.ps1 does both).
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$xml = Get-Content (Join-Path $root 'MarkdownViewer.csproj') -Raw
if ($xml -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw 'No <Version> in MarkdownViewer.csproj' }
$version = $Matches[1]

$exe = Join-Path $root 'publish\MarkdownViewer.exe'
if (-not (Test-Path $exe)) { throw "Missing $exe - publish first." }
$built = (Get-Item $exe).VersionInfo.ProductVersion
if ($built -ne $version) { throw "publish\MarkdownViewer.exe is $built but the project is $version - publish again." }

Push-Location $root
try {
    dotnet tool restore | Out-Null
    $releases = Join-Path $root 'releases'
    New-Item -ItemType Directory -Force $releases | Out-Null
    $msi = Join-Path $releases "MarkdownViewer-$version-setup.msi"
    dotnet wix build installer\Package.wxs -arch x64 -d "Version=$version" -d "PublishDir=$(Join-Path $root 'publish')" -o $msi | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    Remove-Item ([IO.Path]::ChangeExtension($msi, '.wixpdb')) -ErrorAction SilentlyContinue
    "Installer -> $msi"
}
finally { Pop-Location }
