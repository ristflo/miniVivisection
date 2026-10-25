#! python 3
"""16 -- Pause a long-running script, fix it, let it run on: Break now

The bug
    The script draws a watch face -- the numbers 1 to 12, an hour marker at every
    number, a second marker in between -- and moves its second hand one mark every
    100 ms, for a minute (Esc ends it sooner). But the hand starts at 3 instead of 12,
    and it runs backwards.

Try this
    1. Click Debug (the editor's Debug button, no breakpoint needed). The watch is
       drawn and the hand moves; Rhino stays usable meanwhile.
    2. Press Ctrl+Alt+B (Break now) while it runs: the script pauses at its next line,
       in the loop.
    3. Hover  tick  and  angle : tick counts the marks so far, angle is the hand's
       direction -- 0 at the start of each round, and 0 is 3 o'clock (angles count
       from the x axis, counter-clockwise). Add  hand  to the V°Watch tab: it is
       drawn where the hand is now.
    4. Fix it without stopping. In the V°Python tab:
           def angle_of(tick):
               return math.pi / 2 - tick * 2 * math.pi / 60
       Press F5 (Continue): the hand jumps to the right mark and runs clockwise.
    5. Stop (Shift+F5) and fix the code the same way, for the next run.
    6. Run it with RunPythonScript, with V°Settings > Running and pausing in
       Rhino > RunPythonScript command on Stop and record: Ctrl+Alt+B then
       stops it, and the Script Editor shows where.

Without miniVivisection
    A script that runs for a minute can only be watched or killed. You'd add prints,
    run it again, and read numbers -- the debugger's Break would freeze Rhino with it.
"""
import math
import time

import System
import Rhino
import Rhino.Geometry as rg
import scriptcontext

RADIUS = 50.0
ROUNDS = 10          # 10 rounds of 60 marks, 100 ms each: a minute
STEP = 0.1           # seconds per mark


def angle_of(tick):
    # The hand's direction for mark `tick` (0 to 59).
    return tick * 2 * math.pi / 60


def at(angle, radius):
    return rg.Point3d(radius * math.cos(angle), radius * math.sin(angle), 0)


doc = Rhino.RhinoDoc.ActiveDoc
layer = doc.Layers.FindName("Watch face")      # set-up: the face, on a layer of its own
if layer is None:
    layer = doc.Layers[doc.Layers.Add("Watch face", System.Drawing.Color.DimGray)]
for old in doc.Objects.FindByLayer(layer) or []:
    doc.Objects.Delete(old, True)
face = Rhino.DocObjects.ObjectAttributes()
face.LayerIndex = layer.Index
marker = face.Duplicate()
marker.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject
marker.ObjectColor = System.Drawing.Color.Black
second = face.Duplicate()
second.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject
second.ObjectColor = System.Drawing.Color.Red

doc.Objects.AddCircle(rg.Circle(rg.Point3d.Origin, RADIUS), face)
for mark in range(60):
    direction = math.pi / 2 - mark * 2 * math.pi / 60
    if mark % 5 == 0:   # an hour marker, long, and its number inside it
        doc.Objects.AddLine(at(direction, RADIUS * 0.85), at(direction, RADIUS), marker)
        number = 12 if mark == 0 else mark // 5
        plane = rg.Plane(at(direction, RADIUS * 0.72), rg.Vector3d.ZAxis)
        doc.Objects.AddText(str(number), plane, RADIUS * 0.09, "Arial", True, False,
                            rg.TextJustification.MiddleCenter, face)
    else:               # a second marker, short
        doc.Objects.AddLine(at(direction, RADIUS * 0.94), at(direction, RADIUS), face)
doc.Objects.AddPoint(rg.Point3d.Origin, marker)
doc.Views.ActiveView.ActiveViewport.ZoomBoundingBox(rg.BoundingBox(-RADIUS * 1.2, -RADIUS * 1.2, 0, RADIUS * 1.2, RADIUS * 1.2, 0))

hand = rg.Line(rg.Point3d.Origin, at(angle_of(0), RADIUS * 0.9))
hand_id = doc.Objects.AddLine(hand, second)
doc.Views.Redraw()

for tick in range(ROUNDS * 60):
    if scriptcontext.escape_test(False):
        break
    angle = angle_of(tick % 60)
    hand = rg.Line(rg.Point3d.Origin, at(angle, RADIUS * 0.9))
    doc.Objects.Replace(hand_id, rg.LineCurve(hand))
    doc.Views.Redraw()
    until = time.time() + STEP
    while time.time() < until:   # waiting with Rhino's message loop going: Rhino stays usable
        Rhino.RhinoApp.Wait()
        time.sleep(0.005)

print("The watch ran {0} marks.".format(tick + 1))
