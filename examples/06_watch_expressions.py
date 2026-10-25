#! python 3
"""06 -- Watch expressions, not just names

The bug
    Eight segments should join into one closed outline. They join, but the
    outline isn't closed. Somewhere there is a gap, too small to see.

Try this
    1. Set a breakpoint on the line marked BREAKPOINT, then Debug (F5).
    2. In the V°Watch tab, type this expression and press Enter:
           [a.PointAtEnd.DistanceTo(b.PointAtStart) for a, b in zip(segments, segments[1:] + segments[:1])]
       Expand the row: item [4] is 0.05, the others 0.0. The gap is between
       segments[4] and segments[5].
    3. Watch  segments[4]  and  segments[5] : only those two are drawn. Select a
       row to highlight it in Rhino. Zoom in on their joint in Rhino.
    4. Watch  [s.PointAtStart for s in segments] : all start points, drawn.
    5. The editor's own Watch tab (next to Variables) also takes expressions now,
       e.g.  joined[0].IsClosed  or  segments[5].GetLength() .
    6. Fix it: remove the  + 0.05  below.

Without miniVivisection
    The Watch tab takes plain names only. You'd write the distance loop into
    the script, print it, and run again.
"""
import math
import Rhino.Geometry as rg

corners = [rg.Point3d(10 * math.cos(math.radians(45 * k)), 10 * math.sin(math.radians(45 * k)), 0) for k in range(8)]
segments = []
for k in range(8):
    start, end = corners[k], corners[(k + 1) % 8]
    if k == 5:
        start = rg.Point3d(start.X + 0.05, start.Y, 0)   # the gap
    segments.append(rg.LineCurve(start, end))

joined = rg.Curve.JoinCurves(segments, 0.01)
print("{0} piece(s), closed: {1}".format(len(joined), joined[0].IsClosed))   # <-- BREAKPOINT
