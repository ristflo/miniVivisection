"""Tooltips while paused (MiniVivisection): set a breakpoint on the marked line,
debug (F5), then hover over the names. Each tooltip starts with the value as a line of
code, e.g. "count = 5", with key facts below for collections and geometry, then the
editor's own tooltip (the type).

Try:
- count, ratio, flag, name, pts, lookup, nothing -- simple types; many -- a long list;
- node, node.x -- an object of a class of this script, and a member;
- p, crv, crv.PointAtStart -- Rhino geometry;
- crv inside scaled() -- a local integer named like the global curve: it shows the
  integer there, and the curve everywhere else.
"""
import Rhino.Geometry as rg


class Node:
    def __init__(self):
        self.x = 3
        self.label = "n1"


count = 5
ratio = 2.5
flag = True
name = "abc"
pts = [1, 2, 3]
lookup = {"a": 1, "b": 2}
nothing = None
many = list(range(250))
node = Node()
p = rg.Point3d(1, 2, 3)
crv = rg.LineCurve(rg.Point3d(0, 0, 0), rg.Point3d(10, 0, 0))


def scaled(k):
    crv = k * count  # a local integer named like the global curve
    return crv       # <- breakpoint here


print(scaled(2))
print(crv.GetLength(), count, name, p)
