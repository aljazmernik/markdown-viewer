# Authenticode-signs a file when a code-signing certificate is configured; otherwise does nothing.
#   CODESIGN_PFX_PATH      path to the .pfx certificate
#   CODESIGN_PFX_PASSWORD  its password
# Uses signtool.exe from the Windows SDK (preinstalled on GitHub's Windows runners).
param([Parameter(Mandatory)][string]$File)
$ErrorActionPreference = 'Stop'

$pfx = $env:CODESIGN_PFX_PATH
if (-not $pfx) { "Not signing $(Split-Path $File -Leaf) (no CODESIGN_PFX_PATH)."; return }

$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $signtool) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK).' }

& $signtool sign /f $pfx /p $env:CODESIGN_PFX_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /d 'Markdown Viewer' $File
if ($LASTEXITCODE -ne 0) { throw "Signing $File failed." }
