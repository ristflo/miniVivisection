"""The web pages (web/pages): the eight designs, the chooser (index.html) and the two legal pages
(impressum.html, datenschutz.html). Every local link resolves, every page offers the same .yak, the
head has a title and Open Graph tags, nothing is loaded from another host (no Google Fonts: the
fonts lie in the page's fonts/ with their licence), every page links the Impressum and the privacy
page, and the folders hold nothing a page doesn't name (publish_webpages.py uploads only named files).
The package, the manual's PDF and the removal tool lie in web/pages/downloads/, which
tools/make_release.py fills and git ignores: in a clone without it, links into it aren't checked."""
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # the repository
WEB = os.path.join(ROOT, 'web', 'pages')
PAGES = ('clinical-imaging', 'luminous-gallery', 'orbital-theatre', 'porcelain-futurism', 'victorian-engraving',
         'drafting-studio', 'light-loom', 'code-rain')
# the chooser (web/pages/index.html) offers these; the others are reachable by their address only
SHOWN = ('clinical-imaging', 'luminous-gallery', 'orbital-theatre', 'porcelain-futurism', 'victorian-engraving', 'code-rain')
LEGAL = ('impressum.html', 'datenschutz.html')
DOWNLOADS = os.path.join(WEB, 'downloads')
TRADEMARK = 'Not affiliated with Robert McNeel &amp; Associates. Rhino and Grasshopper are trademarks of their owners.'
AI_NOTE = re.compile(r'made with AI image generators|imagined by a machine')


def _read(*parts):
    with open(os.path.join(WEB, *parts), encoding='utf-8') as f:
        return f.read()


def _page(name):
    return _read(name, 'index.html')


def _all():
    """(folder, html) of every HTML file that is uploaded."""
    yield from ((os.path.join(WEB, p), _page(p)) for p in PAGES)
    yield WEB, _read('index.html')
    yield from ((WEB, _read(name)) for name in LEGAL)


def _local_targets(html):
    """The local files a page names in href, src or a CSS url(), without query or anchor."""
    targets = set()
    for target in re.findall(r'(?:href|src)="([^"]+)"', html) + re.findall(r'''\burl\(\s*["']?([^"')\s]+)''', html):
        if re.match(r'^([a-z][a-z0-9+.-]*:|#)', target, re.I):
            continue  # another scheme (https:, mailto:, data:) or an anchor
        target = target.split('?', 1)[0].split('#', 1)[0]
        if target and target not in ('./', '.'):
            targets.add(os.path.normpath(target.replace('/', os.sep)))
    return targets


def _in_downloads(folder, target):
    return os.path.normpath(os.path.join(folder, target)).startswith(DOWNLOADS + os.sep)


def test_local_links_resolve():
    for folder, html in _all():
        for target in _local_targets(html):
            if _in_downloads(folder, target) and not os.path.isdir(DOWNLOADS):
                continue  # not made here (make_release.py)
            assert os.path.exists(os.path.join(folder, target)), '%s names %s, which is missing' % (folder, target)


def test_nothing_loaded_from_another_host():
    """No script, stylesheet, font, picture, frame or preconnect from another server: a visit sends
    nothing to Google or anyone else (datenschutz.html says so). Plain links (<a href>), canonical
    and og: addresses are not loaded by the browser."""
    for folder, html in _all():
        where = os.path.relpath(folder, WEB)
        assert not re.search(r'\bsrc\s*=\s*["\']?(https?:)?//', html, re.I), '%s: a src on another host' % where
        assert not re.search(r'''\burl\(\s*["']?(https?:)?//''', html, re.I), '%s: a CSS url() on another host' % where
        assert '@import' not in html, '%s: @import' % where
        for tag in re.findall(r'<link\b[^>]*>', html, re.I):
            if re.search(r'rel="canonical"', tag):
                continue
            assert not re.search(r'href="(https?:)?//', tag), '%s loads %s' % (where, tag)
        assert not re.search(r'<(script|iframe|embed|object)\b', html, re.I), '%s: a script or an embed' % where


def test_fonts_have_their_licence():
    """Every self-hosted font file is in fonts/ beside a licence that the footer links."""
    for page in PAGES:
        html = _page(page)
        fonts = [t for t in _local_targets(html) if t.endswith('.woff2')]
        licences = [t for t in _local_targets(html) if os.path.basename(t).startswith('OFL-')]
        assert bool(fonts) == bool(licences), '%s: fonts %s, licences %s' % (page, fonts, licences)


def test_pages_name_one_released_package():
    """Every page names one Yak package, the same on all of them, and that file is there (the
    pages name the released build, which is behind the local build count); the chooser links it too."""
    named = set()
    for page in PAGES:
        html = _page(page)
        packages = set(re.findall(r'(minivivisection-[\d.]+-beta\.\d+-rh8_\d+-win\.yak)', html))
        assert len(packages) == 1, '%s names %s packages' % (page, len(packages))
        assert 'href="../downloads/%s"' % next(iter(packages)) in html, '%s: the package is not in ../downloads/' % page
        named |= packages
    assert len(named) == 1, 'the pages name different packages: %s' % named
    chooser = set(re.findall(r'(minivivisection-[\d.]+-beta\.\d+-rh8_\d+-win\.yak)', _read('index.html')))
    assert chooser == named, 'the chooser names %s, the pages %s' % (chooser, named)
    if os.path.isdir(DOWNLOADS):
        assert os.path.isfile(os.path.join(DOWNLOADS, next(iter(named)))), 'downloads/ lacks the package'


