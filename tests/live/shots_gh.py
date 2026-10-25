#! python 3
# Screenshots, Grasshopper's side (tools/shots.py captures, tools/compose_screenshots.py
# composes): GH 1's failing branches and its menu; GH 5 paused at its error (the run's badge);
# GH 9's script Panel as code, edited in place at a stop; GH 11's Markdown Panels -- a link's
# tooltip, a followed link's red circle; V Edit's object in Rhino (GH 4); V Text from Panels.
# Raw captures in %TEMP%\minivivisection-live\shots.
import ctypes
import ctypes.wintypes as wt
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

HERE = lt.ROOT
LESSONS = os.path.join(HERE, 'examples', 'grasshopper')
STATIC = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
user32 = ctypes.windll.user32

VTEXT_NOTES = """# Ring

The **area** between two circles:

- `outer` -- the outer radius
- `inner` -- the inner one

```
area = pi * (outer**2 - inner**2)
```
"""

VTEXT_SCRIPT = '''#! python 3
import math
area = math.pi * (outer ** 2 - inner ** 2)
'''


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('miniVivisection.GH') is not None and lt.assembly('RhinoCodePluginGH') is not None)


EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def popups(class_prefix):
    """This process's visible top-level windows of a class: handles."""
    pid = System.Diagnostics.Process.GetCurrentProcess().Id
    found = []

    def each(hwnd, _):
        if user32.IsWindowVisible(hwnd):
            owner = wt.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            name = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(hwnd, name, 256)
            if owner.value == pid and class_prefix in name.value:
                found.append(hwnd)
        return True
    user32.EnumWindows(EnumProc(each), 0)
    return found


