#! python 3
"""03 -- Keep the work of a script that crashes: Bake

The bug
    The script builds 40 twisted fins, then crashes at the very end on a typo
    (layers["Fin"] instead of layers["Fins"]), before anything reaches the
    document. All the work is lost.

Try this
    1. Set a breakpoint on the line marked BREAKPOINT, then Debug (F5).
    2. Hover  fins : "40 items", and the links. Click Bake: copies of the fins
       go into the document, layer "Script Editor Watch::Baked".
       (Or: V°Watch tab, watch  fins , select the row, toolbar button Bake.)
    3. Continue (F5): the debugger stops at the error itself, KeyError: 'Fin'.
       Hover  fins : you can bake from here too.
    4. Stop (Shift+F5): the baked fins stay in the document.

Without miniVivisection
    Fix the typo and run everything again, or wrap the end of the script in
    try/except to add the geometry, just in case.
"""
import math
import Rhino
import Rhino.Geometry as rg

fins = []
for i in range(40):
    angle = math.radians(i * 9)
    base = rg.Point3d(i * 1.5, 0, 0)
    top = rg.Point3d(i * 1.5, 0, 10)
    bottom_edge = rg.LineCurve(base, base + rg.Vector3d(0, 3, 0))
    top_edge = rg.LineCurve(top, top + rg.Vector3d(3 * math.sin(angle), 3 * math.cos(angle), 0))
    fins.extend(rg.Brep.CreateFromLoft([bottom_edge, top_edge], rg.Point3d.Unset, rg.Point3d.Unset, rg.LoftType.Normal, False))

doc = Rhino.RhinoDoc.ActiveDoc
layers = {"Fins": doc.Layers.CurrentLayerIndex, "Base": doc.Layers.CurrentLayerIndex}
print("built {0} fins".format(len(fins)))   # <-- BREAKPOINT
attributes = Rhino.DocObjects.ObjectAttributes()
attributes.LayerIndex = layers["Fin"]        # the typo: crashes here
for fin in fins:
    doc.Objects.AddBrep(fin, attributes)
doc.Views.Redraw()
