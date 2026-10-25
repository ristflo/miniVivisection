"""What a watched value is drawn as -- MiniVivisection's drawing pipeline.

Runs in the paused script's Python (the plug-in calls it on the paused thread):

1. a visualizer registered with ``@rhinoviv.visualize`` for the value's type --
   its result goes through the pipeline again;
2. Rhino geometry and Rhino's geometric structs (``Point3d``, ``Line``, ``Plane``,
   ...) as they are, and Rhino object ids;
3. raw data -- numeric triples, lists of points, pairs, 6-number rows,
   triangles -- by an interpretation: the chosen one, or the first candidate
   ("auto");
4. containers item by item; anything else isn't drawn ("plain data").

The candidates of raw data are the main RhinoVivisection bridge's
(``bridge/debug_visualizer_bridge.py``: ``_detect_interpretations``), kept in line
by a parity check. The geometry module is passed in (``rg``): ``Rhino.Geometry`` in
Rhino, ``rhino3dm`` in the tests.
"""

AUTO = "auto"
PLAIN = "raw"

_STRUCT_NAMES = (
    "Point3d", "Point3f", "Vector3d", "Vector3f", "Plane", "BoundingBox", "Line",
    "Polyline", "Arc", "Circle", "Ellipse", "Rectangle3d", "Box", "Sphere", "Cylinder", "Cone", "Torus",
)
_POINT_TYPE_NAMES = ("Point3d", "Point3f")
_VECTOR_TYPE_NAMES = ("Vector3d", "Vector3f")

# What the interpretations are called in menus.
LABELS = {
    "geometry": "Geometry",
    "point": "Point",
    "vector": "Vector",
    "points": "Points",
    "vectors": "Vectors from the origin",
    "polyline": "Polyline",
    "polyline_closed": "Closed polyline",
    "curve": "Interpolated curve",
    "lines": "Lines",
    "anchored_vectors": "Anchored vectors",
    "triangles": "Triangles",
    "group": "Group (item by item)",
    "raw": "Plain data (not drawn)",
}


# --- candidates (the bridge's detection) ----------------------------------------------

