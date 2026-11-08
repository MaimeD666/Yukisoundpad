$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
foreach($check in @(@('build\test-results\latest.txt','RESULT PASS'),@('build\native-tests.txt','RESULT PASS'),@('build\smoke-result.txt','PASS:'),@('build\install-lifecycle-test.txt','PASS: clean reinstall'))){
 $path=Join-Path $root $check[0]
 if(!(Test-Path -LiteralPath $path) -or !(Select-String -LiteralPath $path -SimpleMatch $check[1] -Quiet)){throw ('Required check missing: '+$path)}
}
$route=Join-Path $env:LOCALAPPDATA 'YukisPad\route-test\result.txt'
if(!(Test-Path $route) -or !(Select-String -LiteralPath $route -SimpleMatch 'PASS:' -Quiet)){throw 'Microphone route test has not passed.'}
foreach($name in @('YukisPad.exe','YukiMicFx.dll')){
 $built=Join-Path $root ('build\'+$name)
 $installed=Join-Path $env:ProgramFiles ('YukisPad\'+$name)
 if((Get-FileHash $built).Hash-ne (Get-FileHash $installed).Hash){throw ('Installed build differs: '+$name)}
}
$release=Join-Path $root 'release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'build\YukisPadSetup.exe') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $release -Force
Compress-Archive -LiteralPath (Join-Path $release 'YukisPadSetup.exe'),(Join-Path $release 'README.md') -DestinationPath (Join-Path $release 'YukisPad-0.2.1.zip') -Force
# Refresh the previous local preview location without touching any user data.
$preview=Join-Path $release 'YukisPad'
New-Item -ItemType Directory -Path $preview -Force | Out-Null
foreach($name in @('YukisPad.exe','YukisPad.exe.config','YukisPadSetup.exe')){Copy-Item -LiteralPath (Join-Path $root ('build\'+$name)) -Destination $preview -Force}
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $preview -Force
$presets=Join-Path $preview 'starter-sounds'
New-Item -ItemType Directory -Path $presets -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'starter-sounds') -File | Copy-Item -Destination $presets -Force
$oldZip=Join-Path $release 'YukisPad-portable.zip'
if(Test-Path -LiteralPath $oldZip){Remove-Item -LiteralPath $oldZip}
$hashes=Get-FileHash -LiteralPath (Join-Path $release 'YukisPadSetup.exe'),(Join-Path $release 'YukisPad-0.2.1.zip') -Algorithm SHA256
$hashes | ForEach-Object {$_.Hash+'  '+[IO.Path]::GetFileName($_.Path)} | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ASCII
$hashes | Format-Table Hash,Path
