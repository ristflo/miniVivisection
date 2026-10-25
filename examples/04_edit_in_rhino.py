#! python 3
"""04 -- Change the script's data with Rhino's tools: Edit in Rhino

The bug
    The roof is lofted through three profiles. The middle one has a spike: a
    control point typed as z = 50 instead of 5. Which point is it, and what
    value would look right?

Try this
    1. Set breakpoints on the lines marked BREAKPOINT 1 and BREAKPOINT 2, then
       Debug (F5). It pauses at 1.
    2. Hover  middle  and click Edit in Rhino. (Or: V°Watch tab, watch  middle ,
       toolbar button Edit in Rhino.) The points become real Rhino points, on
       layer "Script Editor Watch::Edit".
    3. In Rhino, select the high point and drag it down with the Gumball.
       The change is written back into the paused script: hover  middle  again.
    4. Continue (F5) to BREAKPOINT 2. In the V°Watch tab, watch  roof : the
       roof is built from your edited points.
    5. Copy the corrected list from the first line of the  middle  tooltip into
       the code, so the fix stays.
    6. Auto edit (V°Watch toolbar, ON by default): every watch that is shown
       becomes editable in Rhino at each pause, without clicking Edit in Rhino.

Without miniVivisection
    Guess a number, edit, rerun, look, repeat. The script can't take values back
    from Rhino while it is paused.
"""
import Rhino.Geometry as rg

front = [[0, 0, 0], [5, 0, 4], [10, 0, 0]]
middle = [[0, 6, 0], [5, 6, 50], [10, 6, 0]]   # the typo: 50 should be about 5
back = [[0, 12, 0], [5, 12, 4], [10, 12, 0]]

ready = True   # <-- BREAKPOINT 1


def profile(points):
    return rg.Curve.CreateInterpolatedCurve([rg.Point3d(*p) for p in points], 3)


sections = [profile(front), profile(middle), profile(back)]
roof = rg.Brep.CreateFromLoft(sections, rg.Point3d.Unset, rg.Point3d.Unset, rg.LoftType.Normal, False)[0]
print("roof built, middle profile: {0}".format(middle))   # <-- BREAKPOINT 2
