#! python 3
# Screenshots, Rhino's side (tools/shots.py captures, tools/compose_screenshots.py composes).
# Window sizes in Eto units are the pictures' sizes at 100 % (the hidden desktop runs at
# 125 %; the composer scales by 0.8). Scenes: lesson 01 paused at its breakpoint -- the drawn
# outline and corners, V Watch's rows (a small editor over the view), the values in the code
# (the editor alone) --; V Python fixing it in the paused frame; "Stop and record" after
# Ctrl+Alt+B; Markdown text in Rhino -- the hint over its link into Grasshopper, the red mark
# of a followed link, MarkdownText's dialog for one part. Raw captures in
# %TEMP%\minivivisection-live\shots.
import ctypes
import os
import sys
import threading
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino
import Eto

HERE = lt.ROOT
LESSON01 = os.path.join(HERE, 'examples', '01_watch_geometry.py')
STATIC = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
P = Rhino.Geometry.Point3d

LOOP = '''#! python 3
"""Grow a spiral, point by point -- until stopped."""
import math
import time
import Rhino.Geometry as rg

points = []
turn = 0.0
while True:
    turn += 0.05
    points.append(rg.Point3d(turn * math.cos(turn), turn * math.sin(turn), 0))
    time.sleep(0.002)
'''


def paused_controls():
    hook = lt.hook()
    return list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))


def size(window, width, height, x=40, y=40):
    window.Location = Eto.Drawing.Point(x, y)
    window.Size = Eto.Drawing.Size(width, height)


