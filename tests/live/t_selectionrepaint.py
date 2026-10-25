#! python 3
# Review 2026-10-15, B2: Rhino's selection events don't repaint Grasshopper's canvas while no
# V Edit shows objects -- a script selecting hundreds of objects gave as many canvas paints.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

paints = []


def grasshopper_ready():
    import Grasshopper
    return Grasshopper.Instances.ActiveCanvas is not None and lt.assembly('miniVivisection.GH') is not None


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    canvas = Grasshopper.Instances.ActiveCanvas
    doc = Rhino.RhinoDoc.ActiveDoc
    ids = [doc.Objects.AddPoint(Rhino.Geometry.Point3d(i, 0, 0)) for i in range(300)]
    doc.Views.Redraw()
    yield 1
    handler = Grasshopper.GUI.Canvas.GH_Canvas.CanvasPaintEndEventHandler(lambda sender: paints.append(1))
    canvas.CanvasPaintEnd += handler
    try:
        for i in ids:
            doc.Objects.Select(i, True)
        doc.Objects.UnselectAll()
        yield 1
        lt.check('300 selections: a handful of canvas paints at most', len(paints) <= 5, True)
        lt.note('paints: %d' % len(paints))
    finally:
        canvas.CanvasPaintEnd -= handler
        doc.Objects.Delete(System.Collections.Generic.List[System.Guid](ids), True)


lt.run('t_selectionrepaint', test)
