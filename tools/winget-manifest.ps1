# Writes winget manifests for a published release into releases\winget\<version>\.
# Submit them to https://github.com/microsoft/winget-pkgs (see README, "Publishing to winget").
#   .\tools\winget-manifest.ps1 1.3.0
# Needs releases\MarkdownViewer-<version>-setup.msi: the same file that is attached to the GitHub
# release (download it from the release page if it was built by GitHub Actions).
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$msi = Join-Path $root "releases\MarkdownViewer-$Version-setup.msi"
if (-not (Test-Path $msi)) { throw "Missing $msi" }

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($msi, 0)
$view = $db.OpenView("SELECT Value FROM Property WHERE Property = 'ProductCode'")
$view.Execute()
$productCode = $view.Fetch().StringData(1)
$view.Close()

$id = 'AljazMernik.MarkdownViewer'
$sha = (Get-FileHash $msi -Algorithm SHA256).Hash
$url = "https://github.com/aljazmernik/markdown-viewer/releases/download/v$Version/MarkdownViewer-$Version-setup.msi"
$out = Join-Path $root "releases\winget\$Version"
New-Item -ItemType Directory -Force $out | Out-Null

$files = @{
    "$id.yaml"                = @"
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@
    "$id.installer.yaml"      = @"
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: msi
Scope: user
InstallModes:
- silent
- silentWithProgress
UpgradeBehavior: install
ProductCode: '$productCode'
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
"@
    "$id.locale.en-US.yaml"   = @"
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Aljaz Mernik
PublisherUrl: https://github.com/aljazmernik
PackageName: Markdown Viewer
PackageUrl: https://github.com/aljazmernik/markdown-viewer
License: MIT
LicenseUrl: https://github.com/aljazmernik/markdown-viewer/blob/main/LICENSE
ShortDescription: Fast Markdown viewer and editor for Windows, with tabs, live preview, and visual editing.
Tags:
- markdown
- viewer
- editor
- md
ReleaseNotesUrl: https://github.com/aljazmernik/markdown-viewer/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@
}
foreach ($name in $files.Keys) {
    [IO.File]::WriteAllText((Join-Path $out $name), $files[$name].Replace("`r`n", "`n") + "`n")
}
"winget manifests -> $out"
