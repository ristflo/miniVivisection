"""miniVivisection's download on the web pages: the Yak package (tools/make_yak.py: the plug-in,
its Grasshopper part, their dependencies, the manual, the examples, the license), installed
from Rhino's Package Manager (the same file is on McNeel's server, a pre-release) or by dragging
it onto Rhino 8's window -- minivivisection-<version>-beta.<N>-rh8_36-win.yak --
and the tool that removes an older zip install first, miniVivisection_Remove_Old_Install.cmd
(packaging/misc: one file to double-click). The package goes into each web page's folder
(web/pages/*) with the manual's PDF, the page's older packages and zips
removed; the tool once, one folder up (web/pages/, https://restweiss.de/miniVivisection/: one
address for all pages and for beta testers), which the pages link as ../. Then it rewrites the
build facts in each page's index.html (package name, version and build, ?b=<build>, built time,
size in MB, file count: see page_facts) and checks that every download link resolves.

The package of a build is made once: run again (for a new manual PDF, say), it reuses
bin/yak's package of this build instead of zipping the same files anew into other bytes. A version
already on McNeel's server can't change, so the page's package must be the server's: it is
compared byte for byte, and a difference stops the release (--rebuild makes a new zip anyway).

At the end, a version not yet on the server is pushed there (Rhino 8's Yak.exe), after asking:
the push is permanent. Then the server's file is fetched and compared with the package. Without
a console to ask in, or with --no-push, the push command is printed instead. A push refused for
want of a login: Yak.exe login (the login lasts about 30 days). Upload the pages yourself.

Build Release first (dotnet build -c Release): the build number is read from the built .gha.

    python tools/make_release.py
    python tools/make_release.py --rebuild    # zip this build's package anew
    python tools/make_release.py --no-push    # pages only; print the push command
"""
import datetime
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILD = os.path.join(HERE, 'src', 'miniVivisection', 'bin', 'Release', 'net7.0')
PAGES = os.path.join(HERE, 'web', 'pages')
SKIP_DIRS = {'__pycache__'}
SKIP_FILES = ('.3dmbak', '.rhl', '.pyc')


def version():
    props = open(os.path.join(HERE, 'Version.props'), encoding='utf-8').read()
    found = re.search(r'<VersionPrefix>([^<]+)</VersionPrefix>', props) or re.search(r'<Version>([^<]+)</Version>', props)
    return found.group(1) if found else '0.0.0'


def build_number():
    """The BuildNumber the .gha carries (an AssemblyMetadata attribute: its value follows the key)."""
    data = open(os.path.join(BUILD, 'miniVivisection.GH.gha'), 'rb').read()
    at = data.find(b'BuildNumber')
    if at < 0:
        raise RuntimeError('no BuildNumber in the .gha: build Release first')
    length = data[at + len(b'BuildNumber')]
    return int(data[at + len(b'BuildNumber') + 1:at + len(b'BuildNumber') + 1 + length].decode('ascii'))


SERVER = 'https://yak.rhino3d.com/versions/miniVivisection/%s'  # the package's record on McNeel's server
REMOVER = 'miniVivisection_Remove_Old_Install.cmd'  # one file: the batch part starts the PowerShell below it
YAK_NAME = r'minivivisection-\d+\.\d+\.\d+[0-9A-Za-z.\-]*-rh\d+(?:_\d+)?-(?:win|mac|any)\.yak'


