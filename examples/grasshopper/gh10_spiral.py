#! python 3
"""Spiral: n points on a spiral, and a smooth curve through them (GH 10's Spiral runs this file)."""
import math
import time
import Rhino.Geometry as rg

pts = []
spiral = None
if run:
    for i in range(n * n):              # one point per step
        t = i * 0.3
        pts.append(rg.Point3d(t * math.cos(t), t * math.sin(t), 0))
        time.sleep(0.02)                # measuring a point takes a moment
    spiral = rg.Curve.CreateInterpolatedCurve(pts, 3)
