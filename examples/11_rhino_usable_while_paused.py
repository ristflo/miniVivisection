#! python 3
"""11 -- Use Rhino while the script is paused: FreePause

The bug
    The script counts the curves on layer "Inputs" and finds none, though there
    are five circles in the model. (The script draws them first, on a layer of
    its own.)

Try this
    1. Set a breakpoint on the line marked BREAKPOINT, then Debug (F5).
    2. Rhino still works while the script waits. Open the Layers panel: the
       layer is called "Input", without the s. Click a circle, run Distance,
       pan and zoom.
    3. Fix it without stopping. Either:
       - Rename the layer to "Inputs" in the Layers panel, or
       - In the V°Python tab:  layer_name = "Input"
       Then F5: the script finds the five circles.
    4. Draw one more circle on that layer while paused, then F5: it is counted.
    5. Continue and step with the editor's buttons or F5 / F10 / F11.

Without miniVivisection
    Rhino is frozen while the debugger is paused: no panels, no commands, no
    selecting. You'd stop, look, fix, and run again.
"""
import System
import Rhino
import Rhino.Geometry as rg

doc = Rhino.RhinoDoc.ActiveDoc
if doc.Layers.FindName("Input") is None:   # set-up: five circles on layer "Input"
    index = doc.Layers.Add("Input", System.Drawing.Color.SteelBlue)
    attributes = Rhino.DocObjects.ObjectAttributes()
    attributes.LayerIndex = index
    for k in range(5):
        doc.Objects.AddCircle(rg.Circle(rg.Point3d(k * 6, 30, 0), 2), attributes)
    doc.Views.Redraw()

layer_name = "Inputs"
found = doc.Objects.FindByLayer(layer_name) or []   # <-- BREAKPOINT
print("{0} object(s) on layer {1}".format(len(found), layer_name))
