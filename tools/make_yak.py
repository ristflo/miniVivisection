"""miniVivisection's Yak packages (Rhino's Package Manager), one per Rhino: the Release build
(bin/Release/net7.0 for Rhino 8, bin/Release/rhino9 for Rhino 9: dotnet build -c Release
-p:MvRhino=9) without __pycache__, .pdb and runtimes/ (the browser's System.Text.Encodings.Web), plus
packaging/: the manifest (its version filled in), icon.png and misc/ (README.md, the removal tool),
and the repository's LICENSE and THIRD-PARTY-NOTICES.txt into misc/ (LICENSE.txt). Staged in
bin/yak/stage (Rhino 9: bin/yak/stage9), built by that Rhino's Yak.exe (--platform win) into
bin/yak/minivivisection-<version>-rh8_36-win.yak and -rh9_<n>-win.yak (the tag comes from the
RhinoCommon the .rhp references), then checked: the tag, and the manifest Yak wrote into the
package. Both packages carry the same version: the Package Manager of each Rhino offers its own.

The version: 0.2.0-beta.<build> (a pre-release, hidden in the Package Manager unless
"Include pre-releases" is ticked), or with --stable the plain 0.2.0 (Version.props), which
outranks all its betas. A published version can't be changed or deleted -- only yanked.

Build Release first (dotnet build -c Release): the build number is read from the built .gha.

    python tools/make_yak.py                # build both packages (Rhino 8 and Rhino 9)
    python tools/make_yak.py --rhino 8      # only Rhino 8's (or --rhino 9)
    python tools/make_yak.py --push-test    # ... and push it to https://test.yak.rhino3d.com (wiped nightly)
    python tools/make_yak.py --icon         # redraw packaging/icon.png (needs Pillow)

The push to the real server is printed, never run: run it yourself (Yak.exe login first).
See Research_Yak_Package_Manager.md.
"""
import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import zipfile

from make_release import BUILD, HERE, SKIP_DIRS, SKIP_FILES, build_number, version

PACKAGING = os.path.join(HERE, 'packaging')
OUT = os.path.join(HERE, 'bin', 'yak')
STAGE = os.path.join(OUT, 'stage')
YAK = os.environ.get('YAK_EXE') or r'C:\Program Files\Rhino 8\System\Yak.exe'
# Per Rhino: the build folder, the staging folder and the Yak.exe that builds its package.
RHINOS = {
    '8': {'build': BUILD, 'stage': STAGE, 'yak': YAK},
    '9': {'build': os.path.join(os.path.dirname(BUILD), 'rhino9'), 'stage': os.path.join(OUT, 'stage9'),
          'yak': os.environ.get('YAK_EXE_9') or r'C:\Program Files\Rhino 9\System\Yak.exe'},
}
TEST_SERVER = 'https://test.yak.rhino3d.com'
SKIP_TOP = {'runtimes'}            # runtimes/browser: not for Rhino
SKIP_EXT = SKIP_FILES + ('.pdb',)
TAG = re.compile(r'^minivivisection-(?P<version>.+)-(?P<tag>rh(?P<rhino>8|9)_\d+-win)\.yak$')
GUIDS = ('guid:c198ec17-3078-46a0-8f9b-94a40e9ba298',  # the .rhp
         'guid:6d0f4c1e-6a8b-4f0e-9b5d-2f1b3e7c9a41')  # the .gha (Grasshopper's package restore)


def package_version(stable):
    return version() if stable else '%s-beta.%d' % (version(), build_number())


def stage(ver, rhino='8'):
    build_dir, STAGE = RHINOS[rhino]['build'], RHINOS[rhino]['stage']
    if not os.path.isfile(os.path.join(build_dir, 'miniVivisection.rhp')):
        raise SystemExit('no Rhino %s build in %s: dotnet build -c Release%s' % (rhino, build_dir, '' if rhino == '8' else ' -p:MvRhino=9'))
    if os.path.isdir(STAGE):
        shutil.rmtree(STAGE)
    count = 0
    for folder, dirs, names in os.walk(build_dir):
        rel = os.path.relpath(folder, build_dir)
        dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS and not (rel == '.' and d in SKIP_TOP))
        for name in sorted(names):
            if name.endswith(SKIP_EXT):
                continue
            target = os.path.join(STAGE, rel, name)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copy2(os.path.join(folder, name), target)
            count += 1
    shutil.copy2(os.path.join(PACKAGING, 'icon.png'), os.path.join(STAGE, 'icon.png'))
    shutil.copytree(os.path.join(PACKAGING, 'misc'), os.path.join(STAGE, 'misc'))
    shutil.copy2(os.path.join(HERE, 'LICENSE'), os.path.join(STAGE, 'misc', 'LICENSE.txt'))
    shutil.copy2(os.path.join(HERE, 'THIRD-PARTY-NOTICES.txt'), os.path.join(STAGE, 'misc', 'THIRD-PARTY-NOTICES.txt'))
    manifest = open(os.path.join(PACKAGING, 'manifest.yml'), encoding='utf-8').read()
    manifest = re.sub(r'(?m)\A(?:#.*\n)+', '', manifest)                  # the comment is for the repo
    manifest, n = re.subn(r'(?m)^version: .*$', 'version: ' + ver, manifest)
    if n != 1:
        raise SystemExit('packaging/manifest.yml: no "version:" line')
    with open(os.path.join(STAGE, 'manifest.yml'), 'w', encoding='utf-8', newline='\n') as f:
        f.write(manifest)
    return count


