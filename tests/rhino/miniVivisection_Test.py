"""miniVivisection feature test -- open in Rhino 8's Script Editor.

Part A runs by itself (Run, F5 without breakpoints, or Debug): it checks that the
plug-in is loaded, shows its build and switches, and tests the drawing pipeline
(minivivisection_visualize, rhinoviv) with real Rhino geometry. The report goes to the
Terminal: one line per check, PASS or FAIL, and a summary.

Part B is a guided tour of the debugger features. Set the breakpoints marked
"<- BREAKPOINT A" ... "<- BREAKPOINT D" below (click left of the line numbers),
then Debug (F5). At each breakpoint, the STEP comments above it say what to try and
what you should see. Continue (F5) or step (F10) to the next one.

This file is copied next to miniVivisection.rhp by the build.
"""
import System
import Rhino
import Rhino.Geometry as rg

PLUGIN = "miniVivisection"

# --- Part A: automatic checks ----------------------------------------------------------

_results = []


def check(name, ok, detail=""):
    _results.append((name, bool(ok)))
    print("{0}  {1}{2}".format("PASS" if ok else "FAIL", name, "  -- " + str(detail) if detail and not ok else ""))


def plugin_checks():
    print("== The plug-in")
    pid = Rhino.PlugIns.PlugIn.IdFromName(PLUGIN)
    check("installed", pid != System.Guid.Empty, "not found in Rhino's plug-in list")
    plugin = Rhino.PlugIns.PlugIn.Find(pid) if pid != System.Guid.Empty else None
    check("loaded", plugin is not None, "load it once: run the command miniVivisection")
    build = None
    for assembly in System.AppDomain.CurrentDomain.GetAssemblies():
        if assembly.GetName().Name == PLUGIN:
            for attribute in assembly.GetCustomAttributes(False):
                if attribute.GetType().Name == "AssemblyMetadataAttribute" and attribute.Key == "BuildTime":
                    build = attribute.Value
    check("build time readable", build, "no BuildTime in the assembly")
    if build:
        print("      built " + build)
    if plugin is not None:
        settings = plugin.Settings
        switches = [
            ("PluginOn", "Debug switch"), ("AiOn", "AI switch"), ("Draw", "drawing"), ("HoverValues", "tooltip values"),
            ("TooltipActions", "tooltip links"), ("InlineValues", "values at line ends"),
            ("ShowChanges", "what changed"), ("ConditionalBreakpoints", "conditional breakpoints"), ("FreePause", "FreePause"),
        ]
        off = [label for key, label in switches if not settings.GetBool(key, True)]
        check("V°Debug switch on", settings.GetBool("PluginOn", True), "turn V°Debug ON in the Script Editor's top bar for Part B")
        print("      off: " + (", ".join(off) if off else "nothing"))


def pipeline_checks():
    print("== The drawing pipeline (python/minivivisection_visualize.py), with Rhino geometry")
    try:
        import minivivisection_visualize as wv
        import rhinoviv
    except ImportError as e:
        check("importable", False, "{0} -- the plug-in writes minivivisection.pth into Rhino's site-packages at load".format(e))
        return
    check("importable", True)

    curve = rg.LineCurve(rg.Point3d(0, 0, 0), rg.Point3d(10, 0, 0))
    check("candidates: curve", wv.candidates(curve)[:1] == ["geometry"], wv.candidates(curve))
    check("candidates: triple", wv.candidates([1, 2, 3])[:1] == ["point"], wv.candidates([1, 2, 3]))
    square = [[0, 0, 0], [5, 0, 0], [5, 5, 0], [0, 0, 0]]
    check("candidates: closed points", wv.candidates(square)[:1] == ["polyline_closed"], wv.candidates(square))

    how, out = wv.visualize(curve, "auto", rg)
    check("visualize: curve", len(out) == 1 and isinstance(out[0][1], rg.Curve), (how, out))
    box = rg.Brep.CreateFromBox(rg.BoundingBox(0, 0, 0, 2, 3, 4))
    how, out = wv.visualize(box, "auto", rg)
    check("visualize: Brep", len(out) == 1 and isinstance(out[0][1], rg.Brep), (how, out))
    mesh = rg.Mesh.CreateFromSphere(rg.Sphere(rg.Point3d(0, 0, 0), 2), 8, 6)
    how, out = wv.visualize(mesh, "auto", rg)
    check("visualize: mesh", len(out) == 1 and isinstance(out[0][1], rg.Mesh), (how, out))
    how, out = wv.visualize([1, 2, 3], "auto", rg)
    check("visualize: triple as point", len(out) == 1 and isinstance(out[0][1], rg.Point3d) and out[0][1].Z == 3, (how, out))
    how, out = wv.visualize(square[:3], "polyline", rg)
    check("visualize: polyline", len(out) == 1 and wv.describe(how) == "as polyline", (how, out))
    how, out = wv.visualize([curve, box], "auto", rg)
    check("visualize: a list item by item", len(out) == 2, (how, out))

    check("edit mode: curve", wv.edit_mode(curve) == "geometry", wv.edit_mode(curve))
    check("edit mode: points", wv.edit_mode([[0, 0, 0], [1, 0, 0]]) == "raw", wv.edit_mode([[0, 0, 0], [1, 0, 0]]))
    check("edit mode: number", wv.edit_mode(5).startswith("readonly"), wv.edit_mode(5))

    points = [[0, 0, 0], [1, 0, 0]]
    wv.write_back(points, "points", [rg.Point3d(5, 5, 5), rg.Point3d(6, 6, 6)])
    check("write back: points in place", points == [[5.0, 5.0, 5.0], [6.0, 6.0, 6.0]], points)

    # A visualizer and its inverse for a class of this script (rhinoviv).
    class Sensor(object):
        def __init__(self, x, y):
            self.x, self.y = x, y

    @rhinoviv.visualize(Sensor)
    def show_sensor(sensor):
        return rg.Point3d(sensor.x, sensor.y, 0)

    @rhinoviv.inverse(Sensor)
    def move_sensor(sensor, edited):
        sensor.x, sensor.y = edited.X, edited.Y

    sensor = Sensor(1, 2)
    how, out = wv.visualize(sensor, "auto", rg)
    check("visualizer: used", wv.describe(how) == "by show_sensor" and out and out[0][1].Y == 2, (how, out))
    check("visualizer: editable through its inverse", wv.edit_mode(sensor) == "inverse", wv.edit_mode(sensor))
    wv.write_back(sensor, "auto", [rg.Point3d(7, 8, 0)])
    check("visualizer: written back", (sensor.x, sensor.y) == (7, 8), (sensor.x, sensor.y))
    check("visualizer: listed", any("show_sensor" in line for line in wv.registered()), wv.registered())


