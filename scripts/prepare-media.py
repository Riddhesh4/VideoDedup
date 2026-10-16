"""Prepare pinned tools and a deterministic synthetic fixture; no third-party Python packages."""
import hashlib
import json
import math
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
                '-f', 'lavfi', '-i', 'testsrc=size=64x48:rate=5:duration=8',
                '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=44100:duration=8',
                '-map', '0:v', '-map', '1:a', '-c:v', 'ffv1', '-c:a', 'pcm_s16le',
                '-fflags', '+bitexact', '-flags:v', '+bitexact', '-flags:a', '+bitexact',
                '-map_metadata', '-1', str(fixture)], check=True, timeout=30)
# Small generated motion corpus: no downloaded/copyrighted media and no Python dependencies.
ffmpeg = str(target / 'ffmpeg.exe')
output = fixture.parent

def generate(name, variant=0, static=False):
    frames = bytearray()
    for frame in range(32):
        t = 0 if static else frame / 8
        for y in range(96):
            for x in range(128):
                if variant == 0:
                    value = 95 + 38 * math.sin(x / 15 + t * 2) + 35 * math.cos(y / 11 - t)
                    if (x - (28 + t * 19)) ** 2 + (y - (44 + 18 * math.sin(t))) ** 2 < 225:
                        value = 235
                    if 85 - t * 9 < x < 108 - t * 9 and 15 < y < 35:
                        value = 20
                else:
                    value = 100 + 55 * math.sin((x + y) / 9 - t * 3) + 45 * math.cos((x - y) / 21 + t)
                    if 15 < x < 43 and 12 + t * 13 < y < 31 + t * 13:
                        value = 240
                frames.append(round(max(0, min(255, value))))
    subprocess.run([ffmpeg, '-nostdin', '-v', 'error', '-y', '-f', 'rawvideo', '-pix_fmt', 'gray',
                    '-s', '128x96', '-r', '8', '-i', 'pipe:0', '-c:v', 'ffv1', '-threads', '1',
                    str(output / name)], input=bytes(frames), check=True, timeout=30)

def convert(name, args, source='visual-original.mkv'):
    subprocess.run([ffmpeg, '-nostdin', '-v', 'error', '-y', '-i', str(output / source),
                    *args, '-threads', '1', str(output / name)], check=True, timeout=30)

generate('visual-original.mkv')
generate('visual-unrelated.mkv', variant=1)
generate('visual-static.mkv', static=True)
subprocess.run([ffmpeg, '-nostdin', '-v', 'error', '-y', '-f', 'lavfi', '-i',
                'sine=frequency=440:sample_rate=44100:duration=8', '-c:a', 'pcm_s16le',
                str(output / 'audio-sine.wav')], check=True, timeout=30)
subprocess.run([ffmpeg, '-nostdin', '-v', 'error', '-y', '-i', str(output / 'audio-sine.wav'),
                '-c:a', 'aac', '-b:a', '128k', str(output / 'audio-sine-aac.m4a')], check=True, timeout=30)
convert('visual-resized.mp4', ['-vf', 'scale=64:48', '-c:v', 'mpeg4', '-q:v', '5', '-pix_fmt', 'yuv420p'])
convert('visual-letterboxed.mp4', ['-vf', 'pad=128:96:0:0:black', '-c:v', 'mpeg4', '-q:v', '5', '-pix_fmt', 'yuv420p'])
convert('visual-10bit.mkv', ['-pix_fmt', 'yuv420p10le', '-color_primaries', 'bt2020', '-color_trc', 'smpte2084', '-colorspace', 'bt2020nc', '-c:v', 'ffv1'])
convert('visual-vfr.mkv', ['-vf', 'select=not(mod(n\\,3))+not(mod(n\\,5))', '-fps_mode', 'vfr', '-c:v', 'ffv1'])
convert('visual-short.mkv', ['-frames:v', '1', '-c:v', 'ffv1'])
convert('visual-black.mkv', ['-vf', 'lut=y=0', '-c:v', 'ffv1'])
convert('visual-rotated.mp4', ['-c', 'copy', '-metadata:s:v:0', 'rotate=90'], source='visual-resized.mp4')
print('Pinned LGPL configuration verified; synthetic visual corpus generated:', output)
for path in sorted(target.glob('*.exe')):
    print(path.name, hashlib.file_digest(path.open('rb'), 'sha256').hexdigest())

# Verify the official fpcalc package; keep the complete extracted release tree together.
audio_pin = json.loads((root / 'scripts/audio-tool.json').read_text())
audio_target = root / 'tools/audio'
audio_target.mkdir(parents=True, exist_ok=True)
audio_archive = audio_target / 'download.zip'
if not audio_archive.exists():
    urllib.request.urlretrieve(audio_pin['url'], audio_archive)
with audio_archive.open('rb') as source:
    audio_hash = hashlib.file_digest(source, 'sha256').hexdigest()
if audio_hash != audio_pin['sha256']:
    raise RuntimeError('fpcalc archive checksum mismatch; refusing extraction')
with zipfile.ZipFile(audio_archive) as bundle:
    members = [entry for entry in bundle.infolist() if not entry.is_dir()]
    if not members or any(Path(entry.filename).name != 'fpcalc.exe' for entry in members):
        raise RuntimeError('Unexpected fpcalc archive contents')
    for entry in members:
        if entry.file_size > 64 * 1024 * 1024:
            raise RuntimeError('Oversized fpcalc archive member')
        destination = audio_target / entry.filename
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(bundle.read(entry))
fpcalc = next(audio_target.rglob('fpcalc.exe'))
version = subprocess.run([str(fpcalc), '-version'], capture_output=True, text=True, timeout=15, check=True)
if not version.stdout.strip().startswith(f"fpcalc version {audio_pin['version']} "):
    raise RuntimeError('Unexpected fpcalc version')
print('Pinned fpcalc archive verified:', audio_hash)
print(fpcalc.relative_to(audio_target), hashlib.file_digest(fpcalc.open('rb'), 'sha256').hexdigest())
