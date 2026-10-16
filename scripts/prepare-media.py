"""Prepare pinned tools and a deterministic synthetic fixture; no third-party Python packages."""
import hashlib
import json
from pathlib import Path
import subprocess
import urllib.request
import zipfile

root = Path(__file__).resolve().parents[1]
pin = json.loads((root / 'scripts/media-tools.json').read_text())
target = root / 'tools/media'
target.mkdir(parents=True, exist_ok=True)
archive = target / 'download.zip'
if not archive.exists():
    urllib.request.urlretrieve(pin['url'], archive)
with archive.open('rb') as source:
    archive_hash = hashlib.file_digest(source, 'sha256').hexdigest()
if archive_hash != pin['sha256']:
    raise RuntimeError('Media archive checksum mismatch; refusing extraction')
with zipfile.ZipFile(archive) as bundle:
    for entry in bundle.infolist():
        name = Path(entry.filename).name
        if name in ('ffmpeg.exe', 'ffprobe.exe', 'LICENSE.txt', 'LICENSE'):
            if entry.file_size > 250 * 1024 * 1024:
                raise RuntimeError('Oversized archive member')
            (target / name).write_bytes(bundle.read(entry))
for name in ('ffmpeg', 'ffprobe'):
    result = subprocess.run([str(target / (name + '.exe')), '-version'], capture_output=True, text=True, timeout=15, check=True)
    config = result.stdout + result.stderr
    if '--enable-gpl' in config or '--enable-nonfree' in config:
        raise RuntimeError('Non-LGPL or unrecognized tool configuration')
    license_result = subprocess.run([str(target / (name + '.exe')), '-L'], capture_output=True, text=True, timeout=15, check=True)
    if 'GNU Lesser General Public License' not in license_result.stdout or 'version 3' not in license_result.stdout:
        raise RuntimeError('Unexpected runtime license')
    (target / (name + '-version.txt')).write_text(config)
fixture = root / 'TestFixtures/generated/sample.mkv'
fixture.parent.mkdir(parents=True, exist_ok=True)
subprocess.run([str(target / 'ffmpeg.exe'), '-nostdin', '-v', 'error', '-y',
                '-f', 'lavfi', '-i', 'testsrc=size=64x48:rate=5:duration=1',
                '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=8000:duration=1',
                '-map', '0:v', '-map', '1:a', '-c:v', 'ffv1', '-c:a', 'pcm_s16le',
                '-fflags', '+bitexact', '-flags:v', '+bitexact', '-flags:a', '+bitexact',
                '-map_metadata', '-1', str(fixture)], check=True, timeout=30)
print('Pinned LGPL configuration verified; fixture generated:', fixture)
for path in sorted(target.glob('*.exe')):
    print(path.name, hashlib.file_digest(path.open('rb'), 'sha256').hexdigest())
