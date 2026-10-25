#! python 3
"""01 -- See your geometry in Rhino: the V°Watch tab

The bug
    The script should make a flat 8 x 5 panel, but no panel comes out. The four
    corners look fine as numbers.

Try this
    1. Set a breakpoint on the line marked BREAKPOINT (click left of its line
       number), then Debug (F5).
    2. In the V°Watch tab (bottom), type  outline  and press Enter.
       Rhino draws it: a bow tie, not a rectangle. The outline crosses itself,
       so it can't bound a panel.
    3. Watch  corners  as well, and click its row: the points light up in Rhino.
       Click one of the drawn points in a viewport: its row is selected.
       Expand the row (>) to see each point: corners[2] is where corners[3]
       should be.
    4. Click the eye in the Show column to hide a watch; click again to show it.
    5. Stop (Shift+F5), swap the two lines marked SWAP ME, and debug again.
       A rectangle, and the panel is made.

Without miniVivisection
    You read four Point3d values in the Variables tab and draw the shape in your
    head, or add the curve to the document in code, rerun, and delete it again.
"""
import Rhino.Geometry as rg

width, height = 8.0, 5.0
corners = [
    rg.Point3d(0, 0, 0),
    rg.Point3d(width, 0, 0),
    rg.Point3d(0, height, 0),       # SWAP ME
    rg.Point3d(width, height, 0),   # SWAP ME
]
outline = rg.PolylineCurve(corners + [corners[0]])
panels = rg.Brep.CreatePlanarBreps(outline, 0.001)   # <-- BREAKPOINT
print("panel made" if panels else "no panel: the outline isn't a valid boundary")