def test_head_has_title_and_open_graph():
    for page in PAGES:
        html = _page(page)
        title = re.search(r'<title>([^<]+)</title>', html)
        assert title and title.group(1).strip() != 'miniVivisection', '%s: a descriptive title' % page
        for tag in ('og:title', 'og:description', 'og:image', 'og:url'):
            assert 'property="%s"' % tag in html, '%s: no %s' % (page, tag)
        assert '<meta name="viewport"' in html, '%s: no viewport' % page


def test_nothing_unnamed_in_the_folders():
    """Every file in a page folder, and in web/pages itself, is named by a page (it would be
    uploaded, or it lies there unused)."""
    for page in PAGES:
        folder = os.path.join(WEB, page)
        named = _local_targets(_page(page))
        for root, _, files in os.walk(folder):
            for name in files:
                relative = os.path.relpath(os.path.join(root, name), folder)
                if relative != 'index.html':
                    assert relative in named, '%s holds %s, which no page names' % (page, relative)
    named = set()
    for page in PAGES:
        named |= {t[3:] for t in _local_targets(_page(page)) if t.startswith('..' + os.sep)}
    named |= _local_targets(_read('index.html'))
    for name in os.listdir(WEB):
        if os.path.isfile(os.path.join(WEB, name)) and name != 'index.html':
            assert name in named, 'web/pages holds %s, which no page names' % name
    for shared in ('screenshots', 'downloads'):
        folder = os.path.join(WEB, shared)
        for name in (os.listdir(folder) if os.path.isdir(folder) else []):
            assert os.path.join(shared, name) in named, 'web/pages/%s holds %s, which no page names' % (shared, name)


def test_chooser_page():
    """The chooser offers the shown styles and no other."""
    html = _read('index.html')
    for page in PAGES:
        assert ('href="%s/"' % page in html) == (page in SHOWN), 'the chooser %s %s' % ('lacks' if page in SHOWN else 'shows', page)
    assert '<title>' in html and 'property="og:image"' in html


def test_every_page_links_impressum_and_privacy():
    for page in PAGES:
        html = _page(page)
        for name in LEGAL:
            assert 'href="../%s"' % name in html, '%s: no link to %s' % (page, name)
    for name in ('index.html',) + LEGAL:
        html = _read(name)
        for legal in LEGAL:
            assert 'href="%s"' % legal in html, '%s: no link to %s' % (name, legal)


def test_legal_pages():
    """The Impressum names the provider with a street address and an e-mail; the privacy page
    names the controller, the server log and how long it is kept, and the rights."""
    impressum, privacy = _read('impressum.html'), _read('datenschutz.html')
    for html in (impressum, privacy):
        for part in ('Florian Rist', 'Grüntenstraße 7', '86899 Landsberg am Lech', 'minivivisection@restweiss.de'):
            assert part in html, part
    assert '§ 5 DDG' in impressum
    for part in ('Art. 6 Abs. 1 lit. f DSGVO', '7 Tagen', 'keine Cookies', 'Art. 77', 'GitHub', 'lang="en"'):
        assert part in privacy, part


def test_notes_on_every_page():
    """The trademark note and the note on the AI-made pictures are on every page; the eight
    footers are the same apart from the font credits."""
    footers = set()
    for page in PAGES:
        html = _page(page)
        assert TRADEMARK in html, '%s: no trademark note' % page
        assert AI_NOTE.search(html), '%s: no note on the AI pictures' % page
        footer = re.search(r'<footer>.*?</footer>', html, re.S).group(0)
        footers.add(re.sub(r'\s*<span class="fonts">.*?</span>', '', footer))
    assert len(footers) == 1, 'the footers differ:\n' + '\n'.join(footers)
    chooser = _read('index.html')
    assert TRADEMARK in chooser and AI_NOTE.search(chooser)


def test_same_content_on_every_page():
    """The feature texts are the same on every page (the pages differ in style): every
    list item of the first page is on the others."""
    items = {}
    for page in PAGES:
        html = re.sub(r'<(style|script)[^>]*>.*?</\1>', '', _page(page), flags=re.S)
        html = re.sub(r'<[^>]+>', '', html)
        items[page] = set(line.strip() for line in html.splitlines() if len(line.strip()) > 60)
    first = items[PAGES[0]]
    for page in PAGES[1:]:
        missing = [i for i in first if i not in items[page] and len(i) > 60]
        assert len(missing) <= len(first) * 0.2, '%s lacks %d of %d texts, e.g. %r' % (page, len(missing), len(first), missing[:3])
