// #! csharp
// Test script for the experimental MiniVivisection plug-in, C# (see README.md).
//
// 1. Open this file in Rhino's Script Editor, set a breakpoint on the marked line,
//    and debug (F5).
// 2. Add watches in the V Watch tab: crv, points, circle, plane. Only plain variable names work for C#;
//    expressions like crv.PointAtStart are evaluated for Python scripts only.
// 3. Step (F10): the drawing follows the current values.
using System;
using System.Collections.Generic;
using Rhino.Geometry;

var points = new List<Point3d>
{
    new Point3d(0, 0, 0), new Point3d(5, 10, 0), new Point3d(10, -10, 0), new Point3d(15, 0, 0),
};
Curve crv = Curve.CreateInterpolatedCurve(points, 3);
var circle = new Circle(new Point3d(7.5, 0, 0), 4);
var plane = new Plane(new Point3d(20, 0, 0), new Vector3d(1, 1, 1));

for (int i = 0; i < 5; i++)
{
    crv.Translate(new Vector3d(0, 0, 2)); // <-- breakpoint here
    circle.Radius += 1;
    plane.Rotate(0.3, Vector3d.ZAxis);
}

Console.WriteLine("done");
