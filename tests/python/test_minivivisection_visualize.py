"""Tests for minivivisection_visualize, outside Rhino: rhino3dm as the geometry module.

Run from the repository root:  python -m pytest tests/python
"""
import os
import sys
import types

import pytest
import rhino3dm as rg

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "src", "miniVivisection", "python"))  # the pipeline, and rhinoviv

import minivivisection_visualize as wv  # noqa: E402

# Raw data and its interpretations, best first -- as RhinoVivisection's bridge
# (debug_visualizer_bridge._detect_interpretations) finds them, so both projects offer the same.
RAW_SAMPLES = [
    ([1, 2, 3], ["point", "vector", "raw"]),
    ((1.0, 2.0, 3.5), ["point", "vector", "raw"]),
    ([[0, 0, 0], [1, 0, 0], [1, 1, 0]], ["points", "polyline", "polyline_closed", "curve", "vectors", "raw"]),
    ([[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 0, 0]], ["polyline_closed", "polyline", "points", "curve", "vectors", "raw"]),
    ([[0, 0, 0], [1, 0, 0]], ["points", "polyline", "curve", "vectors", "raw"]),
    ([[0, 0, 0]], ["points", "vectors", "raw"]),
    ([([0, 0, 0], [1, 1, 1]), ([1, 0, 0], [2, 2, 2])], ["lines", "anchored_vectors", "points", "raw"]),
    ([[0, 0, 0, 1, 0, 0], [1, 1, 1, 0, 0, 1]], ["anchored_vectors", "lines", "points", "raw"]),
    ([[[0, 0, 0], [1, 0, 0], [0, 1, 0]]], ["triangles", "points", "raw"]),
    ([1, "a", None], ["group", "raw"]),
    ({"a": [1, 2, 3]}, ["group", "raw"]),
    ({}, ["raw"]),
    ("text", ["raw"]),
    (42, ["raw"]),
]


@pytest.mark.parametrize("value,expected", RAW_SAMPLES, ids=[repr(v)[:30] for v, _ in RAW_SAMPLES])
def test_candidates_match_the_bridge(value, expected):
    assert wv.candidates(value) == expected


def test_geometry_is_drawn_as_it_is():
    point = rg.Point3d(1, 2, 3)
    assert wv.candidates(point)[0] == "geometry"
    how, items = wv.visualize(point, wv.AUTO, rg)
    assert how == "geometry" and items == [("geometry", point)]


def test_triple_auto_is_a_point_and_vector_is_an_arrow():
    how, items = wv.visualize([1, 2, 3], wv.AUTO, rg)
    assert how == "point" and items[0][0] == "geometry" and (items[0][1].X, items[0][1].Y, items[0][1].Z) == (1, 2, 3)
    how, items = wv.visualize([1, 2, 3], "vector", rg)
    assert how == "vector" and items[0][0] == "arrow"
    line = items[0][1]
    assert (line.From.X, line.To.X, line.To.Z) == (0, 1, 3)


def test_points_polyline_closed_and_curve():
    square = [[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]]
    how, items = wv.visualize(square, wv.AUTO, rg)
    assert how == "points" and len(items) == 4
    how, items = wv.visualize(square, "polyline_closed", rg)
    assert how == "polyline_closed" and len(items) == 1 and items[0][1].PointCount == 5
    how, items = wv.visualize(square, "curve", rg)
    assert how == "curve" and len(items) == 1


def test_pairs_rows_and_triangles():
    how, items = wv.visualize([([0, 0, 0], [1, 1, 1])], wv.AUTO, rg)
    assert how == "lines" and len(items) == 1
    how, items = wv.visualize([[0, 0, 0, 0, 0, 5]], wv.AUTO, rg)
    assert how == "anchored_vectors" and items[0][0] == "arrow" and items[0][1].To.Z == 5
    how, items = wv.visualize([[[0, 0, 0], [1, 0, 0], [0, 1, 0]]], wv.AUTO, rg)
    assert how == "triangles" and items[0][1].PointCount == 4


def test_containers_item_by_item_and_plain_data():
    how, items = wv.visualize({"a": rg.Point3d(1, 0, 0), "b": [[0, 0, 0], [1, 1, 1]], "c": "text"}, wv.AUTO, rg)
    assert how == "group" and len(items) == 3  # a point, and the pair's two points
    assert wv.visualize("text", wv.AUTO, rg) == ("nothing", [])
    assert wv.visualize([1, 2, 3], "raw", rg) == ("nothing", [])


