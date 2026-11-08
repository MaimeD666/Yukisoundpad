$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1')
$app = Join-Path $root 'build\YukisPad.exe'
$test = Start-Process -FilePath $app -ArgumentList '--self-test' -WindowStyle Hidden -PassThru -Wait
Get-Content -LiteralPath (Join-Path $root 'build\test-results\latest.txt') -Encoding UTF8
if ($test.ExitCode -ne 0) { throw 'Self-tests failed.' }
$smoke = Start-Process -FilePath $app -ArgumentList '--smoke' -WindowStyle Hidden -PassThru -Wait
$result = Get-Content -LiteralPath (Join-Path $root 'build\smoke-result.txt') -Encoding UTF8
Write-Output $result
if ($smoke.ExitCode -ne 0 -or $result -notlike 'PASS:*') { throw 'UI smoke test failed.' }
