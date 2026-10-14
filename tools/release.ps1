# Bumps the version in MarkdownViewer.csproj, publishes, and puts the exe and the
# installer (built by build-installer.ps1) in releases\.
#   .\tools\release.ps1            -> patch  (1.0.0 -> 1.0.1)
#   .\tools\release.ps1 minor      -> 1.0.1 -> 1.1.0
#   .\tools\release.ps1 major      -> 1.1.0 -> 2.0.0
param([ValidateSet('patch', 'minor', 'major')][string]$Bump = 'patch')
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'MarkdownViewer.csproj'

$xml = Get-Content $proj -Raw
if ($xml -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { throw "No <Version>x.y.z</Version> in $proj" }
$major, $minor, $patch = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]
switch ($Bump) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}
$version = "$major.$minor.$patch"

dotnet publish $proj -c Release -o (Join-Path $root 'publish') -p:Version=$version | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed; version left unchanged." }

# Only record the new version once the build succeeded.
$xml = $xml -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$version</Version>"
[IO.File]::WriteAllText($proj, $xml)

$releases = Join-Path $root 'releases'
New-Item -ItemType Directory -Force $releases | Out-Null
$target = Join-Path $releases "MarkdownViewer-$version.exe"
Copy-Item (Join-Path $root 'publish\MarkdownViewer.exe') $target -Force
& (Join-Path $PSScriptRoot 'build-installer.ps1')
"Released $version -> $target"
