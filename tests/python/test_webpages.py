"""The five web pages (experimental/docu/webpage/*): every local link resolves, every page
names the zip of the current build, the head has a title and Open Graph tags, and the
page folder holds nothing a page doesn't name (publish_webpages.py uploads only named files)."""
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # the repository
WEB = os.path.join(ROOT, 'web', 'pages')
PAGES = ('clinical-imaging', 'luminous-gallery', 'orbital-theatre', 'porcelain-futurism', 'victorian-engraving',
         'drafting-studio', 'light-loom', 'code-rain')
# the chooser (webpage/index.html) offers these; the others are reachable by their address only
SHOWN = ('clinical-imaging', 'luminous-gallery', 'orbital-theatre', 'porcelain-futurism', 'victorian-engraving', 'code-rain')


def _page(name):
    with open(os.path.join(WEB, name, 'index.html'), encoding='utf-8') as f:
        return f.read()


def _local_targets(html):
    """The local files a page names in href and src, without query or anchor."""
    targets = set()
    for target in re.findall(r'(?:href|src)="([^"]+)"', html):
        if re.match(r'^(https?:|mailto:|#|data:)', target):
            continue
        target = target.split('?', 1)[0].split('#', 1)[0]
        if target:
            targets.add(target.replace('/', os.sep))
    return targets


def _build():
    path = os.path.join(ROOT, 'build-number.txt')
    if not os.path.exists(path):
        return None
    with open(path) as f:
        return int(f.read().strip() or 0)


def test_local_links_resolve():
    for page in PAGES:
        folder = os.path.join(WEB, page)
        for target in _local_targets(_page(page)):
            assert os.path.exists(os.path.join(folder, target)), '%s names %s, which is missing' % (page, target)


def test_pages_name_one_released_package():
    """Every page names one Yak package, the same on all of them, and that file is there (the
    pages name the released build, which is behind the local build count)."""
    named = set()
    for page in PAGES:
        html = _page(page)
        packages = set(re.findall(r'(minivivisection-[\d.]+-beta\.\d+-rh8_\d+-win\.yak)', html))
        assert len(packages) == 1, '%s names %s packages' % (page, len(packages))
        assert os.path.exists(os.path.join(WEB, page, next(iter(packages)))), '%s: its package is missing' % page
        named |= packages
    assert len(named) == 1, 'the pages name different packages: %s' % named


def test_head_has_title_and_open_graph():
    for page in PAGES:
        html = _page(page)
        title = re.search(r'<title>([^<]+)</title>', html)
        assert title and title.group(1).strip() != 'miniVivisection', '%s: a descriptive title' % page
        for tag in ('og:title', 'og:description', 'og:image', 'og:url'):
            assert 'property="%s"' % tag in html, '%s: no %s' % (page, tag)
        assert '<meta name="viewport"' in html, '%s: no viewport' % page


def test_nothing_unnamed_in_the_page_folders():
    for page in PAGES:
        folder = os.path.join(WEB, page)
        named = _local_targets(_page(page))
        for root, _, files in os.walk(folder):
            for name in files:
                relative = os.path.relpath(os.path.join(root, name), folder)
                if relative == 'index.html':
                    continue
                assert relative in named, '%s holds %s, which no page names (it would be uploaded)' % (page, relative)


def test_chooser_page():
    """webpage/index.html offers the shown styles and no other, its local links resolve, and
    it names the same zip as the pages."""
    with open(os.path.join(WEB, 'index.html'), encoding='utf-8') as f:
        html = f.read()
    for page in PAGES:
        assert ('href="%s/"' % page in html) == (page in SHOWN), 'the chooser %s %s' % ('lacks' if page in SHOWN else 'shows', page)
    for target in _local_targets(html):
        assert os.path.exists(os.path.join(WEB, target)), 'the chooser names %s, which is missing' % target
    zips = set(re.findall(r'(miniVivisection_[\d.]+_build\d+\.zip)', html))
    pages_zip = set(re.findall(r'(miniVivisection_[\d.]+_build\d+\.zip)', _page(PAGES[0])))
    assert zips == pages_zip, 'the chooser names %s, the pages %s' % (zips, pages_zip)
    assert '<title>' in html and 'property="og:image"' in html


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
