#! python 3
"""
Test script for the experimental MiniVivisection plug-in (see README.md).

1. Open this file in Rhino's Script Editor (ScriptEditor command).
2. Set a breakpoint on the line marked below and start debugging (F5).
3. Add watches in the V Watch tab (type one and press Enter, or right-click
   a name > Watch in Rhino) -- each is drawn in its own color, with its name:
   - plain variables: crv, points, curves, direction, plane, box, box2, sphere, mesh
   - expressions: crv.PointAtStart, curves[0], points[1:3],
     [c.PointAtEnd for c in curves], rg.Line(crv.PointAtStart, crv.PointAtEnd)
4. Step (F10) or continue (F5): every pause redraws with the current values.
   While paused, Rhino stays usable: select objects, run commands.
   The tab's In Rhino column says what each watch is: drawn, its type, or the error.
5. The editor's own Watch tab shows the values of expressions too: try 1+2,
   crv.PointAtEnd, [c.PointAtEnd for c in curves], missing_name.
"""
import Rhino.Geometry as rg

points = [rg.Point3d(0, 0, 0), rg.Point3d(5, 10, 0), rg.Point3d(10, -10, 0), rg.Point3d(15, 0, 0)]
crv = rg.Curve.CreateInterpolatedCurve(points, 3)
curves = []
for k in range(3):
    copy = crv.DuplicateCurve()
    copy.Translate(rg.Vector3d(0, 0, -3 * (k + 1)))
    curves.append(copy)
direction = rg.Vector3d(5, 5, 5)
plane = rg.Plane(rg.Point3d(20, 0, 0), rg.Vector3d(1, 1, 1))
box = rg.Box(rg.Plane.WorldXY, rg.Interval(20, 25), rg.Interval(5, 10), rg.Interval(0, 5))
box2 = rg.Box(rg.Plane.WorldXY, rg.Interval(20, 25), rg.Interval(-15, -10), rg.Interval(0, 8))
sphere = rg.Brep.CreateFromSphere(rg.Sphere(rg.Point3d(30, 0, 0), 3))

mesh = rg.Mesh.CreateFromSphere(rg.Sphere(rg.Point3d(40, 0, 0), 3), 12, 8)
def tet():
    crv=5
    print(crv)

    return

tet()
for i in range(5):
    crv.Translate(rg.Vector3d(0, 0, 2))  # <-- breakpoint here
    curves.append(crv.DuplicateCurve())
    direction.Rotate(0.5, rg.Vector3d.ZAxis)
    plane.Rotate(0.3, rg.Vector3d.ZAxis)
    sphere.Translate(rg.Vector3d(0, 2, 0))

print("done")
