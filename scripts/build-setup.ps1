$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'build'
$compiler = Join-Path $root '.tools\llvm-mingw-20261104-ucrt-x86_64\bin\x86_64-w64-mingw32-clang++.exe'
& $compiler '-std=c++17' '-O2' '-static' '-municode' '-DUNICODE' '-D_UNICODE' '-D_WIN32_WINNT=0x0A00' (Join-Path $root 'native\Probe.cpp') '-o' (Join-Path $out 'MicProbe.exe') '-lole32' '-luuid'
if ($LASTEXITCODE -ne 0) { throw 'Microphone probe compilation failed.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$payload = Join-Path $out 'payload.zip'
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload }
$zip = [IO.Compression.ZipFile]::Open($payload, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in @('YukisPad.exe','YukisPad.exe.config','YukiMicFx.dll','MicProbe.exe')) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $out $name), $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null }
    if (Test-Path -LiteralPath (Join-Path $root 'starter-sounds')) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'starter-sounds') -File) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,('starter-sounds/'+$file.Name),[IO.Compression.CompressionLevel]::Optimal) | Out-Null }
    }
} finally { $zip.Dispose() }
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xaml.dll','System.Windows.Forms.dll','System.Drawing.dll','System.ServiceProcess.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')
$argsList = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001','/main:YukisPad.SetupProgram',"/out:$out\YukisPadSetup.exe", "/win32manifest:$root\installer\setup.manifest", "/win32icon:$root\src\app.ico", "/resource:$payload,YukisPad.Payload.zip")
foreach ($reference in $references) { $argsList += '/reference:' + (Join-Path $framework $reference) }
foreach ($resource in Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.xaml') { $argsList += '/resource:' + $resource.FullName + ',YukisPad.' + $resource.Name }
$argsList += (Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs').FullName
$argsList += Join-Path $root 'installer\Setup.cs'
& (Join-Path $framework 'csc.exe') @argsList
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
Write-Output "Built: $out\YukisPadSetup.exe"
