"""The overview of all of miniVivisection's examples, as Markdown with links: the Python lessons
(examples/NN_*.py, opened in the Script Editor), the Grasshopper lessons (examples/grasshopper/
gh*.gh), the Markdown text example (in examples/00_overview.3dm, right of the overview) and the
two overviews themselves.

Two places show it, so its links are relative to two folders:
    markdown('rhino')   for examples/00_overview.3dm (MarkdownText; tools/make_rhino_examples.py)
    markdown('gh')      for examples/grasshopper/gh0_overview.gh (a Panel; tools/make_gh_examples.py)

The titles come from the lessons themselves: a Python lesson's docstring's first line, a
Grasshopper lesson's heading in tools/make_gh_examples.py. Plain Python, no Rhino needed.
"""
import ast
import os
import re

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXAMPLES = os.path.join(HERE, 'examples')
RHINO_FILE = '00_overview.3dm'
GH_FILE = 'gh0_overview.gh'


def python_lessons():
    """[(file, title)]: "01_watch_geometry.py", "01 -- See your geometry in Rhino: the V°Watch tab"."""
    lessons = []
    for name in sorted(os.listdir(EXAMPLES)):
        if not (name.endswith('.py') and name[:2].isdigit()) or '_tools' in name:
            continue
        with open(os.path.join(EXAMPLES, name), encoding='utf-8') as f:
            text = f.read()
        match = re.search(r'r?"""(.+)', text)
        lessons.append((name, match.group(1).strip() if match else name))
    return lessons


def gh_lessons():
    """[(file, title)] in order: "gh1_which_branch_failed.gh", "GH 1 -- Which branch failed"."""
    with open(os.path.join(HERE, 'tools', 'make_gh_examples.py'), encoding='utf-8') as f:
        source = f.read()
    files = ast.literal_eval(re.search(r'^LESSON_FILES = (\[.*?\])', source, re.M | re.S).group(1))
    titles = dict((int(n), t.strip()) for n, t in re.findall(r'^GH(\d+)_TEXT = """# (?:GH \d+ -- )?(.+)$', source, re.M))
    return [(name, 'GH %d -- %s' % (i + 1, titles.get(i + 1, name))) for i, name in enumerate(files)]


def markdown(where):
    """The overview's Markdown for 'rhino' (examples/) or 'gh' (examples/grasshopper/)."""
    gh = where == 'gh'
    up = '../' if gh else ''              # from the definition's folder to examples/
    ghdir = '' if gh else 'grasshopper/'  # from here to examples/grasshopper/
    click = 'Click'  # setting "Markdown links: follow with": its default, in Rhino and Grasshopper
    lines = ['# miniVivisection -- all examples', '']
    lines.append('%s a link: it opens the lesson -- a script in the Script Editor, a definition in Grasshopper '
                 '(or shows it when it is open), a model in Rhino.' % click)
    lines += ['', '## Python lessons in the Script Editor', '']
    lines.append('Each one starts with its bug and what to try, in its text at the top.')
    lines.append('')
    for name, title in python_lessons():
        lines.append('- [%s](%s%s)' % (title, up, name))
    lines += ['', '## Grasshopper lessons', '']
    lines.append('Each definition explains itself in a Markdown Panel, with links to the lesson before and after.')
    lines.append('')
    for name, title in gh_lessons():
        lines.append('- [%s](%s%s)' % (title, ghdir, name))
    lines += ['', '## Rhino', '']
    if gh:
        lines.append('- [Markdown text in Rhino](rhinoview:../%s#MarkdownText) -- the *MarkdownText* command, in Rhino\'s overview: '
                     'headings, lists, task boxes, tables, links; [its V\u00b0 logo](rhino:../%s#Logo)' % (RHINO_FILE, RHINO_FILE))
    else:
        lines.append('- [Markdown text in Rhino](rhinoview:MarkdownText) -- right of this list: the *MarkdownText* command -- '
                     'headings, lists, task boxes, tables, links; [its V\u00b0 logo](rhino:Logo); [all of it](rhinoview:Overview)')
    if gh:
        lines.append('- [This overview in Rhino](../%s) -- the same links as Markdown text in a model' % RHINO_FILE)
    else:
        lines.append('- [This overview in Grasshopper](grasshopper/%s) -- the same links in a Panel, '
                     'and **V°Protect** guarding the definition' % GH_FILE)
    if gh:
        lines += ['', '## V°Protect', '']
        lines.append('Under this Panel: **V°Protect**, protecting this definition (and every lesson). '
                     '*original*, green: the definition is as shipped.')
        lines.append('')
        lines.append('This definition is **{{protected}}**, and **{{modified}}**.')
        lines.append('')
        lines.append('1. Move the [Try me](gh:Try%20me) slider: V°Protect turns red, *modified* -- so does the line above.')
        lines.append('2. Click **Revert** on it, or [revert here](protect:revert): the definition opens again as it was shipped.')
        lines.append('3. Right-click it > *Back to setup*: its files (by default this definition) and where their '
                     'originals go (here: in the definition itself). *Protect* again when done.')
    if not gh:
        lines += ['', '## ProtectModel', '']
        lines.append('This file is **{{protected}}**, and **{{modified}}** -- *ProtectModel* keeps its original in the file itself.')
        lines.append('')
        lines.append('1. Move something (the V\u00b0 logo, say): after a moment this line says *modified*.')
        lines.append('2. [Revert to the original](protect:revert): the file opens again as it was shipped.')
        lines.append('3. Run *ProtectModel* > *BackToSetup*: its files (by default this file) and where their '
                     'originals go (here: in the file itself). *Protect* again when done.')
        lines.append('')
        lines.append('In Markdown: `{{protected}}` and `{{modified}}` show the state, `[text](protect:revert)` reverts -- '
                     'in Rhino and in a Panel (with V°Protect) alike.')
    lines += ['', '## Links', '']
    lines.append('| Link | Goes to |')
    lines.append('|------|---------|')
    lines.append('| `[text](lesson.py)` | the script in the Script Editor |')
    lines.append('| `[text](lesson.gh)` | the definition in Grasshopper |')
    lines.append('| `[text](model.3dm)` | the model in Rhino |')
    lines.append('| `gh:Name`, `ghview:Name` | a component, a view of the canvas |')
    lines.append('| `rhino:Name`, `rhinoview:Name` | a Rhino object, a named view |')
    lines.append('| `protect:revert` | the protected original, put back |')
    lines.append('')
    if gh:
        lines.append('Links are blue, with a line under them. Their color -- and the sizes, fonts and spacing of all of it -- is '
                     'the Markdown style: V°Settings > *Markdown* > *Markdown style for Grasshopper* (*Edit*). '
                     '*Markdown links in color* there switches the color off.')
    else:
        lines.append('Links are blue, with a line under them. Their color -- and the sizes, fonts and spacing of all of it -- is '
                     'this file\'s Markdown style: *MarkdownText* > *Edit style...* (a new file starts with V°Settings > *Markdown* > '
                     '*Markdown style for Rhino*). V°Settings > *Markdown* > *Markdown links in color* switches the color off.')
    return '\n'.join(lines) + '\n'


if __name__ == '__main__':
    print(markdown('rhino'))
    print(markdown('gh'))
