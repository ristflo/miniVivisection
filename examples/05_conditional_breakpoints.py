#! python 3
"""05 -- Pause only when it matters: conditions, hit counts, log messages

The bug
    599 edges, each divided into 10 parts, should give 599 x 11 division points.
    The script reports fewer, and no error. Some edges silently give nothing.
    Which ones, and why?

Try this
    1. Set a breakpoint on the line marked BREAKPOINT. A plain breakpoint would
       pause 599 times.
    2. Right-click that line > Breakpoint Condition… > type  params is None
       and press Enter. The line now shows "if params is None" after it.
    3. Debug (F5). It pauses only at the bad edges, three of them. Hover  a
       and  b : the same point. Rounding to the grid made two points in a row
       equal, so the edge has length 0.
    4. Right-click > Hit Count… > 2 : of the passes where the condition holds,
       it pauses only at the second. (>= 2 : from the second on; % 2 : every second.)
    5. Right-click the line  total += ...  > Log Message… >
       i={i} a={a} b={b}
       The Terminal prints a line per pass, without pausing and without a print()
       in the code.
    6. The Breakpoints tab lists the conditions and their hits; double-click one
       to edit it. Right-click > Remove Condition for a plain breakpoint.
    7. F10 and F11 ignore conditions; only F5 (Continue) uses them.

Without miniVivisection
    Add  if params is None: print(...)  to the code and rerun, or press F5
    hundreds of times.
"""
import random
import Rhino.Geometry as rg

random.seed(7)
points = [rg.Point3d(round(random.uniform(0, 12)), round(random.uniform(0, 12)), 0) for _ in range(600)]

total = 0
for i in range(len(points) - 1):
    a, b = points[i], points[i + 1]
    edge = rg.LineCurve(a, b)
    params = edge.DivideByCount(10, True)
    total += len(params) if params else 0   # <-- BREAKPOINT
print("{0} division points, expected {1}".format(total, 11 * (len(points) - 1)))