def utc_offset(when):
    """'UTC+3' (or 'UTC+5:30') for a local time."""
    minutes = int(when.astimezone().utcoffset().total_seconds() // 60)
    sign, minutes = ('+' if minutes >= 0 else '-'), abs(minutes)
    return 'UTC%s%d' % (sign, minutes // 60) + (':%02d' % (minutes % 60) if minutes % 60 else '')


def page_facts(ver, build, name, built, size, count):
    """The build facts in the pages: (pattern, replacement). Keep the pages' wording to these forms."""
    return [
        (YAK_NAME, name),                                                            # the package's href
        (r'\b\d+\.\d+\.\d+((?:,|\s*&middot;|\s*\()?\s*)build \d+', ver + r'\1build %d' % build),  # 0.2.0 · build 241
        (r'\?b=\d+', '?b=%d' % build),                                               # hero, og:image, screenshots
        (r'\b\d{4}-\d\d-\d\d \d\d:\d\d UTC[+-]\d+(?::\d\d)?', '%s %s' % (built.strftime('%Y-%m-%d %H:%M'), utc_offset(built))),
        (r'\b\d+\.\d MB\b', '%.1f MB' % (size / 1048576)),
        (r'\b\d+ files\b', '%d files' % count),
        (r'(?<!with )\bminiVivisection \d+\.\d+\.\d+\b', 'miniVivisection ' + ver),  # top bar, footer; not the
        # screenshots' "with miniVivisection 0.2.0": that names the version they were taken with
    ]


def update_pages(facts):
    """Rewrites the build facts in every page's index.html and in the chooser page
    (webpage/index.html); says which pattern a page lacks."""
    for page in sorted(glob.glob(os.path.join(PAGES, '*', 'index.html'))) + [os.path.join(PAGES, 'index.html')]:
        if not os.path.isfile(page):
            continue
        with open(page, encoding='utf-8', newline='') as f:
            html = f.read()
        changed = html
        for pattern, replacement in facts:
            changed, n = re.subn(pattern, replacement, changed)
            if not n:
                print('  ! %s: no %s' % (os.path.basename(os.path.dirname(page)), pattern))
        if changed != html:
            with open(page, 'w', encoding='utf-8', newline='') as f:
                f.write(changed)
            print('  updated', page)


def check_pages():
    """Every package, zip and removal tool a page links to is in its folder, and it links to a package."""
    bad = 0
    for page in sorted(glob.glob(os.path.join(PAGES, '*', 'index.html'))):
        folder = os.path.dirname(page)
        html = open(page, encoding='utf-8').read()
        hrefs = set(re.findall(r'href="(%s|(?:\.\./)?miniVivisection_[^"/]+\.(?:zip|cmd))"' % YAK_NAME, html))
        if not any(h.endswith('.yak') for h in hrefs):
            print('  ! %s links to no package' % page)
            bad += 1
        for href in sorted(hrefs):
            if not os.path.isfile(os.path.join(folder, href)):
                print('  ! %s links to %s, which is missing' % (page, href))
                bad += 1
    if bad:
        raise SystemExit('%d page link(s) broken' % bad)
    print('pages: every download link resolves')


def published(ver):
    """This version's package on McNeel's server (its bytes); None when the server hasn't got it."""
    try:
        record = json.load(urllib.request.urlopen(SERVER % ver, timeout=30))
    except urllib.error.HTTPError as ex:
        if ex.code == 404:
            return None
        raise
    urls = [d['url'] for d in record.get('distributions', []) if d.get('platform') == 'win' and d.get('url')]
    return urllib.request.urlopen(urls[0], timeout=120).read() if urls else None


def main():
    import make_yak  # here: make_yak imports this module
    ver = make_yak.package_version(stable=False)
    made = glob.glob(os.path.join(make_yak.OUT, 'minivivisection-%s-*.yak' % ver))
    if made and '--rebuild' not in sys.argv:
        target = made[0]
        print("%s: this build's package, as made before" % os.path.basename(target))
    else:
        make_yak.stage(ver)
        target = make_yak.build()
    count = make_yak.check(target, ver)[1]
    try:
        server = published(ver)
    except Exception as ex:  # no connection: say so, go on
        server = None
        print("  ! McNeel's server not reached (%s): the package is not compared with it" % ex)
    if server is not None:
        if server != open(target, 'rb').read():
            raise SystemExit("%s differs from version %s on McNeel's server, which cannot change: "
                             'build Release anew (a new build number) for a new package' % (os.path.basename(target), ver))
        print("  the same file as version %s on McNeel's server" % ver)
    name = os.path.basename(target)
    built = datetime.datetime.fromtimestamp(os.path.getmtime(os.path.join(BUILD, 'miniVivisection.rhp')))
    size = os.path.getsize(target)
    print('%s: %d files, %.1f MB, built %s' % (name, count, size / 1048576, built.strftime('%Y-%m-%d %H:%M')))
    remover = os.path.join(HERE, 'packaging', 'misc', REMOVER)
    manual = os.path.join(HERE, 'docs', 'miniVivisection_Manual.pdf')
    for page in sorted(glob.glob(os.path.join(PAGES, '*', 'index.html'))):
        folder = os.path.dirname(page)
        for old in glob.glob(os.path.join(folder, 'miniVivisection_*.zip')) + glob.glob(os.path.join(folder, 'minivivisection-*.yak')) + glob.glob(os.path.join(folder, REMOVER)):
            os.remove(old)  # the tool sits one folder up now
        shutil.copy2(target, os.path.join(folder, name))
        shutil.copy2(manual, os.path.join(folder, 'miniVivisection_Manual.pdf'))
        print('  ->', folder)
    shutil.copy2(remover, os.path.join(PAGES, REMOVER))
    print('  ->', os.path.join(PAGES, REMOVER))
    update_pages(page_facts(version(), build_number(), name, built, size, count))
    check_pages()
    if server is None:
        push(make_yak.YAK, target, ver)
    print('Upload the pages: python tools/publish_webpages.py (to https://restweiss.de/miniVivisection).')


def push(yak, target, ver):
    """The package to McNeel's server, after asking (it stays there for good), then checked there."""
    command = '"%s" push "%s"' % (yak, target)
    if '--no-push' in sys.argv or not sys.stdin.isatty():
        print('Not on the package server yet. To publish it there (permanent):\n  ' + command)
        return
    answer = input('Push %s to McNeel\'s package server? It stays there for good (only yank hides a version). [y/N] ' % os.path.basename(target))
    if answer.strip().lower() not in ('y', 'yes', 'j', 'ja'):
        print('Not pushed. Later:\n  ' + command)
        return
    result = subprocess.run([yak, 'push', target], capture_output=True, text=True, encoding='utf-8', errors='replace')
    if result.returncode != 0:
        print(result.stdout + result.stderr)
        raise SystemExit('The push failed -- not logged in? "%s" login, then:\n  %s' % (yak, command))
    try:
        online = published(ver)
    except Exception as ex:
        online = None
        print("  ! McNeel's server not reached to check the push (%s)" % ex)
    if online is not None and online != open(target, 'rb').read():
        raise SystemExit("Pushed, but McNeel's server has another file for %s" % ver)
    print('Pushed: %s is on McNeel\'s package server%s.' % (ver, ' (the same file)' if online is not None else ''))


if __name__ == '__main__':
    main()