def _is_numeric(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def _is_numeric_triple(obj):
    if isinstance(obj, (list, tuple)) and len(obj) == 3:
        return all(_is_numeric(v) for v in obj)
    return False


def _is_point_like(obj):
    if _is_numeric_triple(obj):
        return True
    if hasattr(obj, "X") and hasattr(obj, "Y") and hasattr(obj, "Z"):
        try:
            return _is_numeric(obj.X) and _is_numeric(obj.Y) and _is_numeric(obj.Z)
        except Exception:
            return False
    return False


def _xyz(obj):
    if hasattr(obj, "X") and hasattr(obj, "Y") and hasattr(obj, "Z"):
        return float(obj.X), float(obj.Y), float(obj.Z)
    return float(obj[0]), float(obj[1]), float(obj[2])


def _is_anchored_vector_pair(obj):
    return (
        isinstance(obj, (list, tuple))
        and len(obj) == 2
        and type(obj[0]).__name__ in _POINT_TYPE_NAMES
        and type(obj[1]).__name__ in _VECTOR_TYPE_NAMES
    )


def is_geometry(obj):
    """Rhino geometry, a geometric struct, or a Rhino object id: drawn as it is."""
    name = type(obj).__name__
    if name in _STRUCT_NAMES or name == "Guid":
        return True
    for base in type(obj).__mro__:
        if base.__name__ in ("GeometryBase", "CommonObject"):
            return True
    return False


def candidates(obj):
    """The interpretations of ``obj``, the default first (the bridge's order)."""
    if is_geometry(obj):
        return ["geometry", "raw"]
    if _is_numeric_triple(obj):
        return ["point", "vector", "raw"]
    if _is_anchored_vector_pair(obj):
        return ["anchored_vectors", "points", "raw"]
    if hasattr(obj, "__iter__") and not isinstance(obj, (str, bytes, bytearray, dict)):
        try:
            seq = list(obj) if not isinstance(obj, (list, tuple)) else obj
        except Exception:
            seq = None
        if seq is not None and len(seq) > 0:
            sample = seq[:50]
            if all(_is_point_like(x) for x in sample):
                if len(seq) >= 3:
                    if _xyz(seq[0]) == _xyz(seq[-1]):
                        return ["polyline_closed", "polyline", "points", "curve", "vectors", "raw"]
                    return ["points", "polyline", "polyline_closed", "curve", "vectors", "raw"]
                if len(seq) >= 2:
                    return ["points", "polyline", "curve", "vectors", "raw"]
                return ["points", "vectors", "raw"]
            if all(isinstance(x, (list, tuple)) and len(x) == 2 and _is_point_like(x[0]) and _is_point_like(x[1]) for x in sample):
                return ["lines", "anchored_vectors", "points", "raw"]
            if all(isinstance(x, (list, tuple)) and len(x) == 6 and all(_is_numeric(v) for v in x) for x in sample):
                return ["anchored_vectors", "lines", "points", "raw"]
            if all(isinstance(x, (list, tuple)) and len(x) == 3 and all(_is_point_like(v) for v in x) for x in sample):
                return ["triangles", "points", "raw"]
            return ["group", "raw"]
    if isinstance(obj, (dict, set, frozenset)):
        return ["group", "raw"] if len(obj) > 0 else ["raw"]
    return ["raw"]


# --- geometry for an interpretation ------------------------------------------------------

def _point(rg, p):
    return rg.Point3d(*_xyz(p))


def _arrow(rg, anchor, vector):
    ax, ay, az = anchor
    vx, vy, vz = vector
    return ("arrow", rg.Line(rg.Point3d(ax, ay, az), rg.Point3d(ax + vx, ay + vy, az + vz)))


def _polyline(rg, points):
    try:
        return rg.PolylineCurve(points)
    except Exception:
        poly = rg.Polyline()
        for p in points:
            poly.Add(p.X, p.Y, p.Z)
        return poly.ToPolylineCurve() if hasattr(poly, "ToPolylineCurve") else poly


def _interpolated(rg, points):
    curve_type = getattr(rg, "Curve", None)
    if curve_type is not None and hasattr(curve_type, "CreateInterpolatedCurve"):
        curve = rg.Curve.CreateInterpolatedCurve(points, 3)
        if curve is not None:
            return curve
    point_list = rg.Point3dList()
    for p in points:
        point_list.Add(p.X, p.Y, p.Z)
    return rg.NurbsCurve.Create(False, min(3, len(points) - 1), point_list)


def build(obj, interpretation, rg, limit=10000):
    """``(kind, value)`` pairs for ``obj`` by ``interpretation``: ``geometry`` (anything
    the plug-in draws) or ``arrow`` (a ``Line`` drawn as an arrow). None when it
    doesn't fit."""
    if interpretation == "raw":
        return []
    if interpretation == "geometry":
        return [("geometry", obj)]
    if interpretation == "point" and _is_point_like(obj):
        return [("geometry", _point(rg, obj))]
    if interpretation == "vector" and _is_point_like(obj):
        return [_arrow(rg, (0.0, 0.0, 0.0), _xyz(obj))]
    if not hasattr(obj, "__iter__") or isinstance(obj, (str, bytes, bytearray, dict)):
        return None
    items = list(obj)[:limit]
    if interpretation == "points":
        if _is_anchored_vector_pair(obj):
            return [("geometry", _point(rg, obj[0]))]
        return [("geometry", _point(rg, p)) for p in items if _is_point_like(p)] or [
            ("geometry", _point(rg, p)) for pair in items for p in pair if _is_point_like(p)]
    if interpretation == "vectors":
        return [_arrow(rg, (0.0, 0.0, 0.0), _xyz(p)) for p in items]
    if interpretation in ("polyline", "polyline_closed"):
        points = [_point(rg, p) for p in items]
        if interpretation == "polyline_closed" and len(points) >= 3 and _xyz(points[0]) != _xyz(points[-1]):
            points.append(_point(rg, points[0]))
        return [("geometry", _polyline(rg, points))] if len(points) >= 2 else None
    if interpretation == "curve":
        points = [_point(rg, p) for p in items]
        return [("geometry", _interpolated(rg, points))] if len(points) >= 2 else None
    if interpretation == "lines":
        result = []
        for pair in items:
            if len(pair) == 2:
                a, b = _xyz(pair[0]), _xyz(pair[1])
            elif len(pair) == 6:
                a, b = tuple(pair[:3]), (pair[0] + pair[3], pair[1] + pair[4], pair[2] + pair[5])
            else:
                continue
            result.append(("geometry", rg.Line(rg.Point3d(*a), rg.Point3d(*b))))
        return result
    if interpretation == "anchored_vectors":
        entries = [obj] if _is_anchored_vector_pair(obj) else items
        result = []
        for entry in entries:
            if len(entry) == 2:
                result.append(_arrow(rg, _xyz(entry[0]), _xyz(entry[1])))
            elif len(entry) == 6:
                result.append(_arrow(rg, tuple(map(float, entry[:3])), tuple(map(float, entry[3:]))))
        return result
    if interpretation == "triangles":
        result = []
        for triangle in items:
            if len(triangle) == 3:
                points = [_point(rg, p) for p in triangle]
                points.append(_point(rg, triangle[0]))
                result.append(("geometry", _polyline(rg, points)))
        return result
    return None


# --- the pipeline ------------------------------------------------------------------------------

def _visualizer(obj):
    try:
        import rhinoviv
    except ImportError:
        return None
    try:
        # Not `return rhinoviv.get_visualizer(obj)`: a return straight after a call into
        # another file confuses RhinoCode's "step over" in scripts calling this module
        # (the plug-in fixes that too, StepTracking).
        visualizer = rhinoviv.get_visualizer(obj)
        return visualizer
    except Exception:
        return None


def visualize(value, interpretation, rg, limit=10000, depth=5):
    """What ``value`` is drawn as: ``(how, [(kind, value), ...])``. ``how`` names the
    path -- ``visualizer <name>``, ``geometry``, an interpretation, ``group`` --
    for the Rhino Watch tab; a visualizer's error is raised."""
    out = []
    how = _visualize(value, interpretation or AUTO, rg, limit, depth, out, set())
    return how, out


def _visualize(value, interpretation, rg, limit, depth, out, seen):
    if value is None or len(out) >= limit or depth < 0:
        return "nothing"
    visualizer = _visualizer(value)
    if visualizer is not None:
        shown = visualizer(value)
        _visualize(shown, AUTO, rg, limit, depth - 1, out, seen)
        return "visualizer " + getattr(visualizer, "__name__", "?")
    options = candidates(value)
    chosen = options[0] if interpretation == AUTO or interpretation not in options else interpretation
    if chosen == "group":
        if id(value) in seen:
            return "group"
        seen.add(id(value))
        elements = value.values() if isinstance(value, dict) else value
        for element in elements:
            if len(out) >= limit:
                break
            _visualize(element, AUTO, rg, limit, depth - 1, out, seen)
        return "group"
    built = build(value, chosen, rg, limit - len(out))
    if built:
        out.extend(built)
    return chosen if built else "nothing"


def describe(how):
    """``how`` for the Rhino Watch tab: ``as polyline``, ``by show_sensors``."""
    if how.startswith("visualizer "):
        return "by " + how[len("visualizer "):]
    return "as " + LABELS.get(how, how).lower() if how not in ("geometry", "group", "nothing") else ""


# --- editing back ------------------------------------------------------------------------------

_RAW_EDITABLE = ("point", "points", "polyline", "polyline_closed")


def _chosen(value, interpretation):
    options = candidates(value)
    return options[0] if interpretation in (None, AUTO) or interpretation not in options else interpretation


def edit_mode(value, interpretation=AUTO):
    """How ``value`` can be edited as Rhino objects: ``inverse`` (a registered
    visualizer with an ``@rhinoviv.inverse``), ``raw`` (points of raw data, written
    into the list), ``geometry`` (Rhino geometry, written back item by item), or
    ``readonly`` with the reason after a colon."""
    visualizer = _visualizer(value)
    if visualizer is not None:
        try:
            import rhinoviv
            if rhinoviv.get_inverse(value) is not None:
                return "inverse"
        except ImportError:
            pass
        return "readonly:no @rhinoviv.inverse for " + type(value).__name__
    chosen = _chosen(value, interpretation)
    if chosen in ("geometry", "group"):
        return "geometry"
    if chosen in _RAW_EDITABLE:
        return "raw"
    return "readonly:" + LABELS.get(chosen, chosen).lower() + " can't be edited back"


def _points_of(geometry):
    """The points of an edited Point3d, point object, or polyline curve."""
    if hasattr(geometry, "PointCount") and hasattr(geometry, "Point"):
        return [geometry.Point(i) for i in range(geometry.PointCount)]
    if hasattr(geometry, "Location"):
        return [geometry.Location]
    return [geometry]


def _like(old, point):
    """``point`` in the shape of ``old``: a list stays the same list (changed in
    place), a tuple becomes a new tuple, anything else the point itself."""
    xyz = (point.X, point.Y, point.Z)
    if isinstance(old, list):
        old[:] = list(xyz)
        return old
    if isinstance(old, tuple):
        return tuple(xyz)
    return point


def write_back(value, interpretation, geometries):
    """Edited geometry back into ``value``: through its inverse, or into the raw data's
    points. Returns ``(how, replacement)``: the caller assigns a replacement to the
    watched expression; None means changed in place. Raises when it can't."""
    geometries = list(geometries)
    visualizer = _visualizer(value)
    if visualizer is not None:
        import rhinoviv
        entry = rhinoviv.get_inverse(value)
        if entry is None:
            raise ValueError("no @rhinoviv.inverse for " + type(value).__name__)
        func = entry[0] if isinstance(entry, tuple) else entry
        name = getattr(func, "__name__", "inverse")
        replacement = func(value, geometries[0] if len(geometries) == 1 else geometries)
        how = name + "() updated it in place" if replacement is None else "assigned what " + name + "() returned"
        return how, replacement
    chosen = _chosen(value, interpretation)
    if chosen not in _RAW_EDITABLE:
        raise ValueError(LABELS.get(chosen, chosen) + " can't be edited back")
    points = [p for g in geometries for p in _points_of(g)]
    if chosen == "point":
        new = _like(value, points[0])
        return "wrote the point", None if new is value else new
    if chosen == "polyline_closed" and len(points) == len(value) + 1:
        points = points[:-1]
    if len(points) != len(value):
        raise ValueError("the edit changed the number of points (%d, was %d)" % (len(points), len(value)))
    if isinstance(value, tuple):
        return "wrote %d points" % len(points), tuple(_like(old, p) for old, p in zip(value, points))
    for i, (old, p) in enumerate(zip(value, points)):
        new = _like(old, p)
        if new is not old:
            value[i] = new
    return "wrote %d points" % len(points), None


def load_visualizer_script(path, seed=None):
    """Runs a visualizer script, as the main bridge's load_visualizer_script does: in a
    module of its own (``rhinoviv.scripts.<stem>``), pre-filled with a copy of
    ``seed`` (the paused frame's names, so ``@rhinoviv.visualize(MyNode)`` sees the
    script's classes). Its own definitions are also set on ``rhinoviv``. Returns
    their names."""
    import os
    import re
    import sys
    import types
    stem = re.sub(r"[^0-9A-Za-z_]", "_", os.path.splitext(os.path.basename(path))[0])
    module = types.ModuleType("rhinoviv.scripts." + stem)
    seeded = {k: v for k, v in (seed or {}).items() if not k.startswith("__")}
    module.__dict__.update(seeded)
    module.__file__ = path
    sys.modules[module.__name__] = module
    with open(path, "r", encoding="utf-8") as f:
        code = compile(f.read(), path, "exec")
    exec(code, module.__dict__)
    missing = object()
    defined = sorted(k for k, v in module.__dict__.items()
                     if not k.startswith("_") and seeded.get(k, missing) is not v and not isinstance(v, types.ModuleType))
    try:
        import rhinoviv
        for name in defined:
            setattr(rhinoviv, name, module.__dict__[name])
    except ImportError:
        pass
    return defined


def registered():
    """The registered visualizers and inverses, as lines (the Python tab's visualizers())."""
    try:
        import rhinoviv
    except ImportError:
        return ["rhinoviv isn't importable"]

    def name(t):
        return t if isinstance(t, str) else getattr(t, "__name__", repr(t))
    lines = ["visualize %s -> %s" % (name(t), getattr(f, "__name__", "?")) for t, f in getattr(rhinoviv, "_visualizers", {}).items()]
    lines += ["inverse   %s -> %s" % (name(t), getattr(e[0], "__name__", "?")) for t, e in getattr(rhinoviv, "_inverses", {}).items()]
    return lines or ["none registered (methods marked with a bare @rhinoviv.visualize are found on their class)"]
