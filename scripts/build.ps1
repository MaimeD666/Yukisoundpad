$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler is missing. Install .NET Framework 4.8.' }
$destination = Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Xaml.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$compilerArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', "/out:$destination\YukisPad.exe", "/win32manifest:$root\src\app.manifest")
foreach ($reference in $references) { $compilerArgs += '/reference:' + (Join-Path $framework $reference) }
foreach ($resource in (Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.xaml')) { $compilerArgs += '/resource:' + $resource.FullName + ',YukisPad.' + $resource.Name }
if (Test-Path -LiteralPath "$root\src\app.ico") { $compilerArgs += "/win32icon:$root\src\app.ico" }
$compilerArgs += (Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs').FullName
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath "$root\src\YukisPad.exe.config" -Destination $destination -Force
Write-Output "Built: $destination\YukisPad.exe"