def test():
    yield 2
    import shots
    doc = Rhino.RhinoDoc.ActiveDoc
    saved = []

    def keep(bitmap, name):
        saved.append(shots.save(bitmap, name))
        lt.note('shot: ' + saved[-1])

    def editor_shot(window, name):
        keep(shots.editor(window, lt.window_handle(window)), name)

    # --- lesson 01, paused at its breakpoint, watched -------------------------------------------
    Rhino.RhinoApp.RunScript('_ScriptEditor', False)
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 60)
    editor = lt.editors()[0]
    lt.member(editor, 'Browser').Visible = False  # the Explorer: more room for the code
    state = lt.member(editor, 'ActiveState')
    [m for m in state.GetType().GetMethods(lt.ANY) if m.Name == 'RemoveCodes' and len(m.GetParameters()) == 0][0].Invoke(state, None)
    yield 1
    code = Rhino.Runtime.Code.RhinoCode.CreateCode(System.Uri(LESSON01))
    add = [m for m in state.GetType().GetMethods(lt.ANY) if m.Name == 'AddCode' and len(m.GetParameters()) == 2
           and m.GetParameters()[0].ParameterType.FullName == 'Rhino.Runtime.Code.Code'][0]
    add.Invoke(state, [code, System.Activator.CreateInstance(add.GetParameters()[1].ParameterType, True)])
    yield 3
    active = lt.member(lt.member(state, 'ActiveCode'), 'Code')
    line = [i + 1 for i, l in enumerate(open(LESSON01, encoding='utf-8').read().split('\n')) if 'BREAKPOINT' in l and 'rg.' in l][0]
    from Rhino.Runtime.Code.Execution.Debugging import CodeReferenceBreakpoint
    active.DebugControls.Breakpoints.Add(CodeReferenceBreakpoint(active, line))
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_On "%s"' % LESSON01, False)
    yield lt.wait_for(lambda: len(paused_controls()) > 0, 60)
    controls = paused_controls()[0]
    hook = lt.hook()
    watch_list = hook.GetType().GetMethod('WatchListFor', lt.ANY).Invoke(hook, [editor])
    items = lt.member(watch_list, 'Items')
    while items.Count > 0:
        items.RemoveAt(0)
    add_watch = hook.GetType().GetMethod('AddRhinoWatch', lt.ANY)
    add_watch.Invoke(hook, [controls, 'outline', None])
    add_watch.Invoke(hook, [controls, 'corners', None])
    tabs = lt.plugin_type('ConsoleTabs')
    lt.check('V Watch in front', tabs.GetMethod('Show', STATIC).Invoke(None, [editor, 'Watch']), True)
    yield 2
    access = System.Activator.CreateInstance(lt.plugin_type('ScriptEditorAccess'), [editor])
    find_editor = lt.plugin_type('CodeViewScripts').GetField('FindEditor', STATIC).GetValue(None)

    def reveal(first):
        lt.plugin_type('HoverPreview').GetMethod('Script', lt.ANY).Invoke(None, [access.Monaco, find_editor + 'editor.setScrollTop(editor.getTopForLineNumber(%d)); return "ok";' % first])

    # The view: Top, the drawn values in its left part (an editor goes over the right)
    view = doc.Views.Find('Top', False)
    doc.Views.ActiveView = view
    view.Maximized = True
    view.ActiveViewport.ZoomBoundingBox(Rhino.Geometry.BoundingBox(P(-0.8, -5.1, 0), P(25.2, 11.15, 0)))
    doc.Views.Redraw()
    size(editor, 560, 540)
    yield 2
    reveal(line - 8)
    yield 2
    keep(shots.viewport(view, 1200, 750), 'watch_viewport')
    editor_shot(editor, 'watch_editor')
    # The editor alone: the values at the ends of the lines
    size(editor, 960, 600)
    yield 2
    reveal(line - 14)
    yield 2
    editor_shot(editor, 'values_editor')

    # V Python in the paused frame: the swapped corners, the fix drawn in Rhino
    size(editor, 560, 540)
    lt.check('V Python in front', tabs.GetMethod('Show', STATIC).Invoke(None, [editor, 'Python']), True)
    yield 1
    for typed in ('print(corners[2], corners[3])',
                  'fixed = rg.PolylineCurve([corners[0], corners[1], corners[3], corners[2], corners[0]])',
                  'show(fixed)',
                  'rg.Brep.CreatePlanarBreps(fixed, 0.001)[0].GetArea()'):
        tabs.GetMethod('TypeInPython', STATIC).Invoke(None, [editor, typed])
        yield 1
    reveal(line - 8)
    yield 1
    editor_shot(editor, 'python_editor')
    doc.Views.Redraw()
    yield 1
    keep(shots.viewport(view, 1200, 750), 'python_viewport')
    tabs.GetMethod('TypeInPython', STATIC).Invoke(None, [editor, 'clear()'])  # its drawing gone again
    yield 1
    controls.Stop()
    yield lt.wait_for(lambda: len(paused_controls()) == 0, 20)
    active.DebugControls.Breakpoints.Clear()
    yield 2

    # --- Stop and record: Ctrl+Alt+B on a script run by RunPythonScript -------------------------
    old = lt.static_call('MiniVivisectionSettings', 'GetText', 'RunCommand')
    lt.static_call('MiniVivisectionSettings', 'Set', 'RunCommand', 'Stop and record')
    try:
        [m for m in state.GetType().GetMethods(lt.ANY) if m.Name == 'RemoveCodes' and len(m.GetParameters()) == 0][0].Invoke(state, None)
        script = lt.write_file('grow_spiral.py', LOOP)
        # From the command line, after this step: the script's own call stack; the stop from
        # another thread, as the hotkey's (the script holds Rhino's UI thread)
        threading.Timer(4.0, lambda: lt.static_call('StoppedRuns', 'Stop')).start()
        Rhino.RhinoApp.SendKeystrokes('_-RunPythonScript "%s"' % script, True)
        yield 10
        opened = lt.editors()
        if opened:
            size(opened[0], 960, 600)
            lt.member(opened[0], 'Browser').Visible = False
            yield 3
            editor_shot(opened[0], 'stoprecord_editor')
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'RunCommand', old)
    for w in lt.editors():
        w.Close()
    yield 2

    # --- Markdown text in Rhino ---------------------------------------------------------------
    sys.path.insert(0, os.path.join(HERE, 'tools'))  # RunPythonScript may have reset the path
    import make_rhino_examples as mk
    Rhino.RhinoDoc.ActiveDoc.Modified = False  # no question about saving the test's model
    Rhino.RhinoApp.RunScript('_-Open "%s"' % mk.PATH, False)
    yield 3
    doc = Rhino.RhinoDoc.ActiveDoc
    view = doc.Views.Find('Top', False) or doc.Views.ActiveView
    doc.Views.ActiveView = view
    view.Maximized = True
    texts = [o for o in doc.Objects if isinstance(o.Geometry, Rhino.Geometry.TextEntity)]
    links_heading = next(o for o in texts if o.Geometry.PlainText.strip() == 'Links')
    lists = next(o for o in texts if o.Geometry.PlainText.startswith('Lists, also nested'))
    ring_link = next(o for o in texts if 'the Ring component' in o.Geometry.PlainText)

    def zoom_on(objects, inflate):
        box = Rhino.Geometry.BoundingBox.Empty
        for o in objects:
            box.Union(o.Geometry.GetBoundingBox(True))
        box.Inflate(inflate)
        # as wide as a 1200 x 750 capture is (the view is wider: the capture keeps its height)
        width, height = box.Max.X - box.Min.X, box.Max.Y - box.Min.Y
        if width < 1.6 * height:
            box = Rhino.Geometry.BoundingBox(P(box.Min.X - (1.6 * height - width) / 2, box.Min.Y, 0), P(box.Max.X + (1.6 * height - width) / 2, box.Max.Y, 0))
        else:
            box = Rhino.Geometry.BoundingBox(P(box.Min.X, box.Min.Y - (width / 1.6 - height) / 2, 0), P(box.Max.X, box.Max.Y + (width / 1.6 - height) / 2, 0))
        view.ActiveViewport.ZoomBoundingBox(box)
        doc.Views.Redraw()

    # the links' section in view, the mouse over the one into Grasshopper: its hint
    top = links_heading.Geometry.GetBoundingBox(True).Max.Y
    section = [o for o in texts if o.Geometry.GetBoundingBox(True).Max.Y <= top + 0.01 and o.Geometry.GetBoundingBox(True).Min.Y >= top - 12]
    zoom_on(section, 2.0)
    yield 1
    links = ring_link.Attributes.GetUserString('minivivisection.markdown.links').split('\n')[0].split(' ')
    style = doc.DimStyles.FindId(ring_link.Geometry.DimensionStyleId) or doc.DimStyles.Current
    groups = list(ring_link.Geometry.CreateCurvesGrouped(style, False, 1.0, 0.0))
    glyph = Rhino.Geometry.BoundingBox.Empty
    for curve in groups[int(links[0]) + 6]:
        glyph.Union(curve.GetBoundingBox(True))
    point = view.ActiveViewport.WorldToClient(glyph.Center)
    ctypes.windll.user32.SendMessageW(ctypes.c_void_p(view.Handle.ToInt64()), 0x0200, 0, (int(point.Y) << 16) | (int(point.X) & 0xFFFF))
    yield 1
    lt.note('hint: %s' % lt.plugin_type('MarkdownTextMouse').GetProperty('Hint', STATIC).GetValue(None))
    vsize = view.ActiveViewport.Size
    keep(view.CaptureToBitmap(), 'markdown_hint_viewport')
    with open(os.path.join(shots.RAW, 'markdown_hint.txt'), 'w') as f:
        f.write('%d %d %d %d' % (vsize.Width, vsize.Height, point.X, point.Y))
    ctypes.windll.user32.SendMessageW(ctypes.c_void_p(view.Handle.ToInt64()), 0x0200, 0, 0)
    yield 0.5

    # the link to the circle followed: zoomed to it (a tenth of the view), marked
    lt.plugin_type('MarkdownLinks').GetMethod('Follow', STATIC).Invoke(None, ['rhino:Logo', os.path.dirname(mk.PATH)])
    yield 0.6
    keep(shots.viewport(view, 1200, 750), 'markdown_mark_viewport')
    yield 3

    # the dialog for one part (the nested list), over the text
    zoom_on([o for o in texts if o.Geometry.GetBoundingBox(True).Max.Y >= lists.Geometry.GetBoundingBox(True).Min.Y - 8], 1.0)
    keep(shots.viewport(view, 1200, 750), 'markdown_viewport')
    builder = lt.plugin_type('MarkdownRhinoText')
    text = builder.GetMethod('Find', STATIC).Invoke(None, [doc, lists])
    reconciled = builder.GetMethod('Reconcile', STATIC).Invoke(None, [doc, text])
    parts = lt.plugin_type('MarkdownTextCommand').GetMethod('EditorDialog', STATIC).Invoke(
        None, [doc, reconciled.Item1, reconciled.Item2, reconciled.Item4, reconciled.Item5, reconciled.Item3])
    dialog = parts.Item1
    dialog.ShowModalAsync(Rhino.UI.RhinoEtoApp.MainWindow)
    yield 2
    size(dialog, 640, 440, 60, 60)
    yield 1
    editor_shot(dialog, 'markdown_dialog')
    dialog.Close(False)
    yield 1
    lt.check('shots taken', len(saved) >= 10, True)


lt.run('shots_rhino', test)