def test():
    yield 2
    import shots
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_DocumentIO
    editor = Grasshopper.Instances.DocumentEditor
    canvas = Grasshopper.Instances.ActiveCanvas
    editor.Location = System.Drawing.Point(30, 30)
    editor.Size = System.Drawing.Size(1200, 750)  # 960 x 600 at 100 %
    doc_rhino = Rhino.RhinoDoc.ActiveDoc
    saved = []
    places = []

    def keep(bitmap, name):
        saved.append(shots.save(bitmap, name))
        lt.note('shot: ' + saved[-1])

    def where(name, rect):
        places.append('%s %d %d %d %d' % ((name,) + tuple(rect)))

    def gh_shot(name, objects=()):
        canvas.Refresh()
        keep(shots.window(editor.Handle.ToInt64()), name)
        wx, wy, ww, wh = shots.rect_of(editor.Handle.ToInt64())
        where(name + '_window', (wx, wy, ww, wh))
        origin = canvas.PointToScreen(System.Drawing.Point(0, 0))
        where(name + '_canvas', (origin.X - wx, origin.Y - wy, canvas.Width, canvas.Height))
        # each object's box in the picture (Grasshopper's canvas units projected to the control)
        for o in objects:
            r = canvas.Viewport.ProjectRectangle(o.Attributes.Bounds)
            where(name + ':' + (o.NickName or o.Name).replace(' ', '_'), (origin.X - wx + r.X, origin.Y - wy + r.Y, r.Width, r.Height))

    def open_doc(path):
        io = GH_DocumentIO()
        io.Open(path)
        doc = io.Document
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        canvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        return doc

    def find(doc, nickname):
        return [o for o in doc.Objects if o.NickName == nickname][0]

    def frame(doc, objects, zoom=1.0, margin=30):
        # the objects in the canvas, as large as fits up to zoom
        box = None
        for o in objects:
            b = o.Attributes.Bounds
            box = b if box is None else System.Drawing.RectangleF.Union(box, b)
        fit = min((canvas.Width - 2 * margin) / max(1.0, box.Width), (canvas.Height - 2 * margin) / max(1.0, box.Height))
        canvas.Viewport.Zoom = min(zoom, fit)
        canvas.Viewport.MidPoint = System.Drawing.PointF(box.X + box.Width / 2, box.Y + box.Height / 2)
        canvas.Refresh()

    # --- GH 1: which branch failed, and the component's menu ---------------------------------------
    doc = open_doc(os.path.join(LESSONS, 'gh1_which_branch_failed.gh'))
    yield 3
    divide = find(doc, 'Divide')
    frame(doc, [o for o in doc.Objects if o.NickName != 'Lesson'], 1.0, 140)
    divide.Attributes.Selected = True
    yield 1
    menu = System.Windows.Forms.ContextMenuStrip()
    divide.AppendMenuItems(menu)
    pivot = canvas.Viewport.ProjectPoint(divide.Attributes.Pivot)
    menu.Show(canvas, System.Drawing.Point(int(pivot.X) + 30, int(pivot.Y) + 10))
    yield 1
    gh_shot('failed_gh', list(doc.Objects))
    keep(shots.window(menu.Handle.ToInt64()), 'failed_menu')
    where('failed_menu', shots.rect_of(menu.Handle.ToInt64()))
    menu.Close()
    divide.Attributes.Selected = False
    doc.Enabled = False
    yield 1

    # --- GH 5: paused at its error, the run's badge --------------------------------------------
    doc = open_doc(os.path.join(LESSONS, 'gh5_runtime_error.gh'))
    yield 3
    spacing = find(doc, 'Spacing')
    frame(doc, [o for o in doc.Objects if o.NickName != 'Lesson'], 1.0)
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    try:
        runs = lt.plugin_type('GrasshopperRuns')
        runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [spacing])
        yield lt.wait_for(lambda: runs.GetMethod('HasVisibleEditor', lt.ANY).Invoke(None, [spacing]), 60)
        yield 3
        spacing.ExpireSolution(False)
        doc.ScheduleSolution(50)
        hook = lt.hook()
        paused = lambda: len(list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))) > 0
        yield lt.wait_for(paused, 30)
        yield 3
        windows = lt.editors()
        lt.note('%d editors' % len(windows))
        for i, w in enumerate(windows):
            if w.Visible:
                import Eto
                w.Location = Eto.Drawing.Point(560, 140)
                w.Size = Eto.Drawing.Size(620, 430)
                lt.member(w, 'Browser').Visible = False
                yield 2
                keep(shots.editor(w, lt.window_handle(w)), 'paused_editor')
                break
        gh_shot('paused_gh', list(doc.Objects))
        controls = list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
        if controls:
            controls[0].Stop()
            yield 3
        for w in lt.editors():
            w.Close()
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)
    doc.Enabled = False
    yield 1

    # --- GH 9: the script Panel as code; edited in place at a stop -----------------------------
    doc = open_doc(os.path.join(LESSONS, 'gh9_script_from_a_panel.gh'))
    yield 3
    script_panel = find(doc, 'Twist script')
    frame(doc, [script_panel] + [o for o in doc.Objects if o.NickName == 'Twist'], 1.0)
    yield 1
    gh_shot('panel_code_gh', list(doc.Objects))
    panels = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.ScriptPanels')
    # The plug-in's own words for a Panel's script stopped by Ctrl+Alt+B (StoppedRuns.PanelOverlay),
    # for a record as that stop makes: Twist, its first run, line 8 of the loop, its inputs' values
    record_type = lt.plugin_type('StoppedRuns').GetNestedType('Record', System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
    record = System.Activator.CreateInstance(record_type)
    for name, value in (('Kind', 'stopped'), ('Time', System.DateTime.Now), ('Line', 8), ('Function', '<module>'),
                        ('Component', 'Twist'), ('RunNumber', 1), ('InputScript', True)):
        record_type.GetProperty(name).SetValue(record, value)
    values = record_type.GetProperty('Values').GetValue(record)
    for pair in (('n', '12'), ('angle', '5.0'), ('i', '7')):
        values.Add(System.Array[System.String](list(pair)))
    overlay = lt.plugin_type('StoppedRuns').GetMethod('PanelOverlay', STATIC).Invoke(None, [record])
    title, lines = overlay.Item1, overlay.Item2
    lt.note('overlay: %s | %s' % (title, ' / '.join(lines)))
    panels.GetMethod('EditAtLine', STATIC).Invoke(None, [script_panel, 8, title, lines])
    yield 2
    gh_shot('panel_stop_gh', list(doc.Objects))
    for c in list(canvas.Controls):
        if c.GetType().Name == 'CodeBox':
            close = [m for m in c.GetType().GetMethods(lt.ANY) if m.Name == 'Close' and len(m.GetParameters()) == 1]
            if close:
                close[0].Invoke(c, [False])
    yield 1
    doc.Enabled = False

    # --- GH 11: Markdown Panels, a link's tooltip, a followed link's mark ------------------------
    doc = open_doc(os.path.join(LESSONS, 'gh11_markdown_notes.gh'))
    yield 3
    lesson = find(doc, 'Lesson')
    canvas.Viewport.Zoom = 1.0
    canvas.Viewport.MidPoint = System.Drawing.PointF(560, 560)
    canvas.Refresh()
    yield 1
    view = panels.GetMethod('MarkdownViewOf', STATIC).Invoke(None, [lesson])
    place = view.GetType().GetMethod('LinkPlace', lt.ANY).Invoke(view, ['gh:Ring'])
    point = System.Drawing.PointF(place.X + place.Width / 2, place.Y + place.Height / 2)
    control = canvas.Viewport.ProjectPoint(point)
    hover = Grasshopper.GUI.GH_CanvasMouseEvent(System.Drawing.Point(int(control.X), int(control.Y)), point,
                                                System.Enum.Parse(System.Type.GetType('System.Windows.Forms.MouseButtons, System.Windows.Forms'), 'None'), 0, 0)
    editor.Activate()
    canvas.Focus()
    lesson.Attributes.GetType().GetMethod('RespondToMouseMove', lt.ANY).Invoke(lesson.Attributes, [canvas, hover])
    yield 1
    gh_shot('markdown_gh', list(doc.Objects))
    tips = popups('tooltips_class')
    lt.note('%d tooltips' % len(tips))
    for tip in tips:
        keep(shots.window(tip), 'markdown_tip')
        where('markdown_tip', shots.rect_of(tip))
        break
    with open(os.path.join(shots.RAW, 'markdown_link.txt'), 'w') as f:
        f.write('%d %d' % (int(control.X), int(control.Y)))
    # followed: the canvas at Ring, the red circle
    panels.GetMethod('Follow', STATIC).Invoke(None, ['gh:Ring', LESSONS])
    yield 0.8
    gh_shot('link_mark_gh', list(doc.Objects))
    doc.Enabled = False
    yield 1

    # --- GH 4: V Edit's object in Rhino ---------------------------------------------------------
    Rhino.RhinoApp.RunScript('_-Grasshopper _Document _Open "%s" _Enter' % os.path.join(LESSONS, 'gh4_v_edit.gh'), False)
    yield lt.wait_for(lambda: canvas.Document is not None and 'gh4' in (canvas.Document.FilePath or ''), 30)
    yield 3
    doc = canvas.Document
    editor.Size = System.Drawing.Size(700, 520)
    yield 1
    frame(doc, [o for o in doc.Objects if o.NickName != 'Lesson'], 1.0, 20)
    gh_shot('vedit_gh', list(doc.Objects))
    rhino_view = doc_rhino.Views.Find('Perspective', False) or doc_rhino.Views.ActiveView
    doc_rhino.Views.ActiveView = rhino_view
    rhino_view.Maximized = True
    vp = rhino_view.ActiveViewport
    vp.ZoomExtents()
    vp.Magnify(0.8, False)
    # the object in the view's right part (the Grasshopper window goes over the left)
    frame_ok, camera = vp.GetCameraFrame()
    target = vp.CameraTarget
    scale_ok, per_unit = vp.GetWorldToScreenScale(target)
    shift = camera.XAxis * (0.22 * vp.Size.Width / max(per_unit, 1e-9))
    vp.SetCameraLocations(target - shift, vp.CameraLocation - shift)
    doc_rhino.Views.Redraw()
    yield 2
    keep(shots.viewport(rhino_view, 1200, 750), 'vedit_viewport')
    doc.Enabled = False
    yield 1

    # --- V Text: Panels as Rhino text --------------------------------------------------------------
    sys.path.insert(0, os.path.join(HERE, 'tools'))  # a script run may have reset the path
    import make_gh_examples as mk
    from Grasshopper.Kernel import GH_Document
    doc = GH_Document()
    notes = mk.panel(VTEXT_NOTES, 20, 20, width=330, height=230)
    notes.NickName = 'Notes'
    script = mk.panel(VTEXT_SCRIPT, 20, 280, width=330, height=90)
    script.NickName = 'Ring script'
    vtext_type = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.VTextComponent')
    from Grasshopper.Kernel.Types import GH_Point
    from Grasshopper.Kernel.Data import GH_Path
    made = []
    for source, x, y, anchor in ((notes, 420, 60, (0, 0, 0)), (script, 420, 290, (0, -110, 0))):
        v = mk._place(System.Activator.CreateInstance(vtext_type), x, y)
        v.Params.Input[0].AddSource(source)
        v.Params.Input[2].PersistentData.Clear()
        v.Params.Input[2].PersistentData.Append(GH_Point(Rhino.Geometry.Point3d(*anchor)), GH_Path(0))
        made.append(v)
    for obj in [notes, script] + made:
        doc.AddObject(obj, False)
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    canvas.Document = doc
    doc.Enabled = True
    doc.NewSolution(False)
    yield 2
    frame(doc, [notes, script] + made, 1.0, 20)
    gh_shot('vtext_gh', list(doc.Objects))
    box = Rhino.Geometry.BoundingBox.Empty
    for v in made:
        box.Union(v.ClippingBox)
    # the text in the view's left part (the Grasshopper window goes over the right), 1.6 wide
    w, h = box.Max.X - box.Min.X, box.Max.Y - box.Min.Y
    height = max(h * 1.12, w * 2.3 / 1.6)
    box = Rhino.Geometry.BoundingBox(Rhino.Geometry.Point3d(box.Min.X - w * 0.12, box.Center.Y - height / 2, 0),
                                     Rhino.Geometry.Point3d(box.Min.X - w * 0.12 + height * 1.6, box.Center.Y + height / 2, 0))
    top = doc_rhino.Views.Find('Top', False)
    doc_rhino.Views.ActiveView = top
    top.Maximized = True
    top.ActiveViewport.ZoomBoundingBox(box)
    doc_rhino.Views.Redraw()
    yield 2
    keep(shots.viewport(top, 1200, 750), 'vtext_viewport')

    with open(os.path.join(shots.RAW, 'places_gh.txt'), 'w') as f:
        f.write('\n'.join(places))
    lt.check('shots taken', len(saved) >= 10, True)


lt.run('shots_gh', test)
