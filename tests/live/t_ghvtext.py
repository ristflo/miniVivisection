#! python 3
# V Text (miniVivisection.GH's VTextComponent): Panels' text as Rhino text, as they show it --
# Markdown laid out, a Python script as code (line numbers, a text per syntax color, lined up:
# its spaces and empty lines kept), plain text; Height in points; the anchor's four corners and
# the angle (Bounds, Anchor); the Panel's width and Show as followed; baked: grouped, the
# colors, Markdown that MarkdownText finds. Images of the preview and the baked text for a look.
import math
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

ANY = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
STATIC = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static

MARKDOWN = """# V Text

A Panel's **Markdown** as Rhino text: headings, *italic*, `code`, and a long paragraph that wraps in about the Panel's width, as the Panel shows it.

- one
- [x] done
"""

# The string only in line 4, after four spaces and print(: its text lines up only if its
# spaces and the empty lines above it are kept
PYTHON = '''#! python 3
x = 1
if x:
    print("yes")
'''

PLAIN = 'Just some words\nin two lines'


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('miniVivisection.GH') is not None)


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document
    from Grasshopper.Kernel.Types import GH_Point, GH_Number
    from Grasshopper.Kernel.Data import GH_Path
    import make_gh_examples as mk
    gha = lt.assembly('miniVivisection.GH')
    vtext_type = gha.GetType('MiniVivisection.GH.VTextComponent')
    corner_type = vtext_type.GetNestedType('Corner', System.Reflection.BindingFlags.NonPublic)
    rhino = Rhino.RhinoDoc.ActiveDoc
    settings = {name: lt.static_get('MiniVivisectionSettings', name) for name in ('GrasshopperScriptPanels', 'GrasshopperMarkdown')}
    for name in settings:
        lt.static_call('MiniVivisectionSettings', 'Set', name, True)
    folder = os.path.join(tempfile.gettempdir(), 'minivivisection-live')
    os.makedirs(folder, exist_ok=True)
    try:
        doc = GH_Document()
        md = mk.panel(MARKDOWN, 20, 20, width=300, height=200)
        md.NickName = 'Notes'
        py = mk.panel(PYTHON, 20, 260, width=300, height=110)
        py.NickName = 'Script'
        plain = mk.panel(PLAIN, 20, 400, width=200, height=60)
        plain.NickName = 'Words'

        def vtext(panel, x, y, anchor):
            v = mk._place(System.Activator.CreateInstance(vtext_type), x, y)
            v.Params.Input[0].AddSource(panel)
            v.Params.Input[2].PersistentData.Clear()  # its default, the origin
            v.Params.Input[2].PersistentData.Append(GH_Point(Rhino.Geometry.Point3d(*anchor)), GH_Path(0))
            return v

        v_md = vtext(md, 400, 20, (0, 0, 0))
        v_py = vtext(py, 400, 260, (0, -150, 0))
        v_plain = vtext(plain, 400, 400, (0, -250, 0))
        for obj in (md, py, plain, v_md, v_py, v_plain):
            doc.AddObject(obj, False)
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        canvas = Grasshopper.Instances.ActiveCanvas
        canvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        yield 2

        def out(v, name):  # by name: Text and Decorators come before Bounds and Anchor
            param = next(o for o in v.Params.Output if o.Name == name)
            return [g.Value for g in param.VolatileData.AllData(True)]

        def pieces(v):
            results = list(lt.member(v, '_results'))
            return [(lt.member(p, 'Geometry'), lt.member(p, 'Color')) for r in results for p in list(lt.member(r, 'Pieces'))]

        def texts(v):
            return [(g, c) for g, c in pieces(v) if isinstance(g, Rhino.Geometry.TextEntity)]

        lt.check('what each shows: Markdown, Python code, plain text', [v.Message for v in (v_md, v_py, v_plain)],
                 ['Markdown, top left', 'Python, top left', 'Text, top left'])
        lt.check('no errors', [str(v.RuntimeMessageLevel) for v in (v_md, v_py, v_plain)], ['Blank'] * 3)

        # Height: 10 pt in the model's units; a heading 1.8 times as high
        pt = Rhino.RhinoMath.UnitScale(Rhino.UnitSystem.Inches, rhino.ModelUnitSystem) / 72.0
        heights = sorted(set(round(t.TextHeight / pt, 3) for t, c in texts(v_md)))
        lt.note('text heights in pt: %s (%s)' % (heights, rhino.ModelUnitSystem))
        lt.check('10 pt paragraphs, an 18 pt heading', (10.0 in heights, 18.0 in heights), (True, True))
        plain_texts = [t.PlainText for t, c in texts(v_md)]
        boxes = [g for g, c in pieces(v_md) if isinstance(g, Rhino.Geometry.PolylineCurve) and g.IsClosed and g.PointCount == 5]
        lt.check('the Markdown laid out: heading, the task box drawn (a square, no glyph)', (any('V Text' in p for p in plain_texts), len(boxes) >= 1, any('\u2611' in p for p in plain_texts)), (True, True, False))
        # The Panel's task box clicked (ScriptPanels.ToggleTask): unchecked in its text, then checked again
        panels = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.ScriptPanels')
        toggle = lambda: panels.GetMethod('ToggleTask', STATIC).Invoke(None, [md, 5, 3])
        lt.check('a Panel\'s task box clicked: unchecked in its text', (toggle(), '- [ ] done' in md.UserText.replace('\r\n', '\n')), (None, True))
        lt.check('  and clicked again: checked, the text as it was', (toggle(), md.UserText.replace('\r\n', '\n') == MARKDOWN), (None, True))
        yield 2

        # Code: the line numbers, a text per color, lined up
        code = texts(v_py)
        light = lt.member(gha.GetType('MiniVivisection.GH.ScriptPanels').GetField('Light', STATIC).GetValue(None), 'String')
        numbers = [t for t, c in code if t.PlainText.replace('\r', '').split('\n')[:4] == ['1', '2', '3', '4']]
        lt.check('the line numbers, right-aligned', len(numbers) == 1 and str(numbers[0].TextHorizontalAlignment) == 'Right', True)
        strings = [t for t, c in code if c.ToArgb() == light.ToArgb()]
        comments = [t for t, c in code if '#! python 3' in t.PlainText]
        lt.check('a text for the strings, one for the comment', (len(strings), len(comments)), (1, 1))
        lt.check('colored texts: more than two', len(set(c.ToArgb() for t, c in code if not c.IsEmpty)) > 2, True)
        # Lined up: each color's text from the same top left, its spaces and empty lines kept
        # (an image shows it: a box starts at its first line with text in it)
        lines = strings[0].PlainText.replace('\r', '').split('\n')
        lt.check('the string\'s text: in line 4, after its spaces', lines[:4], ['', '', '', '          "yes"'])
        lt.check('the string\'s and the comment\'s texts: the same top left', strings[0].Plane.Origin.DistanceTo(comments[0].Plane.Origin) < 1e-9, True)

        # The anchor's corners: that corner of Bounds at the anchor, the size the same
        size = None
        for name, (u, w) in (('TopRight', (1, 1)), ('BottomLeft', (0, 0)), ('BottomRight', (1, 0)), ('TopLeft', (0, 1))):
            vtext_type.GetField('_corner', ANY).SetValue(v_md, System.Enum.Parse(corner_type, name))
            v_md.ExpireSolution(True)
            yield 1
            rect = out(v_md, 'Bounds')[0]
            point = rect.Plane.PointAt(rect.X.Max if u else rect.X.Min, rect.Y.Max if w else rect.Y.Min)
            lt.check('anchor %s: that corner at the anchor' % name, point.DistanceTo(Rhino.Geometry.Point3d.Origin) < 1e-6, True)
            if size is None:
                size = (rect.Width, rect.Height)
            lt.check('anchor %s: the same size' % name, abs(rect.Width - size[0]) < 1e-6 and abs(rect.Height - size[1]) < 1e-6, True)
        lt.check('the Anchor output', out(v_md, 'Anchor')[0].DistanceTo(Rhino.Geometry.Point3d.Origin) < 1e-9, True)

        # The angle: turned about the anchor
        angle = v_md.Params.Input[3]
        angle.PersistentData.Clear()
        angle.PersistentData.Append(GH_Number(math.pi / 2), GH_Path(0))
        angle.ExpireSolution(True)
        yield 1
        rect = out(v_md, 'Bounds')[0]
        lt.note('turned: its X axis %s (Degrees: %s)' % (rect.Plane.XAxis, angle.UseDegrees))
        lt.check('turned 90 degrees: along Y, its top left at the anchor',
                 (rect.Plane.XAxis.EpsilonEquals(Rhino.Geometry.Vector3d.YAxis, 1e-9), rect.Plane.PointAt(rect.X.Min, rect.Y.Max).DistanceTo(Rhino.Geometry.Point3d.Origin) < 1e-6), (True, True))
        angle.PersistentData.Clear()
        angle.PersistentData.Append(GH_Number(0), GH_Path(0))
        angle.ExpireSolution(True)
        yield 1

        # The Panel followed: wider, the text wraps wider; Show as Plain text, plain text
        narrow = out(v_md, 'Bounds')[0].Width
        md.Attributes.Bounds = System.Drawing.RectangleF(20, 20, 520, 200)
        canvas.Refresh()
        yield 2
        lt.check('the Panel made wider: the text too', out(v_md, 'Bounds')[0].Width > narrow * 1.3, True)
        mk._show_as(md, 'Text')
        canvas.Refresh()
        yield 2
        lt.check('the Panel shown as plain text: so is the text', v_md.Message, 'Text, top left')
        mk._show_as(md, 'Auto')
        canvas.Refresh()
        yield 2
        lt.check('back to Markdown', v_md.Message, 'Markdown, top left')

        # A look: the preview in the Top view
        box = Rhino.Geometry.BoundingBox.Empty
        for v in (v_md, v_py, v_plain):
            box.Union(v.ClippingBox)
        box.Inflate(box.Diagonal.Length * 0.05)
        view = rhino.Views.Find('Top', False) or rhino.Views.ActiveView
        rhino.Views.ActiveView = view
        view.Maximized = True
        view.ActiveViewport.ZoomBoundingBox(box)
        rhino.Views.Redraw()
        yield 1

        def picture(name):
            try:
                bitmap = view.CaptureToBitmap()
                if bitmap is not None:
                    bitmap.Save(os.path.join(folder, name))
                    lt.note('image: ' + os.path.join(folder, name))
            except Exception as e:
                lt.note('no image: %s' % e)

        picture('vtext_preview.png')
        # And the Panels, to compare: the wrapped lines about as long
        tile = Grasshopper.GUI.Canvas.GH_Viewport()
        tile.Width, tile.Height = 1300, 1000
        tile.Zoom = 2.0
        tile.MidPoint = System.Drawing.PointF(320, 240)
        canvas.GenerateHiResImageTile(tile, System.Drawing.Color.White).Save(os.path.join(folder, 'vtext_canvas.png'))
        lt.note('image: ' + os.path.join(folder, 'vtext_canvas.png'))

        # Baked: grouped; a color per token; the Markdown kept, MarkdownText finds it
        ids = System.Collections.Generic.List[System.Guid]()
        v_md.BakeGeometry(rhino, None, ids)
        baked = [rhino.Objects.FindId(i) for i in ids]
        lt.check('Markdown baked: every piece', len(baked), len(pieces(v_md)))
        groups = set(tuple(o.Attributes.GetGroupList() or []) for o in baked)
        lt.check('Markdown baked: one group', len(groups) == 1 and len(next(iter(groups))) == 1, True)
        lt.check('Markdown baked: the Markdown kept', all(o.Attributes.GetUserString('minivivisection.markdown') == MARKDOWN for o in baked), True)
        found = lt.plugin_type('MarkdownRhinoText').GetMethod('Find', STATIC).Invoke(None, [rhino, baked[0]])
        lt.check('MarkdownText finds it: all of it', found is not None and len(list(found.Parts)) == len(baked), True)
        code_ids = System.Collections.Generic.List[System.Guid]()
        v_py.BakeGeometry(rhino, None, code_ids)
        code_objects = [rhino.Objects.FindId(i) for i in code_ids]
        lt.check('code baked: the token colors the objects\'',
                 sum(1 for o in code_objects if str(o.Attributes.ColorSource) == 'ColorFromObject') > 2, True)
        lt.check('code baked: not Markdown', any(o.Attributes.GetUserString('minivivisection.markdown') for o in code_objects), False)
        plain_ids = System.Collections.Generic.List[System.Guid]()
        v_plain.BakeGeometry(rhino, None, plain_ids)
        lt.check('plain text baked: one text', [rhino.Objects.FindId(i).Geometry.PlainText.replace('\r', '') for i in plain_ids], [PLAIN])
        doc.PreviewMode = Grasshopper.Kernel.GH_PreviewMode.Disabled
        rhino.Views.Redraw()
        yield 1
        picture('vtext_baked.png')
    finally:
        for name, value in settings.items():
            lt.static_call('MiniVivisectionSettings', 'Set', name, value)


lt.run('t_ghvtext', test)
