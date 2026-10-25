#! python 3
# Review 2026-10-15, C4: a double-click on a link in a Markdown Panel edits the Panel (the
# code box opens) and does not follow the link -- neither the first click's pending follow
# nor the second press's.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

NOTES = """# Links

Go to [the twist](gh:Twist).
"""


def grasshopper_ready():
    import Grasshopper
    return Grasshopper.Instances.ActiveCanvas is not None and lt.assembly('miniVivisection.GH') is not None


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document
    import make_gh_examples as mk
    gha = lt.assembly('miniVivisection.GH')
    panels = gha.GetType('MiniVivisection.GH.ScriptPanels')
    STATIC = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    ANY = STATIC | System.Reflection.BindingFlags.Instance
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperMarkdown', True)
    lt.static_call('MiniVivisectionSettings', 'Set', 'MarkdownLinkFollow', 'Click')  # a plain click follows (the default)
    doc = GH_Document()
    notes = mk.panel(NOTES, 20, 20, width=320, height=160)
    notes.NickName = 'Notes'
    twist = mk.panel('the target', 2000, 1500, width=120, height=40)
    twist.NickName = 'Twist'
    for obj in (notes, twist):
        doc.AddObject(obj, False)
    mk._show_as(notes, 'Markdown')
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    canvas = Grasshopper.Instances.ActiveCanvas
    canvas.Document = doc
    doc.Enabled = True
    doc.NewSolution(False)
    canvas.Viewport.Zoom = 1.0
    canvas.Viewport.MidPoint = System.Drawing.PointF(360, 100)
    canvas.Refresh()
    yield 1
    view = panels.GetMethod('MarkdownViewOf', STATIC).Invoke(None, [notes])
    place = view.GetType().GetMethod('LinkPlace', ANY).Invoke(view, ['gh:Twist'])
    lt.check('the link drawn', place.IsEmpty, False)
    point = System.Drawing.PointF(place.X + place.Width / 2, place.Y + place.Height / 2)
    control = canvas.Viewport.ProjectPoint(point)
    at = System.Drawing.Point(int(control.X), int(control.Y))
    left = System.Windows.Forms.MouseButtons.Left
    attributes = notes.Attributes
    respond = lambda name, event: str(attributes.GetType().GetMethod(name, ANY).Invoke(attributes, [canvas, event]))
    # A double-click, as Windows sends it: down, up, down (the second), double-click, up
    first = Grasshopper.GUI.GH_CanvasMouseEvent(at, point, left, 1, 0)
    second = Grasshopper.GUI.GH_CanvasMouseEvent(at, point, left, 2, 0)
    respond('RespondToMouseDown', first)
    respond('RespondToMouseUp', first)
    respond('RespondToMouseDown', second)
    respond('RespondToMouseDoubleClick', second)
    respond('RespondToMouseUp', second)
    boxes = lambda: [c for c in canvas.Controls if c.GetType().Name == 'CodeBox']
    yield 2  # past the double-click time: a pending follow would run now
    middle = canvas.Viewport.MidPoint
    lt.check('the code box opened', len(boxes()), 1)
    lt.check('the link not followed: the canvas stayed', abs(middle.X - 360) < 5 and abs(middle.Y - 100) < 5, True)
    for box in boxes():
        box.GetType().GetMethod('Close', ANY).Invoke(box, [False])
    # A single click still follows
    respond('RespondToMouseDown', first)
    respond('RespondToMouseUp', first)
    yield 2
    middle = canvas.Viewport.MidPoint
    lt.check('a click follows the link', abs(middle.X - 2060) < 5 and abs(middle.Y - 1520) < 5, True)
    lt.static_call('MiniVivisectionSettings', 'Set', 'MarkdownLinkFollow', 'Click')  # the default
    doc.Enabled = False


lt.run('t_linkdoubleclick', test)
