$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $root '.tools\llvm-mingw-20261104-ucrt-x86_64\bin\x86_64-w64-mingw32-clang++.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Run scripts/bootstrap-native.py to prepare the compiler.' }
$out = Join-Path $root 'build'
New-Item -ItemType Directory -Path $out -Force | Out-Null
& $compiler '-std=c++17' '-O2' '-shared' '-static' '-fno-exceptions' '-fno-rtti' '-DUNICODE' '-D_UNICODE' '-D_WIN32_WINNT=0x0A00' '-Wno-pragma-pack' '-Wno-dll-attribute-on-redeclaration' '-I' (Join-Path $root '.tools') (Join-Path $root 'native\YukiMicFx.cpp') '-o' (Join-Path $out 'YukiMicFx.dll') '-lole32' '-luuid' '-lpropsys'
if ($LASTEXITCODE -ne 0) { throw 'Native compilation failed.' }
Write-Output 'Built YukiMicFx.dll'
