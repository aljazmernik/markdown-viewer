# Runs all automated tests: the C# tests (tests/MarkdownViewer.Tests) and the
# JavaScript tests for web/text.js (tests/web, needs Node.js).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

dotnet test (Join-Path $root 'tests\MarkdownViewer.Tests') --nologo
if ($LASTEXITCODE -ne 0) { throw 'C# tests failed.' }

node --test (Get-ChildItem (Join-Path $root 'tests\web\*.test.mjs')).FullName
if ($LASTEXITCODE -ne 0) { throw 'JavaScript tests failed.' }
'All tests passed.'
