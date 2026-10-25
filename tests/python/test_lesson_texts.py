"""The Grasshopper lessons' texts (tools/make_gh_examples.py) against the code and the guide:
one structure, menu items that exist, links to objects the generator makes, every lesson
listed. Runs with the helpers' pytest (no Rhino)."""
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # the repository
SRC = os.path.join(ROOT, 'src', 'miniVivisection')  # the plug-in's sources
GH = os.path.join(ROOT, 'src', 'miniVivisection.GH')
GENERATOR = os.path.join(ROOT, 'tools', 'make_gh_examples.py')
GUIDE = os.path.join(ROOT, 'examples', '00_Examples_Guide.md')


def _read(path):
    with open(path, encoding='utf-8') as f:
        return f.read()


def _generator():
    return _read(GENERATOR)


def _lesson_texts():
    """{n: text} of GH1_TEXT ... GH12_TEXT."""
    found = re.findall(r'^GH(\d+)_TEXT = """(.*?)"""', _generator(), re.S | re.M)
    return {int(n): text for n, text in found}


def _csharp():
    parts = []
    for folder in (SRC, GH):
        for name in os.listdir(folder):
            if name.endswith('.cs'):
                parts.append(_read(os.path.join(folder, name)))
    return '\n'.join(parts)


def test_every_lesson_has_the_three_sections():
    texts = _lesson_texts()
    assert len(texts) == 12
    for n, text in texts.items():
        assert text.lstrip().startswith('# GH %d -- ' % n), 'GH %d: its title' % n
        for heading in ('## The bug', '## Try this', '## Without miniVivisection'):
            assert heading in text, 'GH %d: no "%s"' % (n, heading)
        assert text.index('## The bug') < text.index('## Try this') < text.index('## Without miniVivisection'), 'GH %d: the order' % n


def test_every_lesson_panel_is_markdown():
    assert _generator().count("_show_as(lesson, 'Markdown')") == 12 + 1  # and the overview, gh0


def test_quoted_menu_items_exist():
    """A step's *Right-click X > V° Item* names a menu item the code has (Menu("Item...": its words
    before any count, number or data)."""
    code = _csharp()
    for n, text in _lesson_texts().items():
        for item in re.findall(r'V° ([A-Z][^*`\]\n]+)', text):
            head = re.split(r' \(|\s+\d| --', item)[0].strip()
            assert ('Menu("' + head) in code or ('Add("' + head) in code, 'GH %d quotes "V° %s" -- not a menu item in the code' % (n, head)


def test_gh_links_name_generated_objects():
    """[text](gh:Name) and [text](<gh:Name With Spaces>): a nickname the generator gives."""
    generator = _generator()
    for n, text in _lesson_texts().items():
        text = re.sub(r'`[^`\n]*`', '', text)  # a link written as `code` explains the syntax, it goes nowhere
        names = re.findall(r'\]\(<gh:([^>]+)>\)', text) + re.findall(r'\]\(gh:([^)\s]+)\)', text)
        for name in names:
            if '#' in name:
                name = name.split('#', 1)[1]  # a file before the name: the name's object
            literal = name.replace('°', '\\u00b0')
            assert ("'%s'" % name in generator or '"%s"' % name in generator or "'%s'" % literal in generator), \
                'GH %d links gh:%s -- no object of that nickname in the generator' % (n, name)


def test_guide_lists_every_lesson():
    guide = _read(GUIDE)
    examples = os.path.join(ROOT, 'examples')
    lessons = [f for f in os.listdir(examples) if re.match(r'\d\d_.*\.py$', f) and '_tools' not in f]
    lessons += [f for f in os.listdir(os.path.join(examples, 'grasshopper')) if f.endswith('.gh') and not f.startswith('gh0_')]
    assert len(lessons) == 17 + 12
    for lesson in lessons:
        assert lesson in guide, '%s isn\'t in the guide' % lesson


def test_python_lessons_share_the_structure():
    examples = os.path.join(ROOT, 'examples')
    for name in sorted(os.listdir(examples)):
        if not re.match(r'\d\d_.*\.py$', name) or '_tools' in name:
            continue
        text = _read(os.path.join(examples, name))
        for heading in ('The bug', 'Try this', 'Without miniVivisection'):
            assert heading in text, '%s: no "%s"' % (name, heading)


def _list_items(text):
    """The words each list item starts with: Markdown "- ", "1. ", task boxes and links' brackets dropped."""
    items = re.findall(r'^\s*(?:-|\d+\.) (?:\[[ x]\] )?\[?(.)', text, re.M)
    return items


def test_list_items_start_with_a_capital():
    """Every list item in the lessons (GH texts, their Panels, the Rhino example, the Python
    lessons' steps) starts with a capital letter -- or with code (`x`) or a number."""
    rhino = _read(os.path.join(ROOT, 'tools', 'make_rhino_examples.py'))
    texts = {'make_gh_examples.py': _generator(), 'make_rhino_examples.py': rhino}
    examples = os.path.join(ROOT, 'examples')
    for name in os.listdir(examples):
        if re.match(r'\d\d_.*\.py$', name):
            texts[name] = _read(os.path.join(examples, name)).split('"""')[1]  # the docstring
    for name, text in texts.items():
        for first in _list_items(text):
            assert not first.islower(), '%s: a list item starts with "%s"' % (name, first)


def test_fix_steps_say_fix_it():
    """The step with the fix says "Fix it:" -- not "Fix:" -- in every lesson."""
    examples = os.path.join(ROOT, 'examples')
    texts = [_generator()] + [_read(os.path.join(examples, n)) for n in os.listdir(examples) if re.match(r'\d\d_.*\.py$', n)]
    for text in texts:
        assert not re.search(r'\bFix:', text), 'a lesson says "Fix:"'
