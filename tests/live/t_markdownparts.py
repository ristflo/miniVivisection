#! python 3
# A Markdown text's parts (MarkdownRhinoText): linked by the text's id -- found whole also
# ungrouped, a copy's parts its own --; changed in Rhino (a paragraph's words, a block deleted,
# all moved) and taken back into the Markdown (Reconcile), the picked part's section; updated
# in place -- the same objects --; the part under a double-click (MarkdownTextMouse.Hit, and a
# real double-click message to the view) and the
# link under it (its glyph); a click in the command's pick taking the part (with group select, as
# before, it took nothing); links followed (MarkdownLinks): a named view, a named object marked.
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino
from Rhino.Geometry import Point3d, Vector3d, Transform, TextEntity

STATIC = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static

MARKDOWN = """# Parts and links

A **paragraph** with a [named view](rhinoview:Overview) and a [circle](rhino:MarkedCircle), written to be edited.

> A quote to delete.

- one item
- two items
"""

ID = 'minivivisection.markdown.id'
PART = 'minivivisection.markdown.part'


def block_of(o):
    return int(o.Attributes.GetUserString(PART).split(';')[1])


def test():
    yield 2
    import make_rhino_examples as mk
    doc = Rhino.RhinoDoc.ActiveDoc
    builder = lt.plugin_type('MarkdownRhinoText')
    call = lambda name, *args: builder.GetMethod(name, STATIC).Invoke(None, list(args))
    folder = os.path.join(tempfile.gettempdir(), 'minivivisection-live')
    os.makedirs(folder, exist_ok=True)

    ids = mk.create(doc, MARKDOWN, mk.frame(1.0, 60.0))
    objects = [doc.Objects.FindId(i) for i in ids]
    lt.check('every part keeps the text id and its place', all(o.Attributes.GetUserString(ID) and o.Attributes.GetUserString(PART) for o in objects), True)
    lt.check('one text id', len(set(o.Attributes.GetUserString(ID) for o in objects)), 1)
    paragraph = next(o for o in objects if isinstance(o.Geometry, TextEntity) and 'paragraph' in o.Geometry.PlainText)
    links = paragraph.Attributes.GetUserString('minivivisection.markdown.links') or ''
    lt.check('the paragraph keeps its characters and links',
             (paragraph.Attributes.GetUserString('minivivisection.markdown.plain') is not None, 'rhinoview:Overview' in links, 'rhino:MarkedCircle' in links), (True, True, True))

    # Ungrouped: still found whole, by the id
    for o in objects:
        attributes = o.Attributes.Duplicate()
        attributes.RemoveFromAllGroups()
        doc.Objects.ModifyAttributes(o.Id, attributes, True)
    text = call('Find', doc, doc.Objects.FindId(paragraph.Id))
    lt.check('ungrouped: found whole by its id', len(list(text.Parts)), len(ids))

    # Changed in Rhino: the paragraph's words, the quote's parts deleted, all moved
    edited = doc.Objects.FindId(paragraph.Id).Geometry.Duplicate()
    edited.RichText = edited.RichText.replace('written', 'typed')
    lt.check('a word changed with Rhino\'s text', 'typed' in edited.PlainText, True)
    doc.Objects.Replace(paragraph.Id, edited)
    quote = next(o for o in objects if isinstance(o.Geometry, TextEntity) and 'quote' in o.Geometry.PlainText)
    quote_block = block_of(quote)
    gone = [o.Id for o in objects if block_of(o) == quote_block]
    for i in gone:
        doc.Objects.Delete(i, True)
    move = Transform.Translation(10, 5, 0)
    left = [i for i in ids if i not in gone]
    for i in left:
        doc.Objects.Transform(i, move, True)
    lt.check('moved: the same objects', all(doc.Objects.FindId(i) is not None for i in left), True)
    text = call('Find', doc, doc.Objects.FindId(paragraph.Id))
    reconciled = call('Reconcile', doc, text)
    source, frame, notes, start, end = reconciled.Item1, reconciled.Item2, list(reconciled.Item3), reconciled.Item4, reconciled.Item5
    lt.note('notes: %s' % notes)
    lt.check('the words taken into the Markdown, its formatting and links kept',
             ('typed to be edited' in source, 'A **paragraph** with a [named view](rhinoview:Overview)' in source), (True, True))
    lt.check('the deleted quote taken out', 'A quote to delete' in source, False)
    lt.check('moved: the frame follows', frame.Plane.Origin.DistanceTo(Point3d(10, 5, 0)) < 1e-6, True)
    lt.check('three notes', len(notes), 3)
    lt.check('the picked part\'s section', source[start:end].startswith('A **paragraph**'), True)
    # The dialog's text box: the caret at that section's start (not at the end of the line before)
    command = lt.plugin_type('MarkdownTextCommand')
    area = command.GetMethod('EditorArea', STATIC).Invoke(None, [source, start, end])
    command.GetMethod('PlaceCaret', STATIC).Invoke(None, [area, start])
    lt.check('the dialog\'s caret at the section', area.CaretIndex, start)

    # Updated in place: the same objects, the words in them
    new_ids = list(call('Update', doc, text, source, frame))
    lt.check('updated in place: no new object', all(str(i) in set(str(j) for j in left) for i in new_ids), True)
    texts = [doc.Objects.FindId(i) for i in new_ids if isinstance(doc.Objects.FindId(i).Geometry, TextEntity)]
    lt.check('the words in the text', any('typed to be edited' in t.Geometry.PlainText for t in texts), True)
    lt.check('no quote left', any('quote' in t.Geometry.PlainText for t in texts), False)
    again = call('Reconcile', doc, call('Find', doc, texts[0]))
    lt.check('afterwards nothing to take', (len(list(again.Item3)), again.Item1 == source), (0, True))

    # The view, for the mouse
    box = Rhino.Geometry.BoundingBox.Empty
    for i in new_ids:
        box.Union(doc.Objects.FindId(i).Geometry.GetBoundingBox(True))
    box.Inflate(5)
    view = doc.Views.Find('Top', False) or doc.Views.ActiveView
    doc.Views.ActiveView = view
    view.Maximized = True
    view.ActiveViewport.ZoomBoundingBox(box)
    doc.Views.Redraw()
    yield 1
    mouse = lt.plugin_type('MarkdownTextMouse')
    lt.static_call('MiniVivisectionSettings', 'Set', 'MarkdownLinkFollow', 'Click')  # the default
    hit = lambda point: mouse.GetMethod('Hit', STATIC).Invoke(None, [view, point])

    def screen(p):
        s = view.ActiveViewport.WorldToClient(p)
        return System.Drawing.Point(int(round(s.X)), int(round(s.Y)))

    paragraph = next(t for t in texts if 'paragraph' in t.Geometry.PlainText)
    middle = paragraph.Geometry.GetBoundingBox(True).Center
    found = hit(screen(middle))
    lt.check('a double-click on the paragraph: it', found is not None and str(found.Item1.Id) == str(paragraph.Id), True)
    lt.check('nothing far away', hit(screen(Point3d(box.Max.X + 50, box.Max.Y + 50, 0))), None)
    glyphs = mouse.GetMethod('GlyphsOf', STATIC).Invoke(None, [doc, paragraph])
    lt.check('a glyph for each character that isn\'t blank', glyphs.Item1, glyphs.Item2)
    first_link = paragraph.Attributes.GetUserString('minivivisection.markdown.links').split('\n')[0].split(' ')
    style = doc.DimStyles.FindId(paragraph.Geometry.DimensionStyleId) or doc.DimStyles.Current
    groups = list(paragraph.Geometry.CreateCurvesGrouped(style, False, 1.0, 0.0))
    glyph_box = Rhino.Geometry.BoundingBox.Empty
    for curve in groups[int(first_link[0]) + 1]:
        glyph_box.Union(curve.GetBoundingBox(True))
    found = hit(screen(glyph_box.Center))
    lt.check('a double-click on the link: its target', found is not None and found.Item2 == first_link[2], True)

    # A real double-click (Windows' message to the view): taken by MarkdownTextMouse, Rhino's own action cancelled
    import ctypes
    seen = []
    probe = mouse.GetField('Probe', STATIC)
    probe.SetValue(None, System.Action[Rhino.DocObjects.RhinoObject, System.String](lambda part, url: seen.append((str(part.Id), url))))
    try:
        hwnd = view.Handle
        first_glyph = Rhino.Geometry.BoundingBox.Empty  # 'A', no link
        for curve in groups[0]:
            first_glyph.Union(curve.GetBoundingBox(True))
        for p, button in ((screen(first_glyph.Center), 0x0203), (screen(glyph_box.Center), 0x0203)):
            lparam = (p.Y << 16) | (p.X & 0xFFFF)
            ctypes.windll.user32.SendMessageW(ctypes.c_void_p(hwnd.ToInt64()), 0x0201, 1, lparam)  # down
            ctypes.windll.user32.SendMessageW(ctypes.c_void_p(hwnd.ToInt64()), 0x0202, 0, lparam)  # up
            ctypes.windll.user32.SendMessageW(ctypes.c_void_p(hwnd.ToInt64()), button, 1, lparam)  # double-click
            ctypes.windll.user32.SendMessageW(ctypes.c_void_p(hwnd.ToInt64()), 0x0202, 0, lparam)  # up
            yield 1
        lt.note('double-clicks seen: %s' % seen)
        lt.check('a real double-click: the paragraph, then its link', [s[1] for s in seen], [None, first_link[2]])
        # The mouse over the link: a hint by it, gone off it
        link_point = screen(glyph_box.Center)
        ctypes.windll.user32.SendMessageW(ctypes.c_void_p(hwnd.ToInt64()), 0x0200, 0, (link_point.Y << 16) | (link_point.X & 0xFFFF))
        yield 1
        hint = mouse.GetProperty('Hint', STATIC).GetValue(None)
        lt.note('hint: %s' % hint)
        lt.check('over the link: a hint where it goes, and click (the default)', (hint or '').startswith('Rhino: the view Overview') and hint.endswith('-- click (double-click: edit)'), True)
        try:
            view.CaptureToBitmap().Save(os.path.join(folder, 'markdown_link_hint.png'))
            lt.note('image: ' + os.path.join(folder, 'markdown_link_hint.png'))
        except Exception as e:
            lt.note('no image: %s' % e)
        time_point = screen(first_glyph.Center)
        System.Threading.Thread.Sleep(80)
        ctypes.windll.user32.SendMessageW(ctypes.c_void_p(hwnd.ToInt64()), 0x0200, 0, (time_point.Y << 16) | (time_point.X & 0xFFFF))
        yield 1
        lt.check('off the link: no hint', mouse.GetProperty('Hint', STATIC).GetValue(None), None)

        # Following a link, as "Markdown links: follow with" says
        followed = []
        follow_probe = mouse.GetField('FollowProbe', STATIC)
        follow_probe.SetValue(None, System.Action[System.String](lambda url: followed.append(url)))
        send = lambda message, wparam, p: ctypes.windll.user32.SendMessageW(ctypes.c_void_p(hwnd.ToInt64()), message, wparam, (p.Y << 16) | (p.X & 0xFFFF))

        def click(p, ctrl=False):
            send(0x0201, 0x0009 if ctrl else 0x0001, p)  # down (MK_LBUTTON, MK_CONTROL)
            send(0x0202, 0x0008 if ctrl else 0, p)       # up

        def hover_hint(setting):
            lt.static_call('MiniVivisectionSettings', 'Set', 'MarkdownLinkFollow', setting)
            send(0x0200, 0, time_point)
            System.Threading.Thread.Sleep(80)
            send(0x0200, 0, link_point)
            return mouse.GetProperty('Hint', STATIC).GetValue(None) or ''

        try:
            off_hint = hover_hint('Off')
            yield 1
            lt.note('hint, off: %s' % off_hint)
            lt.check('off: the hint says where to switch links on', 'following links is off' in off_hint and 'Markdown links: follow with' in off_hint, True)
            click(link_point)
            click(link_point, ctrl=True)
            yield 1.5
            lt.check('off: neither a click nor a Ctrl+click follows', followed, [])
            lt.check('click: the hint says click', hover_hint('Click').endswith('-- click (double-click: edit)'), True)
            click(link_point)
            yield 0.2
            lt.check('click: not before the double-click time', followed, [])
            yield 1.5
            lt.check('click: followed after it', followed, [first_link[2]])
            del followed[:]
            del seen[:]
            click(time_point)
            yield 1.5
            lt.check('click: not off the link', followed, [])
            send(0x0201, 0x0001, link_point)
            send(0x0202, 0, link_point)
            send(0x0203, 0x0001, link_point)  # double-click
            send(0x0202, 0, link_point)
            yield 1.5
            lt.check('click: a double-click on the link edits, not follows', (followed, [s[1] for s in seen]), ([], [first_link[2]]))
            hover_hint('Ctrl+click')
            click(link_point)
            yield 1.5
            lt.check('Ctrl+click: a plain click doesn\'t follow', followed, [])
            click(link_point, ctrl=True)
            yield 1
            lt.note('Ctrl+click followed: %s' % followed)
            lt.check('Ctrl+click: followed at once', followed, [first_link[2]])
        finally:
            follow_probe.SetValue(None, None)
            lt.static_call('MiniVivisectionSettings', 'Set', 'MarkdownLinkFollow', 'Click')
    finally:
        probe.SetValue(None, None)
        doc.Objects.UnselectAll()

    # A click in the command's pick (a GetObject as MarkdownText's: not group select) takes the
    # part clicked; with group select (as before) the click on a grouped part took nothing
    grouped = [i for i in new_ids if doc.Objects.FindId(i) is not None]
    doc.Groups.Add(grouped)
    from Rhino.Input.Custom import GetObject
    from Rhino.Input import GetResult
    picks = []
    for group_select in (False, True):
        go = GetObject()
        go.SetCommandPrompt('test pick')
        go.EnablePreSelect(False, True)
        go.AcceptNothing(True)
        go.GroupSelect = group_select
        p = screen(first_glyph.Center)
        lparam = (p.Y << 16) | (p.X & 0xFFFF)
        handle = ctypes.c_void_p(view.Handle.ToInt64())
        ctypes.windll.user32.PostMessageW(handle, 0x0200, 0, lparam)  # move there
        ctypes.windll.user32.PostMessageW(handle, 0x0201, 1, lparam)
        ctypes.windll.user32.PostMessageW(handle, 0x0202, 0, lparam)
        import threading
        escape = threading.Timer(2.0, lambda: ctypes.windll.user32.PostMessageW(handle, 0x0100, 0x1B, 0))  # Esc: ends it when the click took nothing
        escape.start()
        result = go.Get()
        escape.cancel()
        picks.append((str(result), str(go.Object(0).ObjectId) if result == GetResult.Object else None))
        doc.Objects.UnselectAll()
        yield 1
    lt.note('picks: %s' % picks)
    lt.check('a click in the command: the part clicked', picks[0], ('Object', str(paragraph.Id)))
    lt.check('with group select (how it was): the click took nothing', picks[1], ('Cancel', None))

    # Links followed: a named view, a named object marked
    links = lt.plugin_type('MarkdownLinks')
    follow = lambda url: links.GetMethod('Follow', STATIC).Invoke(None, [url, None])
    doc.NamedViews.Add('Overview', view.ActiveViewportID)
    circle_attributes = doc.CreateDefaultAttributes()
    circle_attributes.Name = 'MarkedCircle'
    doc.Objects.AddCircle(Rhino.Geometry.Circle(Point3d(box.Max.X + 20, 0, 0), 4), circle_attributes)
    view.ActiveViewport.ZoomBoundingBox(Rhino.Geometry.BoundingBox(Point3d(-500, -500, 0), Point3d(-400, -400, 0)))
    lt.check('rhinoview:Overview: restored', follow('rhinoview:Overview'), None)
    yield 1
    lt.check('the view back on the text', view.ActiveViewport.GetFrustumBoundingBox().Contains(middle), True)
    lt.check('rhino:MarkedCircle: zoomed to, marked', (follow('rhino:MarkedCircle'), links.GetProperty('Marking', STATIC).GetValue(None)), (None, True))
    # Zoomed so it takes about 10 % of the view (setting "Links to Rhino objects: their size in the view")
    circle = next(o for o in doc.Objects if o.Attributes.Name == 'MarkedCircle')
    corners = [view.ActiveViewport.WorldToClient(c) for c in circle.Geometry.GetBoundingBox(True).GetCorners()]
    size = view.ActiveViewport.Size
    share = max(max(c.X for c in corners) - min(c.X for c in corners), max(c.Y for c in corners) - min(c.Y for c in corners)) / min(size.Width, size.Height)
    lt.note('the circle takes %.0f %% of the view' % (100 * share))
    lt.check('the circle: about a tenth of the view', 0.05 < share < 0.2, True)
    doc.Views.Redraw()
    try:
        view.CaptureToBitmap().Save(os.path.join(folder, 'markdown_link_mark.png'))
        lt.note('image: ' + os.path.join(folder, 'markdown_link_mark.png'))
    except Exception as e:
        lt.note('no image: %s' % e)
    yield 3
    lt.check('the mark gone after a moment', links.GetProperty('Marking', STATIC).GetValue(None), False)
    lt.check('rhino:Nothing: said', 'No Rhino object' in (follow('rhino:Nothing') or ''), True)
    lt.check('an unknown kind of link: said', 'Links go to' in (follow('nonsense:x') or ''), True)

    # A copy has the same id: its parts are its own
    copies = [doc.Objects.Transform(i, Transform.Translation(0, -80, 0), False) for i in new_ids]
    copy_text = call('Find', doc, doc.Objects.FindId(copies[0]))
    lt.check('a copy: found as its own parts', sorted(str(o.Id) for o in copy_text.Parts), sorted(str(i) for i in copies))


lt.run('t_markdownparts', test)