def build(rhino='8'):
    STAGE, YAK = RHINOS[rhino]['stage'], RHINOS[rhino]['yak']
    for old in os.listdir(STAGE):
        if old.endswith('.yak'):
            os.remove(os.path.join(STAGE, old))
    result = subprocess.run([YAK, 'build', '--platform', 'win'], cwd=STAGE, capture_output=True, text=True, encoding='utf-8', errors='replace')
    if result.returncode != 0:
        raise SystemExit('Yak build failed:\n' + result.stdout + result.stderr)
    built = [n for n in os.listdir(STAGE) if n.endswith('.yak')]
    if len(built) != 1:
        raise SystemExit('Yak build made %r' % built)
    for old in os.listdir(OUT):
        match = TAG.match(old)
        if match and match.group('rhino') == rhino:
            os.remove(os.path.join(OUT, old))  # one package per Rhino to push: the newest
    target = os.path.join(OUT, built[0])
    shutil.move(os.path.join(STAGE, built[0]), target)
    return target


def check(path, ver):
    """The file name's version and tag, and the manifest Yak wrote: version, platform, both GUIDs."""
    name = os.path.basename(path)
    match = TAG.match(name)
    if not match:
        raise SystemExit('%s: not minivivisection-<version>-rh8_<n>-win.yak / -rh9_<n>-win.yak' % name)
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        manifest = z.read('manifest.yml').decode('utf-8').replace('\r\n', '\n')  # Yak writes it with CRLF
    problems = []
    if not re.search(r'(?m)^version: %s$' % re.escape(ver), manifest):
        problems.append('manifest version is not ' + ver)
    if not re.search(r'(?m)^platform: win$', manifest):
        problems.append('manifest platform is not win')
    for guid in GUIDS:
        if manifest.count(guid) != 1:
            problems.append('%s appears %d times in the keywords' % (guid, manifest.count(guid)))
    for needed in ('miniVivisection.rhp', 'miniVivisection.GH.gha', '0Harmony.dll', 'icon.png', 'misc/LICENSE.txt', 'miniVivisection_Manual.md'):
        if needed not in names:
            problems.append(needed + ' missing')
    junk = [n for n in names if n.endswith(SKIP_EXT) or '__pycache__' in n or n.startswith('runtimes/')]
    if junk:
        problems.append('left in: ' + ', '.join(junk[:5]))
    if problems:
        raise SystemExit(name + ':\n  ' + '\n  '.join(problems))
    return match.group('tag'), len(names)


def push(path, source, yak=YAK):
    result = subprocess.run([yak, 'push', '--source', source, path], capture_output=True, text=True, encoding='utf-8', errors='replace')
    print(result.stdout + result.stderr)
    if result.returncode != 0:
        raise SystemExit('push failed (Yak.exe login first?)')
    print('pushed to', source)


def make_icon():
    """packaging/icon.png, 64 x 64: the web pages' favicon -- a white bold V° on red."""
    from PIL import Image, ImageDraw, ImageFont
    scale = 8
    side = 32 * scale
    image = Image.new('RGBA', (side, side), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((0, 0, side - 1, side - 1), radius=7 * scale, fill=(193, 39, 45, 255))
    font = ImageFont.truetype(r'C:\Windows\Fonts\segoeuib.ttf', 19 * scale)
    draw.text((15 * scale, 23 * scale), 'V\u00b0', font=font, fill=(255, 255, 255, 255), anchor='ms')
    target = os.path.join(PACKAGING, 'icon.png')
    image.resize((64, 64), Image.LANCZOS).save(target)
    print('drew', target)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('--stable', action='store_true', help='version %s, not %s-beta.<build>' % (version(), version()))
    parser.add_argument('--push-test', action='store_true', help='push to ' + TEST_SERVER)
    parser.add_argument('--icon', action='store_true', help='redraw packaging/icon.png and stop')
    parser.add_argument('--rhino', choices=('8', '9', 'both'), default='both', help="which Rhino's package (default: both)")
    args = parser.parse_args()
    if args.icon:
        make_icon()
        return
    ver = package_version(args.stable)
    made = []
    for rhino in (('8', '9') if args.rhino == 'both' else (args.rhino,)):
        yak = RHINOS[rhino]['yak']
        if not os.path.isfile(yak):
            raise SystemExit('no Yak.exe at %s (set YAK_EXE%s)' % (yak, '' if rhino == '8' else '_9'))
        count = stage(ver, rhino)
        path = build(rhino)
        tag, entries = check(path, ver)
        digest = hashlib.sha256(open(path, 'rb').read()).hexdigest()
        print('%s: version %s, %s, %d files from the build, %d in the package, %.1f MB' % (
            os.path.basename(path), ver, tag, count, entries, os.path.getsize(path) / 1048576))
        print('  sha256', digest)
        if args.push_test:
            push(path, TEST_SERVER, yak)
        made.append((yak, path))
    print('\nNext:')
    for yak, path in made:
        if not args.push_test:
            print('  "%s" push --source %s "%s"' % (yak, TEST_SERVER, path))
        print('  "%s" install --source %s miniVivisection %s     (or: install "%s")' % (yak, TEST_SERVER, ver, path))
    for yak, path in made:
        print('  then, permanent:  "%s" push "%s"' % (yak, path))


if __name__ == '__main__':
    sys.exit(main())
