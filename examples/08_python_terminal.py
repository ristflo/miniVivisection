#! python 3
"""08 -- Try fixes in the paused script: the V°Python tab

The bug
    Rounding the corners of an L-shaped plan fails: CreateFilletCornersCurve
    gives None. The radius of 4 is too large for the 6 wide wing. Which radius
    works?

Try this
    1. Set a breakpoint on the line marked BREAKPOINT, then Debug (F5).
    2. Open the V°Python tab. Its code runs in the paused script: plan, radius
       and rg are there. Type (Enter runs it):
           test = rg.Curve.CreateFilletCornersCurve(plan, 2.0, 0.001, 0.0174)
           show(test)
       The rounded plan is drawn in Rhino. Try 2.5 and 3.0: show(...) again.
           clear()            removes what show() drew
    3. Change the script's own variable, then continue:
           radius = 2.5
       Press F5: the script goes on with 2.5 and prints "rounded".
    4. More commands: watch('plan') adds it to V°Watch, bake(test) copies it into
       the document, breakpoints() lists conditions and hits.

Without miniVivisection
    Change the number, run again, look, for every try. The script's state is
    gone after each run.
"""
import Rhino.Geometry as rg

plan = rg.PolylineCurve([
    rg.Point3d(0, 0, 0), rg.Point3d(20, 0, 0), rg.Point3d(20, 6, 0),
    rg.Point3d(6, 6, 0), rg.Point3d(6, 18, 0), rg.Point3d(0, 18, 0), rg.Point3d(0, 0, 0),
])
radius = 4.0

rounded = rg.Curve.CreateFilletCornersCurve(plan, radius, 0.001, 0.0174)   # <-- BREAKPOINT
print("rounded" if rounded else "radius {0} is too large".format(radius))
