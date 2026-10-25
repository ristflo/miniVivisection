#! python 3
"""17 -- A runtime error: stop where it happens, see why

The bug
    A garden path was surveyed as 10 points. The script draws it and marks how
    sharply it turns at every point -- and crashes half way:
    "ValueError: math domain error". The path looks harmless: why?

Try this
    1. Run it (the editor's Run): the error, its line in turn_angle(), and some
       dots drawn -- but at which point did it fail, with which numbers?
    2. Debug it (the editor's Debug button, no breakpoint needed): the debugger
       stops at the error itself, the line marked with the exception.
       Hover  cosine : 1.0000000000000002 -- a hair over 1, and acos is only
       defined from -1 to 1.
    3. The Call Stack tab: turn_angle() was called from the loop -- click that
       frame and hover  i : 5. Points 4, 5 and 6 lie on one straight line, so
       the cosine should be exactly 1; rounding made it a little more.
       In the V°Watch tab, watch  pts[4:7] : the three points.
    4. Fix it: clamp the cosine before acos --
           return math.degrees(math.acos(max(-1.0, min(1.0, cosine))))
       Stop (Shift+F5), change the line, run again: every point gets its angle.
    5. Run it with RunPythonScript, with V°Settings > Running and pausing in
       Rhino > RunPythonScript command on Stop and record: the error is
       recorded. Open the Script Editor: it shows the line, with the call stack
       and the values of that moment.

Without miniVivisection
    The message names the line, not the point: you'd print every cosine with
    17 digits, and wonder why 1.0000000000000002 isn't 1.
"""
import math

import Rhino
import Rhino.Geometry as rg

# The survey: x, y in meters. Points 4, 5 and 6 are on one straight line.
pts = [(0.0, 0.0), (3.0, 1.0), (5.0, 4.0), (8.0, 4.5), (9.5, 7.0),
       (9.6, 7.2), (9.71, 7.42), (12.0, 10.0), (15.0, 9.5), (17.0, 12.0)]


def turn_angle(before, here, after):
    # How sharply the path turns at `here`, in degrees: 0 is straight on.
    ax, ay = here[0] - before[0], here[1] - before[1]
    bx, by = after[0] - here[0], after[1] - here[1]
    cosine = (ax * bx + ay * by) / (math.sqrt(ax * ax + ay * ay) * math.sqrt(bx * bx + by * by))
    return math.degrees(math.acos(cosine))


doc = Rhino.RhinoDoc.ActiveDoc
doc.Objects.AddPolyline(rg.Polyline([rg.Point3d(x, y, 0) for x, y in pts]))
angles = []
for i in range(1, len(pts) - 1):
    angle = turn_angle(pts[i - 1], pts[i], pts[i + 1])
    angles.append(angle)
    doc.Objects.AddTextDot("%d: %.0f°" % (i, angle), rg.Point3d(pts[i][0], pts[i][1], 0))
doc.Views.Redraw()

sharpest = max(range(len(angles)), key=lambda k: angles[k]) + 1
print("The sharpest turn is at point {0}: {1:.0f} degrees.".format(sharpest, angles[sharpest - 1]))
