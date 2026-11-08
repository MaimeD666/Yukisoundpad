param([Parameter(Mandatory=$true)][Guid]$Endpoint)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$setup = Join-Path $root 'build\YukisPadSetup.exe'
if (!(Test-Path -LiteralPath $setup)) { throw 'Build the installer first.' }
if (Get-Process YukisPad -ErrorAction SilentlyContinue) { throw 'Close YukisPad before running the lifecycle test.' }
$arguments = '--test-lifecycle --endpoint=' + $Endpoint.ToString('B') + ' --quiet'
$process = Start-Process -FilePath $setup -Verb RunAs -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw 'Lifecycle test failed. See %ProgramData%\YukisPad\setup.log.' }
$report = Join-Path $env:ProgramData 'YukisPad\install-lifecycle-test.txt'
Copy-Item -LiteralPath $report -Destination (Join-Path $root 'build\install-lifecycle-test.txt') -Force
Get-Content -LiteralPath $report
