#! python 3
"""02 -- Read values in the code: tooltips, line-end values, "was", Tree

The bug
    ring(3.0, 12) should put 12 points evenly on a circle of radius 3. They land
    on a circle of radius 10, and not evenly. There are two mistakes.

Try this
    1. Set a breakpoint on the line marked BREAKPOINT, then Debug (F5).
    2. Line-end values: after each line of ring() you see gray values, e.g.
       angle = 0.0 and x = 10.0. Press F10 (step over) five times, once round
       the loop and back to this line: the values that changed turn orange.
    3. Scope: hover  radius  in the line  x = radius * ...  -> radius = 10.0,
       the global one. Hover  r  in  def ring(r, count)  -> r = 3.0.
       Mistake 1: the function uses the global radius instead of its r.
    4. "was": hover  angle : angle = 30.0, and below it, in gray, "was 0.0".
       math.cos wants radians, not degrees: mistake 2.
    5. Press F5 twice (each pass of the loop pauses here), then hover  points :
       "3 items" and the links. Click Tree: a small window with the points and
       their members. Leave it open and press F5: it updates at each pause.
       Esc closes it.
    6. Hover preview: hovering  points  draws them in yellow in Rhino.
    7. Fix it: x = r * math.cos(math.radians(angle)), and the same for y.

Without miniVivisection
    Tooltips show only the type, so you'd add print() lines, or look for each
    name in the Variables tab. The global and the local of the same name aren't
    told apart there.
"""
import math
import Rhino.Geometry as rg

radius = 10.0                     # the outer ring, used further down
center = rg.Point3d(0, 0, 0)


def ring(r, count):
    points = []
    for i in range(count):
        angle = 360.0 * i / count
        x = radius * math.cos(angle)
        y = radius * math.sin(angle)
        points.append(rg.Point3d(x, y, 0))   # <-- BREAKPOINT
    return points


small = ring(3.0, 12)
outer = rg.Circle(center, radius).ToNurbsCurve()
print("{0} points, the first at {1}".format(len(small), small[0]))
