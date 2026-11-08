$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $root '.tools\llvm-mingw-20261104-ucrt-x86_64\bin\x86_64-w64-mingw32-clang++.exe'
& $compiler '-std=c++17' '-O2' '-static' '-municode' '-DUNICODE' '-D_UNICODE' '-D_WIN32_WINNT=0x0A00' '-Wno-pragma-pack' '-Wno-dll-attribute-on-redeclaration' '-I' (Join-Path $root '.tools') (Join-Path $root 'native\Tests.cpp') '-o' (Join-Path $root 'build\NativeTests.exe') '-lole32' '-luuid' '-lpropsys' '-lshlwapi'
if($LASTEXITCODE -ne 0){throw 'Native tests compilation failed.'}
& (Join-Path $root 'build\NativeTests.exe') (Join-Path $root 'build\native-test-bridge.bin') | Tee-Object -FilePath (Join-Path $root 'build\native-tests.txt')
if($LASTEXITCODE -ne 0){throw 'Native tests failed.'}
