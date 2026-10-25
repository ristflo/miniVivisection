#! python 3
"""07 -- Draw plain data: lists of numbers, Interpret as

The bug
    A terrain profile is merged from two surveys, as plain (x, y, z) tuples.
    The profile has a jump somewhere, and the numbers look plausible.

Try this
    1. Set a breakpoint on the line marked BREAKPOINT, then Debug (F5).
    2. In the V°Watch tab, watch  profile . Plain (x, y, z) tuples are drawn.
    3. Right-click the row > Interpret as > Polyline. The profile is one line:
       at x = 20 it jumps up, and from there its waves are 3.28 times taller.
       Survey B is in feet, not meters.
    4. Try the other choices: Points, Closed polyline, Interpolated curve, Plain data (not drawn).
    5. Watch a slice:  profile[18:24]  draws only where the two surveys meet.
    6. Fix it: convert survey_b, e.g. [(x, y, z / 3.28) for x, y, z in survey_b].

Without miniVivisection
    Lists of numbers are text in the Variables tab. To see them you'd build
    Point3d and Polyline objects in code and add them to the document.
"""
import math

survey_a = [(x, 0.0, 2.0 + math.sin(x / 3.0)) for x in range(0, 20)]                   # meters
survey_b = [(x, 0.0, (2.0 + math.sin(x / 3.0)) * 3.28) for x in range(20, 40)]         # feet!

profile = survey_a + survey_b
highest = max(z for _, _, z in profile)   # <-- BREAKPOINT
print("highest point: {0:.2f} m".format(highest))
