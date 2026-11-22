"""miniVivisection's download on the web pages: the Yak packages (tools/make_yak.py: the plug-in,
its Grasshopper part, their dependencies, the manual, the examples, the license), installed
from Rhino's Package Manager (the same files are on McNeel's server, pre-releases) or by dragging
one onto Rhino's window -- minivivisection-<version>-beta.<N>-rh8_36-win.yak for Rhino 8 and
...-rh9_2-win.yak for Rhino 9 (made when there is a Rhino 9 build: dotnet build -c Release
-p:MvRhino=9; the first such release adds a "For Rhino 9" button to the pages, add_rhino9) -- and the tool that removes an older zip install first, miniVivisection_Remove_Old_Install.cmd
(packaging/misc: one file to double-click). The package, the manual's PDF and the tool go into
web/pages/downloads/ (not in git; https://restweiss.de/miniVivisection/downloads/: one address
for all pages and for beta testers, which the pages link as ../downloads/), the older packages
there removed; the pages' screenshots (web/pages/screenshots/, linked as ../screenshots/) are
refreshed from docs/screenshots. Then it rewrites the build facts in each page's index.html
(package names, version and build, ?b=<build>, built time, size in MB, file count: see
page_facts) and checks that every download link resolves.

Each package of a build is made once: run again (for a new manual PDF, say), it reuses
bin/yak's package of this build instead of zipping the same files anew into other bytes. A version
already on McNeel's server can't change, so the page's package must be the server's: it is
compared byte for byte, and a difference stops the release (--rebuild makes a new zip anyway).

At the end, a package not yet on the server is pushed there (its Rhino's Yak.exe), after asking:
the push is permanent. Then the server's file is fetched and compared with the package. Without
a console to ask in, or with --no-push, the push command is printed instead. A push refused for
want of a login: Yak.exe login (the login lasts about 30 days). Upload the pages yourself.

Build Release first (dotnet build -c Release, and for Rhino 9 also -p:MvRhino=9): the build number
is read from the Rhino 8 build's .gha; both packages carry that version.

    python tools/make_release.py
    python tools/make_release.py --rebuild    # zip this build's package anew
    python tools/make_release.py --no-push    # pages only; print the push command
    python tools/make_release.py --check      # only check the pages' download links; changes nothing
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
DOWNLOADS = os.path.join(PAGES, 'downloads')
SCREENSHOTS = os.path.join(PAGES, 'screenshots')
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
YAK_NAME = r'minivivisection-\d+\.\d+\.\d+[0-9A-Za-z.\-]*-rh%s(?:_\d+)?-(?:win|mac|any)\.yak'  # % the Rhino


def utc_offset(when):
    """'UTC+3' (or 'UTC+5:30') for a local time."""
    minutes = int(when.astimezone().utcoffset().total_seconds() // 60)
    sign, minutes = ('+' if minutes >= 0 else '-'), abs(minutes)
    return 'UTC%s%d' % (sign, minutes // 60) + (':%02d' % (minutes % 60) if minutes % 60 else '')


def page_facts(ver, build, names, built, size, count):
    """The build facts in the pages: (pattern, replacement). Keep the pages' wording to these forms.
    names: the packages' file names by Rhino ('8', '9'); size and count are the Rhino 8 package's."""
    return [(YAK_NAME % rhino, name) for rhino, name in sorted(names.items())] + [  # the packages' hrefs
        (r'\b\d+\.\d+\.\d+((?:,|\s*&middot;|\s*\()?\s*)build \d+', ver + r'\1build %d' % build),  # 0.2.0 · build 241
        (r'\?b=\d+', '?b=%d' % build),                                               # hero, og:image, screenshots
        (r'\b\d{4}-\d\d-\d\d \d\d:\d\d UTC[+-]\d+(?::\d\d)?', '%s %s' % (built.strftime('%Y-%m-%d %H:%M'), utc_offset(built))),
        (r'\b\d+\.\d MB\b', '%.1f MB' % (size / 1048576)),
        (r'\b\d+ files\b', '%d files' % count),
        (r'(?<!with )\bminiVivisection \d+\.\d+\.\d+\b', 'miniVivisection ' + ver),  # top bar, footer; not the
        # screenshots' "with miniVivisection 0.2.0": that names the version they were taken with
    ]


# The pages' Rhino 9 wording, put in by the first release with a Rhino 9 package (add_rhino9):
# (text there before, text after). The pages' own wording; keep it to these forms.
RHINO9_TEXT = [
    ("or drag it onto Rhino 8.</p>", "or drag it onto Rhino 8. Rhino 9 has a package of its own.</p>"),
    ("<td>Windows, Rhino 8.36 or later (tested with 8.37)", "<td>Windows, Rhino 8.36 or later (tested with 8.37), or Rhino 9.2 or later with the Rhino 9 package"),
    ("In Rhino 8: command <em>PackageManager</em>", "In Rhino 8 or 9: command <em>PackageManager</em>"),
    ("Or drag the downloaded <code>.yak</code> file onto Rhino 8's window.", "Or drag the downloaded <code>.yak</code> file onto Rhino's window (the Rhino 9 package for Rhino 9)."),
]


def add_rhino9(name9):
    """The Rhino 9 package on the pages, once: a "For Rhino 9" button after each page's download
    button (the pages' "btn ghost" style), the chooser's "(Rhino 9)" link, and RHINO9_TEXT. A page
    that has a Rhino 9 link already is left as it is (update_pages keeps its name current)."""
    for page in sorted(glob.glob(os.path.join(PAGES, '*', 'index.html'))):
        with open(page, encoding='utf-8', newline='') as f:
            html = f.read()
        if '-rh9_' in html:
            continue
        nl = '\r\n' if '\r\n' in html else '\n'
        found = re.search(r'([ \t]*)<a class="btn primary" href="\.\./downloads/' + YAK_NAME % '8' + r'" download>(&#\d+;)(&ensp;| )Download \(\d+\.\d MB\)</a>', html)
        if not found:
            print('  ! %s: no download button for the Rhino 9 one' % os.path.basename(os.path.dirname(page)))
            continue
        button = '%s<a class="btn ghost" href="../downloads/%s" download>%s%sFor Rhino 9</a>' % (found.group(1), name9, found.group(2), found.group(3))
        html = html[:found.end()] + nl + button + html[found.end():]
        for before, after in RHINO9_TEXT:
            if before in html:
                html = html.replace(before, after, 1)
            else:
                print('  ! %s: no "%s"' % (os.path.basename(os.path.dirname(page)), before[:40]))
        with open(page, 'w', encoding='utf-8', newline='') as f:
            f.write(html)
        print('  Rhino 9 package added to', page)
    chooser = os.path.join(PAGES, 'index.html')
    if os.path.isfile(chooser):
        with open(chooser, encoding='utf-8', newline='') as f:
            html = f.read()
        found = re.search(r'<a href="downloads/' + YAK_NAME % '8' + r'">download</a>', html)
        if '-rh9_' not in html and found:
            html = html[:found.end()] + ' (<a href="downloads/%s">Rhino 9</a>)' % name9 + html[found.end():]
            with open(chooser, 'w', encoding='utf-8', newline='') as f:
                f.write(html)
            print('  Rhino 9 package added to', chooser)


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
    """Every package, PDF, removal tool and screenshot a page (or the chooser) links to is there,
    and each links to a package."""
    bad = 0
    for page in sorted(glob.glob(os.path.join(PAGES, '*', 'index.html'))) + [os.path.join(PAGES, 'index.html')]:
        folder = os.path.dirname(page)
        html = open(page, encoding='utf-8').read()
        hrefs = set(re.findall(r'(?:href|src)="((?:\.\./)?(?:downloads|screenshots)/[^"?#]+)', html))
        if not any(h.endswith('.yak') for h in hrefs):
            print('  ! %s links to no package' % page)
            bad += 1
        for href in sorted(hrefs):
            if not os.path.isfile(os.path.join(folder, *href.split('/'))):
                print('  ! %s links to %s, which is missing' % (page, href))
                bad += 1
    if bad:
        raise SystemExit('%d page link(s) broken' % bad)
    print('pages: every download link resolves')


def published(ver, rhino='8'):
    """This version's package for that Rhino on McNeel's server (its bytes); None when the server hasn't got it."""
    try:
        record = json.load(urllib.request.urlopen(SERVER % ver, timeout=30))
    except urllib.error.HTTPError as ex:
        if ex.code == 404:
            return None
        raise
    urls = [d['url'] for d in record.get('distributions', [])
            if d.get('platform') == 'win' and d.get('url') and (d.get('rhino_version') or 'rh8').startswith('rh%s' % rhino)]
    return urllib.request.urlopen(urls[0], timeout=120).read() if urls else None


def main():
    if '--check' in sys.argv:
        check_pages()
        return
    import make_yak  # here: make_yak imports this module
    ver = make_yak.package_version(stable=False)
    targets = {}  # by Rhino: the package
    for rhino in ('8', '9'):
        tags = [(m, make_yak.TAG.match(os.path.basename(m))) for m in glob.glob(os.path.join(make_yak.OUT, 'minivivisection-%s-*.yak' % ver))]
        made = [m for m, tag in tags if tag and tag.group('rhino') == rhino]
        if made and '--rebuild' not in sys.argv:
            targets[rhino] = made[0]
            print("%s: this build's package, as made before" % os.path.basename(made[0]))
        elif rhino == '9' and not os.path.isfile(os.path.join(make_yak.RHINOS['9']['build'], 'miniVivisection.rhp')):
            print('  ! no Rhino 9 build (dotnet build -c Release -p:MvRhino=9): only the Rhino 8 package')
        else:
            make_yak.stage(ver, rhino)
            targets[rhino] = make_yak.build(rhino)
    servers = {}
    for rhino, target in sorted(targets.items()):
        entries = make_yak.check(target, ver)[1]
        if rhino == '8':
            count = entries  # the pages give the Rhino 8 package's file count
        try:
            servers[rhino] = published(ver, rhino)
        except Exception as ex:  # no connection: say so, go on
            servers[rhino] = None
            print("  ! McNeel's server not reached (%s): the package is not compared with it" % ex)
        if servers[rhino] is not None:
            if servers[rhino] != open(target, 'rb').read():
                raise SystemExit("%s differs from version %s on McNeel's server, which cannot change: "
                                 'build Release anew (a new build number) for a new package' % (os.path.basename(target), ver))
            print("  %s: the same file as version %s on McNeel's server" % (os.path.basename(target), ver))
    target = targets['8']
    names = {rhino: os.path.basename(t) for rhino, t in targets.items()}
    name = names['8']
    built = datetime.datetime.fromtimestamp(os.path.getmtime(os.path.join(BUILD, 'miniVivisection.rhp')))
    size = os.path.getsize(target)
    print('%s: %d files, %.1f MB, built %s' % (name, count, size / 1048576, built.strftime('%Y-%m-%d %H:%M')))
    remover = os.path.join(HERE, 'packaging', 'misc', REMOVER)
    manual = os.path.join(HERE, 'docs', 'miniVivisection_Manual.pdf')
    os.makedirs(DOWNLOADS, exist_ok=True)
    for old in glob.glob(os.path.join(DOWNLOADS, 'minivivisection-*.yak')) + glob.glob(os.path.join(DOWNLOADS, 'miniVivisection_*.zip')):
        os.remove(old)
    for source in list(targets.values()) + [manual, remover]:
        shutil.copy2(source, DOWNLOADS)
        print('  ->', os.path.join(DOWNLOADS, os.path.basename(source)))
    for shot in sorted(os.listdir(SCREENSHOTS)):  # the ones the pages show, as the manual has them now
        source = os.path.join(HERE, 'docs', 'screenshots', shot)
        if os.path.isfile(source):
            shutil.copy2(source, os.path.join(SCREENSHOTS, shot))
    if '9' in names:
        add_rhino9(names['9'])
    update_pages(page_facts(version(), build_number(), names, built, size, count))
    check_pages()
    for rhino, target in sorted(targets.items()):
        if servers[rhino] is None:
            push(make_yak.RHINOS[rhino]['yak'], target, ver, rhino)
    print('Upload the pages: python tools/publish_webpages.py (to https://restweiss.de/miniVivisection).')


def push(yak, target, ver, rhino='8'):
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
        online = published(ver, rhino)
    except Exception as ex:
        online = None
        print("  ! McNeel's server not reached to check the push (%s)" % ex)
    if online is not None and online != open(target, 'rb').read():
        raise SystemExit("Pushed, but McNeel's server has another file for %s" % ver)
    print('Pushed: %s is on McNeel\'s package server%s.' % (ver, ' (the same file)' if online is not None else ''))


if __name__ == '__main__':
    main()
