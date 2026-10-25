#! python 3
"""13 -- Ask Claude about the paused script: the V°Claude tab

Needs: the Claude Code CLI ("claude"), signed in, and V°AI ON in the editor's top bar.

The bug
    A window opening is cut out of a wall, but the boolean difference gives
    nothing and the script raises an error. Why?

Try this
    1. Set a breakpoint on the line marked BREAKPOINT, then Debug (F5).
    2. Open the V°Claude tab and ask, in your own words:
           Why does the boolean difference give nothing here?
       or type  /why . Claude reads the paused values (wall, opening, offset_y)
       and the code. The opening sits beside the wall, not in it: offset_y
       has the wrong value.
    3. Type  /fix : Claude proposes an edit. Edits made while the script is
       paused are queued: /pending lists them, /apply or /reject decides,
       /undo takes one back.
    4. Ask Claude to set a breakpoint with a condition, to step, or to continue.
       It can do that.
    5. /help lists all slash commands. V°Settings > Claude: whether Claude may
       run Python in Rhino.

Without miniVivisection
    Copy values and code into a chat by hand, and hope you copied the right ones.
"""
import Rhino.Geometry as rg

thickness = 0.3
wall = rg.Box(rg.Plane.WorldXY, rg.Interval(0, 10), rg.Interval(0, thickness), rg.Interval(0, 3)).ToBrep()

offset_y = -1.0   # the bug: the opening should go through the wall (0.0)
opening = rg.Box(rg.Plane.WorldXY, rg.Interval(4, 6), rg.Interval(offset_y - 0.2, offset_y + thickness + 0.2),
                 rg.Interval(1, 2.2)).ToBrep()

result = rg.Brep.CreateBooleanDifference(wall, opening, 0.001)   # <-- BREAKPOINT
if not result:
    raise RuntimeError("the boolean difference gave nothing")
print("wall with opening: {0} brep(s)".format(len(result)))
