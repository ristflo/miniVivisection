#! python 3
# MarkdownText: Markdown as Rhino objects. The example (in examples/00_overview.3dm, right of the overview) made with it
# by tools/make_rhino_examples.py and saved; its objects -- text with rich text (bold, code in
# Consolas), lines, the frame of the code block --, one group, the Markdown kept on every object;
# edited again: the same objects updated in place; the command selecting the whole text, and
# editing from a file. An image of the view for a look.
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino


def test():
    yield 2
    import make_rhino_examples as mk
    doc = Rhino.RhinoDoc.ActiveDoc
    ids = mk.make(doc)
    yield 1
    lt.note('%d objects' % len(ids))
    objects = [doc.Objects.FindId(i) for i in ids]
    texts = [o for o in objects if isinstance(o.Geometry, Rhino.Geometry.TextEntity)]
    lines = [o for o in objects if isinstance(o.Geometry, Rhino.Geometry.LineCurve)]
    task_key = 'minivivisection.markdown.task'
    frames = [o for o in objects if isinstance(o.Geometry, Rhino.Geometry.PolylineCurve) and o.Attributes.GetUserString(task_key) is None]
    lt.check('made: texts, lines, the code block\'s frame', (len(texts) > 15, len(lines) > 5, len(frames)), (True, True, 1))
    plain = [t.Geometry.PlainText for t in texts]
    lt.check('the heading', any('Markdown text in Rhino' in p for p in plain), True)
    lt.check('spaces kept where the style changes', (any('run MarkdownText on it' in p for p in plain), any('the MarkdownText command from' in p for p in plain)), (True, True))
    lt.check('bold in the rich text', any('\\b' in t.Geometry.RichText for t in texts), True)
    lt.check('code in Consolas', any('Consolas' in t.Geometry.RichText for t in texts), True)
    # Task boxes drawn (no glyphs: a square each, a check mark in the done one), each knowing its place in the Markdown
    boxes = [o for o in objects if o.Attributes.GetUserString(task_key) is not None]
    squares = [o for o in boxes if o.Geometry.IsClosed]
    lt.check('the task boxes: two squares, one check mark, no glyphs', (len(squares), len(boxes) - len(squares), any(box in p for p in plain for box in ('\u2611', '\u2610'))), (2, 1, False))
    if squares:
        square = squares[0].Geometry.GetBoundingBox(True)
        lt.check('a box square', abs((square.Max.X - square.Min.X) - (square.Max.Y - square.Min.Y)) < 1e-6, True)
    # Where the example's two tasks are (line;column of the box's ' ' or 'x';done)
    task_lines = [i for i, line in enumerate(mk.MARKDOWN.split('\n')) if line.startswith('- [')]
    done_box, open_box_key = '%d;3;1' % task_lines[0], '%d;3;0' % task_lines[1]
    lt.check('the boxes\' places in the Markdown', sorted(o.Attributes.GetUserString(task_key) for o in squares), [done_box, open_box_key])
    lt.check('the quote\'s bar: a \'|\' text', any(p.strip() == '|' for p in plain), True)
    lt.check('the table\'s cells', all(any(cell in p for p in plain) for cell in ('Markdown', 'Rhino object', 'Text in a frame')), True)
    groups = set(tuple(o.Attributes.GetGroupList() or []) for o in objects)
    lt.check('one group', len(groups) == 1 and len(next(iter(groups))) == 1, True)
    lt.check('the Markdown kept on every object', all(o.Attributes.GetUserString('minivivisection.markdown') == mk.MARKDOWN for o in objects), True)
    # Links: no underline in the rich text; a bar under each -- a solid hatch, 10 % of the text
    # high (the setting's default), under the descenders, in the link color -- and their letters
    # drawn filled in the link color over the text (MarkdownLinkLetters: no objects)
    bars = [o for o in objects if isinstance(o.Geometry, Rhino.Geometry.Hatch)]
    letter_of = lambda part: list(lt.plugin_type('MarkdownLinkLetters').GetMethod('LettersOf', flags).Invoke(None, [doc, part]))
    flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    letters = [h for t in texts for h in letter_of(t)]
    lt.check('links not underlined', any('\\ul' in t.Geometry.RichText for t in texts), False)
    lt.check('a bar under the links (at least one per link line)', len(bars) >= 8, True)
    lt.check('the links\' letters drawn filled (more than three per bar), none made objects', (len(letters) > 3 * len(bars), len(bars) < 30), (True, True))
    if bars:
        box = bars[0].Geometry.GetBoundingBox(True)
        height = min(t.Geometry.TextHeight for t in texts)
        lt.note('a bar: %.4f high, the text %.4f' % (box.Max.Y - box.Min.Y, height))
        lt.check('the bar 10 % of the text high (of a text height used)', any(abs((box.Max.Y - box.Min.Y) - 0.10 * t.Geometry.TextHeight) < 0.005 * t.Geometry.TextHeight for t in texts), True)
        # Exactly under its link: from its first letter to its last, its top 15 % of the text height
        # (the setting "Markdown links: line distance") under the baseline -- the first link, "links",
        # has no descenders: its letters' bottoms are the baseline
        above = [letter.GetBoundingBox(True) for letter in letters]
        above = [b for b in above if b.Max.X > box.Min.X - 0.05 * height and b.Min.X < box.Max.X + 0.05 * height
                 and b.Min.Y > box.Max.Y - 0.5 * height and b.Min.Y < box.Max.Y + height]
        if above:
            text_height = (box.Max.Y - box.Min.Y) / 0.10  # the bar is 10 % of it (checked above)
            baseline = max(b.Min.Y for b in above)
            lt.note('bar %.3f..%.3f, its letters %.3f..%.3f; its top %.3f under the baseline' % (
                box.Min.X, box.Max.X, min(b.Min.X for b in above), max(b.Max.X for b in above), baseline - box.Max.Y))
            lt.check('the bar as long as its link\'s letters', abs(box.Min.X - min(b.Min.X for b in above)) < 0.02 * text_height and abs(box.Max.X - max(b.Max.X for b in above)) < 0.02 * text_height, True)
            lt.check('its top 15 % of the text height under the baseline', abs((baseline - box.Max.Y) - 0.15 * text_height) < 0.03 * text_height, True)
        else:
            lt.check('letters above the first bar', 'none', 'some')
        pattern = doc.HatchPatterns.FindIndex(bars[0].Geometry.PatternIndex)
        lt.check('a solid hatch', pattern is not None and pattern.FillType == Rhino.DocObjects.HatchPatternFillType.Solid, True)
        style_of = lt.plugin_type('MarkdownStyleFiles').GetMethod('ForDocument', System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        color = style_of.Invoke(None, [doc]).LinkColor
        lt.check('in the link color (the style: blue)', (bars[0].Attributes.ObjectColor.ToArgb() == color.ToArgb(), bars[0].Attributes.ColorSource == Rhino.DocObjects.ObjectColorSource.ColorFromObject), (True, True))
    lt.check('saved: the example', os.path.exists(mk.PATH), True)
    lt.check('the targets of its links saved with it: the named view, the logo',
             (doc.NamedViews.FindByName('Overview') >= 0, any(o.Attributes.Name == 'Logo' for o in doc.Objects)), (True, True))

    picture = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'markdown_text.png')
    try:
        bitmap = doc.Views.ActiveView.CaptureToBitmap()
        if bitmap is not None:
            bitmap.Save(picture)
            lt.note('image: ' + picture)
    except Exception as e:
        lt.note('no image: %s' % e)

    # Edited again (what MarkdownText does with the dialog's text): replaced where it was
    builder = lt.plugin_type('MarkdownRhinoText')
    flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    found = builder.GetMethod('Find', flags).Invoke(None, [doc, objects[0]])
    lt.check('found from any of its objects', found is not None and len(list(found.Parts)) == len(objects), True)
    edited = mk.MARKDOWN.replace('# Markdown text in Rhino', '# Markdown text in Rhino, edited')
    new_ids = list(builder.GetMethod('Update', flags).Invoke(None, [doc, found, edited, found.Frame]))
    lt.check('edited: the same objects, updated in place', sorted(str(i) for i in new_ids), sorted(str(i) for i in ids))
    new_texts = [doc.Objects.FindId(i) for i in new_ids if isinstance(doc.Objects.FindId(i).Geometry, Rhino.Geometry.TextEntity)]
    lt.check('edited: the new heading', any('edited' in t.Geometry.PlainText for t in new_texts), True)
    first_old = texts[0].Geometry.Plane.Origin
    first_new = new_texts[0].Geometry.Plane.Origin
    lt.check('edited: in the same place', first_old.DistanceTo(first_new) < 1e-6, True)

    # The command on one of its objects, selected alone: the whole text selected while it's
    # edited (-MarkdownText, Enter: the Markdown kept)
    doc.Objects.UnselectAll()
    doc.Objects.FindId(new_ids[3]).Select(True)
    Rhino.RhinoApp.RunScript('_-MarkdownText _Enter', False)
    yield 1
    lt.check('one object selected: the whole text then', all(doc.Objects.FindId(i).IsSelected(False) > 0 for i in new_ids), True)
    # Edited from a file: the same objects, selected
    path = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'markdown_text_edit.md')
    with open(path, 'w', encoding='utf-8') as f:
        f.write(edited.replace('edited', 'edited twice'))
    Rhino.RhinoApp.RunScript('_-MarkdownText "%s"' % path, False)
    yield 1
    twice = [o for o in doc.Objects if o.Attributes.GetUserString('minivivisection.markdown') and 'edited twice' in o.Attributes.GetUserString('minivivisection.markdown')]
    lt.check('edited from a file: all of it selected', len(twice) == len(new_ids) and all(o.IsSelected(False) > 0 for o in twice), True)
    lt.check('edited from a file: the same objects', sorted(str(o.Id) for o in twice), sorted(str(i) for i in new_ids))

    # A task box clicked: checked in the Markdown, the text updated (a check mark more), one undo step
    open_box = next(o for o in twice if o.Attributes.GetUserString(task_key) == open_box_key)
    lt.check('task: checked', builder.GetMethod('ToggleTask', flags).Invoke(None, [doc, open_box]), None)
    source = doc.Objects.FindId(open_box.Id).Attributes.GetUserString('minivivisection.markdown')
    marks = [o for o in doc.Objects if o.Attributes.GetUserString(task_key) is not None]
    lt.check('task: its Markdown, both boxes checked now', ('- [x] An open task' in source, len([o for o in marks if not o.Geometry.IsClosed])), (True, 2))
    doc.Undo()
    yield 1
    source = doc.Objects.FindId(open_box.Id).Attributes.GetUserString('minivivisection.markdown')
    lt.check('task: undone', ('- [ ] An open task' in source, 'edited twice' in source), (True, True))

    # The file's Markdown style: kept with it (a copy of the default), one for all its texts;
    # changed, all of them drawn again with it
    style_type = lt.plugin_type('MarkdownStyle')
    kept = doc.Strings.GetValue('minivivisection.markdown.css')
    lt.check('style: the file keeps a copy of the default', (kept is not None, kept is not None and style_type.GetMethod('IsPristineDefault', flags).Invoke(None, [kept])), (True, True))

    def heading_height():
        heights = [o.Geometry.TextHeight for o in doc.Objects if isinstance(o.Geometry, Rhino.Geometry.TextEntity) and 'Markdown text in Rhino' in o.Geometry.PlainText]
        return max(heights) if heights else 0
    before = heading_height()
    bigger = (kept or '').replace('h1 { font-size: 1.8em;', 'h1 { font-size: 2.7em;')
    lt.check('style: changed, it is the file\'s own', style_type.GetMethod('IsPristineDefault', flags).Invoke(None, [bigger]), False)
    lt.plugin_type('MarkdownStyleFiles').GetMethod('Keep', flags).Invoke(None, [doc, bigger, 'test.css'])
    count = builder.GetMethod('Restyle', flags).Invoke(None, [doc, System.Guid.Empty])
    lt.check('style: its texts drawn again (the overview and the example) -- H1 1.5 times as high', (count, round(heading_height() / before, 3) if before else 0), (2, 1.5))
    lt.check('style: the file keeps it', doc.Strings.GetValue('minivivisection.markdown.css') == bigger, True)

    # Its links into Grasshopper: GH 11 opened (Grasshopper started for it), its Ring marked,
    # then its named view -- from the example's folder, as a double-click in it would
    links = lt.plugin_type('MarkdownLinks')
    follow = lambda url: links.GetMethod('Follow', flags).Invoke(None, [url, os.path.dirname(mk.PATH)])
    lt.check('gh:grasshopper/gh11_markdown_notes.gh#Ring: followed', follow('gh:grasshopper/gh11_markdown_notes.gh#Ring'), None)
    yield 2
    import Grasshopper
    canvas = Grasshopper.Instances.ActiveCanvas
    lt.check('Grasshopper opened GH 11', os.path.basename(canvas.Document.FilePath or ''), 'gh11_markdown_notes.gh')
    gh_links = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.GhLinks')
    marked = [m.NickName for m in gh_links.GetProperty('Marked', flags).GetValue(None)]
    lt.check('its Ring marked', 'Ring' in marked, True)
    lt.check('ghview:...#Overview: followed (GH 11 already open: not again)', follow('ghview:grasshopper/gh11_markdown_notes.gh#Overview'), None)
    yield 1
    middle = canvas.Viewport.MidPoint
    lt.check('its named view restored', (abs(middle.X - 640) < 5, abs(middle.Y - 420) < 5, len([d for d in Grasshopper.Instances.DocumentServer if (d.FilePath or '').endswith('gh11_markdown_notes.gh')])), (True, True, 1))
    lt.check('rhinoview:Perspective: the viewport by its title', (follow('rhinoview:Perspective'), doc.Views.ActiveView.ActiveViewport.Name), (None, 'Perspective'))
    lt.check('a relative file without the folder: said', 'save the file' in (links.GetMethod('Follow', flags).Invoke(None, ['gh:grasshopper/gh11_markdown_notes.gh#Ring', None]) or ''), True)


lt.run('t_markdowntext', test)
