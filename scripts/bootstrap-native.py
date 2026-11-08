"""Prepare pinned native build tools locally; no installation or PATH changes."""
from pathlib import Path
import hashlib, urllib.request, zipfile
ROOT = Path(__file__).resolve().parents[1]
DEST = ROOT / '.tools'
DEST.mkdir(exist_ok=True)
VERSION = '20261104'
ARCHIVE = f'llvm-mingw-{VERSION}-ucrt-x86_64.zip'
SHA = '552191b5754c49a5a20723276a6fc56541ea1922c4c50b7b669022333fc1b40f'
COMMIT = '6d6617a8eee57d7c4f6c55d8c23e35064ea69b9e'
HEADERS = {
 'audioenginebaseapo.h':'1859398a91a4498f23ad1e06755f9520138308268f4ac4927a3526c3de1d6382',
 'audiomediatype.h':'20d550d6fe21fed26bee3e1313791b859984cef0bd7d1a143a1acf92fa9481cc',
 'AudioAPOTypes.h':'a93f40f7e672974fd868598c3ab6c19b88bba7a08dd14b3f3af8ae13046865e3',
}
def download(url,path,digest):
 if path.exists() and hashlib.sha256(path.read_bytes()).hexdigest()==digest:return
 temp=path.with_suffix(path.suffix+'.download')
 urllib.request.urlretrieve(url,temp)
 if hashlib.sha256(temp.read_bytes()).hexdigest()!=digest:
  temp.unlink();raise RuntimeError('Checksum mismatch: '+url)
 temp.replace(path)
compiler=DEST/f'llvm-mingw-{VERSION}-ucrt-x86_64/bin/x86_64-w64-mingw32-clang++.exe'
if not compiler.exists():
 archive=DEST/'llvm-mingw.zip'
 download(f'https://github.com/mstorsjo/llvm-mingw/releases/download/{VERSION}/{ARCHIVE}',archive,SHA)
 with zipfile.ZipFile(archive) as z:
  for item in z.infolist():
   target=(DEST/item.filename).resolve()
   if not target.is_relative_to(DEST.resolve()):raise RuntimeError('Invalid archive path')
  z.extractall(DEST)
for name,digest in HEADERS.items():
 download(f'https://raw.githubusercontent.com/microsoft/win32metadata/{COMMIT}/generation/WinSDK/RecompiledIdlHeaders/um/{name}',DEST/name,digest)
print('Native compiler and SDK headers ready in .tools')