def report():
    failed = [name for name, ok in _results if not ok]
    print("== {0} of {1} checks passed{2}".format(len(_results) - len(failed), len(_results), "" if not failed else ": FAILED " + ", ".join(failed)))


plugin_checks()
pipeline_checks()
report()


# --- Part B: the guided tour (Debug, with the breakpoints below) ----------------------------

class Node(object):
    def __init__(self, x, label):
        self.x = x
        self.label = label
        self.children = [x * 10, x * 20]


crv = rg.LineCurve(rg.Point3d(0, 0, 0), rg.Point3d(10, 0, 0))   # a global curve (see STEP 5)


def shadow(k):
    # STEP 5 -- the hover follows Python's scope.
    #   Hover crv on the next line: a local integer (crv = 2 * k). Hover the global
    #   crv at the top of Part B (crv = rg.LineCurve...): the curve.
    crv = 2 * k
    return crv   # <- BREAKPOINT C


def tour():
    count = 5
    ratio = 2.5
    name = "abc"
    pts = [[0, 0, 0], [5, 0, 0], [5, 5, 0], [0, 5, 0]]
    many = list(range(250))
    node = Node(3, "n1")
    box = rg.Box(rg.Plane.WorldXY, rg.Interval(20, 25), rg.Interval(0, 5), rg.Interval(0, 5))

    # STEP 1 -- values in the code (paused at BREAKPOINT A).
    #   Ends of lines: gray "count = 5", "ratio = 2.5", ... after the lines above.
    #   Tooltips: hover count, pts, many, node, box, crv -- the value as a line of code
    #   (pts = [[0,0,0],[5,0,0],...]), key facts in gray (4 items; 250 items;
    #   box size), then the editor's own tooltip below a line.
    #   Links in a tooltip: hover pts -> "Watch in Rhino" adds it to the V°Watch tab
    #   (drawn in Rhino); hover node -> "Tree" opens its members (x, label, children).
    #   Hover preview: hovering crv or box draws it in yellow in Rhino.
    # STEP 2 -- the V°Watch tab: type box and Enter: drawn in Rhino. The eye in
    #   Show hides it; right-click > Interpret As on pts: points / polyline / ...
    # STEP 3 -- the Python tab: show(pts), watch('node.children'), breakpoints().
    # STEP 4 -- switches and settings: top bar Debug OFF -> the plug-in's tabs, values
    #   and drawing go; ON -> back. Settings page (gear, left panel): turn
    #   "Values at the ends of lines" off and on -> the gray values go and come back.
    marker = 0   # <- BREAKPOINT A

    total = 0
    ring = []
    for i in range(10):
        total += i
        point = rg.Point3d(i, (i * i) * 0.1, 0)
        ring.append(point)
        # STEP 6 -- stepping (F10 inside this loop, from BREAKPOINT B).
        #   Changed values turn orange at the line ends (total, i, point);
        #   a tooltip over total says "was ..." (its value at the previous pause).
        # STEP 7 -- conditional breakpoint: right-click the BREAKPOINT B line >
        #   Breakpoint Condition... > i % 3 == 0 > Enter. F5 now stops at i = 0, 3, 6, 9
        #   only; the line shows "if i % 3 == 0" after it. Hit Count... > >= 2 pauses
        #   from the second time it holds.
        # STEP 8 -- logpoint: right-click the line "marker = i" below > Log Message... >
        #   i={i} total={total} > Enter. The Terminal prints a line per pass, no pause.
        marker = i   # <- BREAKPOINT B
    shadow(21)

    # STEP 9 -- editing in Rhino (paused at BREAKPOINT D).
    #   Hover box > "Edit in Rhino" (or the V°Watch tab's Auto edit ON): box becomes
    #   a Rhino object. Move it with the Gumball, release: no flicker, and after F5 the
    #   Terminal prints its new center below.
    #   Hover ring > "Bake": copies of its points in the document (layer
    #   Script Editor Watch::Baked).
    marker = -1   # <- BREAKPOINT D
    print("box center after editing: {0}".format(box.Center))
    print("total = {0}, ring has {1} points, shadow(21) = {2}".format(total, len(ring), shadow(21)))
    return total


tour()
print("Tour done.")
