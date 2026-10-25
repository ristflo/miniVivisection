#! python 3
"""10 -- Draw and edit your own classes: visualizers

The bug
    Beams are your own class, not Rhino geometry, so nothing knows how to draw
    them. One beam was typed with its end at height 0 instead of 3.5, so it
    slopes.

Try this
    1. Set a breakpoint on the line marked BREAKPOINT, then Debug (F5).
    2. In the V°Watch tab, watch  beams . The visualizer below (draw_beam) turns each Beam
       into a line, so they are drawn. The sloping one stands out. Click it in
       Rhino: its row is selected.
    3. Hover  beams  > Edit in Rhino, or Auto edit. Drag the low end of the
       sloping beam up with the Gumball. The inverse (move_beam) writes the
       line back into the Beam: hover  beams  again.
    4. Comment out the two @rhinoviv lines and debug again: the V°Watch row
       says there's nothing to draw. That is what a class without a visualizer
       looks like.
    5. In the V°Python tab,  visualizers()  lists the registered visualizers.

Without miniVivisection
    Your own objects are fields of numbers in the Variables tab.
"""
import Rhino.Geometry as rg
import rhinoviv   # comes with miniVivisection


class Beam(object):
    def __init__(self, name, start, end):
        self.name, self.start, self.end = name, start, end


@rhinoviv.visualize(Beam)
def draw_beam(beam):
    return rg.LineCurve(rg.Point3d(*beam.start), rg.Point3d(*beam.end))


@rhinoviv.inverse(Beam)
def move_beam(beam, edited):
    a, b = edited.PointAtStart, edited.PointAtEnd
    beam.start, beam.end = [a.X, a.Y, a.Z], [b.X, b.Y, b.Z]


beams = [
    Beam("B1", [0, 0, 3.5], [12, 0, 3.5]),
    Beam("B2", [0, 6, 3.5], [12, 6, 3.5]),
    Beam("B3", [0, 12, 3.5], [12, 12, 0]),    # the typo: the end should be at 3.5
    Beam("B4", [0, 18, 3.5], [12, 18, 3.5]),
]
lowest = min(min(b.start[2], b.end[2]) for b in beams)   # <-- BREAKPOINT
print("lowest beam end: {0}".format(lowest))