def test_a_choice_that_doesnt_fit_falls_back_to_auto():
    how, _ = wv.visualize([1, 2, 3], "polyline", rg)
    assert how == "point"


def test_limit_and_cycles():
    many = [[i, 0, 0] for i in range(100)]
    _, items = wv.visualize(many, "points", rg, limit=10)
    assert len(items) == 10
    loop = []
    loop.append(loop)
    assert wv.visualize(loop, wv.AUTO, rg)[1] == []


def test_registered_visualizer(monkeypatch):
    class Sensor:
        def __init__(self, x):
            self.x = x

    fake = types.ModuleType("rhinoviv")

    def show_sensor(sensor):
        return [sensor.x, 0, 0]

    fake.get_visualizer = lambda obj: show_sensor if isinstance(obj, Sensor) else None
    monkeypatch.setitem(sys.modules, "rhinoviv", fake)
    how, items = wv.visualize([Sensor(1), Sensor(2)], wv.AUTO, rg)
    assert how == "group" and len(items) == 2
    how, items = wv.visualize(Sensor(3), wv.AUTO, rg)
    assert how == "visualizer show_sensor" and items[0][1].X == 3
    assert wv.describe(how) == "by show_sensor"
    assert wv.describe("polyline_closed") == "as closed polyline"


def test_edit_modes():
    assert wv.edit_mode(rg.Point3d(0, 0, 0)) == "geometry"
    assert wv.edit_mode([[0, 0, 0], [1, 0, 0]]) == "raw"
    assert wv.edit_mode([[0, 0, 0], [1, 0, 0]], "curve").startswith("readonly:")
    assert wv.edit_mode({"a": 1}) == "geometry"  # a group: item by item


def test_write_back_raw_points_in_place():
    data = [[0, 0, 0], (1, 0, 0)]
    keep = data[0]
    how, replacement = wv.write_back(data, "points", [rg.Point3d(5, 5, 5), rg.Point3d(6, 6, 6)])
    assert replacement is None and data[0] is keep and data[0] == [5, 5, 5] and data[1] == (6, 6, 6)
    with pytest.raises(ValueError):
        wv.write_back(data, "points", [rg.Point3d(1, 1, 1)])


def test_write_back_closed_polyline_and_tuple():
    square = ((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0))
    _, items = wv.visualize(square, "polyline_closed", rg)
    how, replacement = wv.write_back(square, "polyline_closed", [items[0][1]])
    assert replacement == square  # the same points back, as a tuple of tuples


def test_write_back_a_single_point():
    point = [1, 2, 3]
    how, replacement = wv.write_back(point, "point", [rg.Point3d(4, 5, 6)])
    assert replacement is None and point == [4, 5, 6]
    how, replacement = wv.write_back((1, 2, 3), "point", [rg.Point3d(4, 5, 6)])
    assert replacement == (4, 5, 6)


def test_write_back_through_an_inverse(monkeypatch):
    class Node:
        def __init__(self):
            self.x = 0

    def show(node):
        return rg.Point3d(node.x, 0, 0)

    def back(node, point):
        node.x = point.X

    fake = types.ModuleType("rhinoviv")
    fake.get_visualizer = lambda obj: show if isinstance(obj, Node) else None
    fake.get_inverse = lambda obj: (back, "full") if isinstance(obj, Node) else None
    monkeypatch.setitem(sys.modules, "rhinoviv", fake)
    node = Node()
    assert wv.edit_mode(node) == "inverse"
    how, replacement = wv.write_back(node, wv.AUTO, [rg.Point3d(7, 0, 0)])
    assert node.x == 7 and replacement is None and how == "back() updated it in place"


def test_load_visualizer_script(tmp_path, monkeypatch):
    fake = types.ModuleType("rhinoviv")
    monkeypatch.setitem(sys.modules, "rhinoviv", fake)
    script = tmp_path / "my_vis.py"
    script.write_text("def show_node(n):\n    return Base\n", encoding="utf-8")
    defined = wv.load_visualizer_script(str(script), {"Base": 1, "__name__": "x"})
    assert defined == ["show_node"] and fake.show_node(None) == 1
