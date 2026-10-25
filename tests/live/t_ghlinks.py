#! python 3
# Links in Grasshopper Panels and Panels with sources: a Panel fed by another shows its data
# as Markdown, its menu has Show as (and no editing); gh:Name centers the canvas on the object
# and marks it, ghview:Name restores a named view; a click on a link in a Panel follows it
# (after the double-click time). V Text: its Font input; its Markdown baked keeps the parts'
# data -- found and updated in place like MarkdownText's.
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

ANY = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
STATIC = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static

NOTES = """# Links

Go to [the twist](gh:Twist), or to [the overview](ghview:Overview).

- one
- two
"""


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
    from Grasshopper.Kernel.Types import GH_Point, GH_String
    from Grasshopper.Kernel.Data import GH_Path
    import make_gh_examples as mk
    gha = lt.assembly('miniVivisection.GH')
    panels = gha.GetType('MiniVivisection.GH.ScriptPanels')
    gh_links = gha.GetType('MiniVivisection.GH.GhLinks')
    settings = {name: lt.static_get('MiniVivisectionSettings', name) for name in ('GrasshopperScriptPanels', 'GrasshopperMarkdown')}
    for name in settings:
        lt.static_call('MiniVivisectionSettings', 'Set', name, True)
    try:
        doc = GH_Document()
        notes = mk.panel(NOTES, 20, 20, width=320, height=160)
        notes.NickName = 'Notes'
        fed = mk.panel('', 380, 20, width=320, height=160)
        fed.NickName = 'Fed'
        fed.AddSource(notes)
        twist = mk.panel('the target', 2000, 1500, width=120, height=40)
        twist.NickName = 'Twist'
        for obj in (notes, fed, twist):
            doc.AddObject(obj, False)
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        canvas = Grasshopper.Instances.ActiveCanvas
        canvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        canvas.Viewport.Zoom = 1.0
        canvas.Viewport.MidPoint = System.Drawing.PointF(360, 100)
        canvas.Refresh()
        yield 1

        # A Panel fed by another: its data shown as Markdown; Show as in its menu, no editing
        view_of = lambda panel: panels.GetMethod('MarkdownViewOf', STATIC).Invoke(None, [panel])
        lt.check('a Panel with a source: its data shown as Markdown', view_of(fed) is not None, True)
        menu = System.Windows.Forms.ContextMenuStrip()
        fed.AppendMenuItems(menu)
        items = [i.Text for i in menu.Items if i.Text]
        lt.check('its menu: Show as, no editing', (any('Show as' in t for t in items), any('Edit the Markdown' in t for t in items)), (True, False))
        mk._show_as(fed, 'Python')
        lt.check('Show as Python: shown as code', panels.GetMethod('ScriptOf', STATIC).Invoke(None, [fed]) is not None, True)
        mk._show_as(fed, 'Auto')
        canvas.Refresh()
        yield 1
        folder = os.path.join(tempfile.gettempdir(), 'minivivisection-live')
        os.makedirs(folder, exist_ok=True)
        tile = Grasshopper.GUI.Canvas.GH_Viewport()
        tile.Width, tile.Height = 1500, 500
        tile.Zoom = 2.0
        tile.MidPoint = System.Drawing.PointF(360, 100)
        canvas.GenerateHiResImageTile(tile, System.Drawing.Color.White).Save(os.path.join(folder, 'gh_links.png'))
        lt.note('image: ' + os.path.join(folder, 'gh_links.png'))

        # gh: and ghview: followed
        follow = lambda scheme, target: gh_links.GetMethod('Follow', STATIC).Invoke(None, [scheme, target, None])
        lt.check('gh:Twist followed', follow('gh', 'Twist'), None)
        yield 1
        middle = canvas.Viewport.MidPoint
        lt.check('the canvas centered on it', abs(middle.X - 2060) < 5 and abs(middle.Y - 1520) < 5, True)
        marked = list(gh_links.GetProperty('Marked', STATIC).GetValue(None))
        lt.check('it marked', any(m.InstanceGuid == twist.InstanceGuid for m in marked), True)
        doc.Properties.ViewList.Add(Grasshopper.GUI.Canvas.GH_NamedView('Overview', System.Drawing.PointF(360, 100), 1.0, Grasshopper.GUI.Canvas.GH_NamedViewType.center))
        lt.check('ghview:Overview followed', follow('ghview', 'Overview'), None)
        yield 1
        middle = canvas.Viewport.MidPoint
        lt.check('the named view restored', abs(middle.X - 360) < 5 and abs(middle.Y - 100) < 5, True)
        lt.check('gh:Nothing: said', 'No Grasshopper object' in (follow('gh', 'Nothing') or ''), True)

        # A click on the link in the Panel: followed after the double-click time
        canvas.Refresh()
        yield 1
        markdown_view = view_of(notes)
        place = markdown_view.GetType().GetMethod('LinkPlace', ANY).Invoke(markdown_view, ['gh:Twist'])
        lt.check('the link drawn', place.IsEmpty, False)
        point = System.Drawing.PointF(place.X + place.Width / 2, place.Y + place.Height / 2)
        control = canvas.Viewport.ProjectPoint(point)
        event = Grasshopper.GUI.GH_CanvasMouseEvent(System.Drawing.Point(int(control.X), int(control.Y)), point, System.Windows.Forms.MouseButtons.Left, 1, 0)
        attributes = notes.Attributes
        hover = Grasshopper.GUI.GH_CanvasMouseEvent(System.Drawing.Point(int(control.X), int(control.Y)), point, System.Enum.Parse(System.Type.GetType('System.Windows.Forms.MouseButtons, System.Windows.Forms'), 'None'), 0, 0)
        attributes.GetType().GetMethod('RespondToMouseMove', ANY).Invoke(attributes, [canvas, hover])
        lt.check('the mouse over the link: its hint', panels.GetProperty('LinkTip', STATIC).GetValue(None), 'gh:Twist')
        respond = lambda name: str(attributes.GetType().GetMethod(name, ANY).Invoke(attributes, [canvas, event]))
        # "Markdown links: follow with" Ctrl+click: a plain click isn't the link's
        lt.static_call('MiniVivisectionSettings', 'Set', 'MarkdownLinkFollow', 'Ctrl+click')
        canvas.Viewport.MidPoint = System.Drawing.PointF(360, 100)
        respond('RespondToMouseDown')
        respond('RespondToMouseUp')
        yield 2
        middle = canvas.Viewport.MidPoint
        lt.check('Ctrl+click: a plain click doesn\'t follow', abs(middle.X - 360) < 5 and abs(middle.Y - 100) < 5, True)
        # Click: followed after the double-click time
        lt.static_call('MiniVivisectionSettings', 'Set', 'MarkdownLinkFollow', 'Click')
        try:
            down = respond('RespondToMouseDown')
            up = respond('RespondToMouseUp')
            lt.check('click: the click taken by the link', (down, up), ('Capture', 'Release'))
            yield 2
            middle = canvas.Viewport.MidPoint
            lt.check('followed: the canvas at the twist', abs(middle.X - 2060) < 5 and abs(middle.Y - 1520) < 5, True)
        finally:
            lt.static_call('MiniVivisectionSettings', 'Set', 'MarkdownLinkFollow', 'Click')  # the default

        # V Text: the Font input; its Markdown baked: parts found and updated in place
        vtext = mk._place(System.Activator.CreateInstance(gha.GetType('MiniVivisection.GH.VTextComponent')), 400, 300)
        doc.AddObject(vtext, False)
        vtext.Params.Input[0].AddSource(notes)
        lt.check('the Font input: a font picker', vtext.Params.Input[5].GetType().Name, 'FontNameParameter')
        installed = [f.Name for f in System.Drawing.Text.InstalledFontCollection().Families]
        face = next((f for f in ('Times New Roman', 'Georgia', 'Segoe UI', 'Calibri') if f in installed), installed[0])
        vtext.Params.Input[5].PersistentData.Append(GH_String(face), GH_Path(0))
        vtext.ExpireSolution(True)
        yield 1
        results = list(lt.member(vtext, '_results'))
        texts = [lt.member(p, 'Geometry') for r in results for p in list(lt.member(r, 'Pieces')) if isinstance(lt.member(p, 'Geometry'), Rhino.Geometry.TextEntity)]
        lt.check('the text in the font (%s)' % face, any(face in t.RichText for t in texts), True)
        rhino = Rhino.RhinoDoc.ActiveDoc
        ids = System.Collections.Generic.List[System.Guid]()
        vtext.BakeGeometry(rhino, None, ids)
        baked = [rhino.Objects.FindId(i) for i in ids]
        lt.check('baked: the parts\' data', all(o.Attributes.GetUserString('minivivisection.markdown.id') for o in baked), True)
        builder = lt.plugin_type('MarkdownRhinoText')
        found = builder.GetMethod('Find', STATIC).Invoke(None, [rhino, baked[0]])
        lt.check('baked: MarkdownText finds it whole', len(list(found.Parts)), len(baked))
        lt.check('baked: the font kept', found.Frame.Font, face)
        updated = list(builder.GetMethod('Update', STATIC).Invoke(None, [rhino, found, NOTES.replace('# Links', '# Links, edited'), found.Frame]))
        lt.check('baked, edited: the same objects', sorted(str(i) for i in updated), sorted(str(i) for i in ids))
        links = [o.Attributes.GetUserString('minivivisection.markdown.links') for o in baked if o.Attributes.GetUserString('minivivisection.markdown.links')]
        lt.check('baked: its links kept', any('gh:Twist' in l for l in links), True)
    finally:
        for name, value in settings.items():
            lt.static_call('MiniVivisectionSettings', 'Set', name, value)


lt.run('t_ghlinks', test)
