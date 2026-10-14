# Bumps the version in MarkdownViewer.csproj and builds the release files into releases\:
#   MarkdownViewer-<ver>.exe          portable, needs the .NET 9 Desktop Runtime (8 MB)
#   MarkdownViewer-<ver>-setup.msi    installer, self-contained (built by build-installer.ps1)
#   *.sha256                          checksums (the in-app updater verifies the MSI's)
#
#   .\tools\release.ps1            -> patch  (1.0.0 -> 1.0.1)
#   .\tools\release.ps1 minor      -> 1.0.1 -> 1.1.0
#   .\tools\release.ps1 major      -> 1.1.0 -> 2.0.0
#   .\tools\release.ps1 -Version 1.4.2   build exactly this version (used by CI for a pushed tag)
param([ValidateSet('patch', 'minor', 'major')][string]$Bump = 'patch', [string]$Version)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'MarkdownViewer.csproj'

$xml = Get-Content $proj -Raw
if ($xml -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { throw "No <Version>x.y.z</Version> in $proj" }
if (-not $Version) {
    $major, $minor, $patch = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]
    switch ($Bump) {
        'major' { $major++; $minor = 0; $patch = 0 }
        'minor' { $minor++; $patch = 0 }
        'patch' { $patch++ }
    }
    $Version = "$major.$minor.$patch"
}

$pub = Join-Path $root 'publish'
dotnet publish $proj -c Release -o $pub -p:Version=$Version | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed; version left unchanged." }

# Only record the new version once the build succeeded.
$xml = $xml -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$Version</Version>"
[IO.File]::WriteAllText($proj, $xml)

$releases = Join-Path $root 'releases'
New-Item -ItemType Directory -Force $releases | Out-Null
$exe = Join-Path $releases "MarkdownViewer-$Version.exe"
Copy-Item (Join-Path $pub 'MarkdownViewer.exe') $exe -Force
& (Join-Path $PSScriptRoot 'sign.ps1') $exe
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$exe.sha256", "$hash  $(Split-Path $exe -Leaf)`n")

& (Join-Path $PSScriptRoot 'build-installer.ps1') -Version $Version
"Released $Version -> $releases"
