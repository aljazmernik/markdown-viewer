# Builds releases\MarkdownViewer-<version>-setup.msi (+ .sha256) from a self-contained publish.
#   .\tools\build-installer.ps1             publishes publish-sc\ for the project version, then builds the MSI
#   .\tools\build-installer.ps1 -NoPublish  reuses an existing publish-sc\
# If CODESIGN_PFX_PATH (and CODESIGN_PFX_PASSWORD) are set, the exe and the MSI are signed.
param([switch]$NoPublish, [string]$Version)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $xml = Get-Content (Join-Path $root 'MarkdownViewer.csproj') -Raw
    if ($xml -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw 'No <Version> in MarkdownViewer.csproj' }
    $Version = $Matches[1]
}

$pub = Join-Path $root 'publish-sc'
if (-not $NoPublish) {
    if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
    # Single exe with the .NET runtime inside; native libraries (WebView2Loader.dll) stay next to it,
    # so nothing is extracted at startup.
    dotnet publish (Join-Path $root 'MarkdownViewer.csproj') -c Release -o $pub --self-contained true `
        -p:IncludeNativeLibrariesForSelfExtract=false -p:Version=$Version | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    Copy-Item (Join-Path $pub 'runtimes\win-x64\native\WebView2Loader.dll') $pub -Force
}

$exe = Join-Path $pub 'MarkdownViewer.exe'
$built = (Get-Item $exe).VersionInfo.ProductVersion
if ($built -ne $Version) { throw "publish-sc\MarkdownViewer.exe is $built, expected $Version." }

& (Join-Path $PSScriptRoot 'sign.ps1') $exe

Push-Location $root
try {
    dotnet tool restore | Out-Null
    $releases = Join-Path $root 'releases'
    New-Item -ItemType Directory -Force $releases | Out-Null
    $msi = Join-Path $releases "MarkdownViewer-$Version-setup.msi"
    dotnet wix build installer\Package.wxs -arch x64 -d "Version=$Version" -d "PublishDir=$pub" -o $msi | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    Remove-Item ([IO.Path]::ChangeExtension($msi, '.wixpdb')) -ErrorAction SilentlyContinue

    & (Join-Path $PSScriptRoot 'sign.ps1') $msi

    # Checksum the in-app updater verifies before installing.
    $hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$msi.sha256", "$hash  $(Split-Path $msi -Leaf)`n")
    "Installer -> $msi"
}
finally { Pop-Location }
