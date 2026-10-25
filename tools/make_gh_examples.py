#! python 3
"""Makes miniVivisection's Grasshopper lessons: examples/grasshopper/gh*.gh (and GH 10's
script file, gh10_spiral.py, next to its definition), and gh0_overview.gh: the overview of all
examples (tools/examples_overview.py) in a Panel, links to every lesson, with V Protect.

Runs inside Rhino 8 (Grasshopper and miniVivisection.GH.gha loaded): it builds each
definition with Grasshopper's own API -- parameters with their data, Python 3 script
components, and the lesson's text in a Panel set to Show as Markdown -- and saves it. The
lesson texts are Markdown with headings "The bug", "Try this" and "Without miniVivisection";
every list item stays on one line (the plug-in's own Markdown parser reads them that way).
Run it again after changing a lesson here; the live test t_ghexamples runs it and checks
every definition.

    make(folder)    # returns the paths written
"""
import os

import System
import Rhino
import Rhino.Geometry as rg

import clr
clr.AddReference('Grasshopper')
import Grasshopper
from Grasshopper.Kernel import GH_Document, GH_DocumentIO, GH_ParamAccess, GH_ParameterSide
from Grasshopper.Kernel.Data import GH_Path
from Grasshopper.Kernel.Parameters import Param_Curve, Param_Integer, Param_Number, Param_Point
from Grasshopper.Kernel.Special import GH_BooleanToggle, GH_NumberSlider, GH_Panel
from Grasshopper.Kernel.Types import GH_Curve, GH_Integer, GH_Number, GH_Point

import examples_overview  # tools/, next to this file

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FOLDER = os.path.join(HERE, 'examples', 'grasshopper')


def _assembly(name):
    for a in System.AppDomain.CurrentDomain.GetAssemblies():
        if a.GetName().Name == name:
            return a
    raise RuntimeError(name + ' is not loaded (open Grasshopper first)')


def _place(obj, x, y):
    obj.CreateAttributes()
    obj.Attributes.Pivot = System.Drawing.PointF(x, y)
    obj.Attributes.ExpireLayout()
    return obj


def script(title, nickname, source, inputs, outputs):
    """A Python 3 script component: inputs [(name, access, type hint)], outputs [name]."""
    py3 = _assembly('RhinoCodePluginGH').GetType('RhinoCodePluginGH.Components.Python3Component')
    create = [m for m in py3.GetMethods() if m.Name == 'Create' and len(m.GetParameters()) == 4
              and m.GetParameters()[1].ParameterType.FullName == 'System.String'][0]
    component = create.Invoke(None, [title, source, None, True])  # no default x, y, a
    component.NickName = nickname
    for name, access, hint in inputs:
        param = component.CreateParameter(GH_ParameterSide.Input, component.Params.Input.Count)
        param.Name = param.NickName = name
        param.Access = access
        names = [h.TypeName for h in param.TypeHints]
        if hint not in names:
            raise RuntimeError('no type hint %r; there are: %s' % (hint, ', '.join(names)))
        param.TypeHints.Select(hint)  # as the input's menu > Type hint does
        component.Params.RegisterInputParam(param)
    for name in outputs:
        param = component.CreateParameter(GH_ParameterSide.Output, component.Params.Output.Count)
        param.Name = param.NickName = name
        component.Params.RegisterOutputParam(param)
    component.Params.OnParametersChanged()
    component.VariableParameterMaintenance()
    return component


def panel(text, x, y, width=430, height=330):
    p = GH_Panel()
    p.UserText = text
    p.NickName = 'Lesson'
    _place(p, x, y)
    p.Attributes.Bounds = System.Drawing.RectangleF(x, y, width, height)
    return p


def persistent(param, name, tree):
    """tree: {(path...): [values]}"""
    param.Name = param.NickName = name
    for path, values in tree.items():
        for value in values:
            param.PersistentData.Append(value, GH_Path(System.Array[int](list(path))))
    return param


PROTECT = System.Guid('4f3b9c2e-7a61-4d58-9e0b-2c8d61f5a7b3')  # V Protect (miniVivisection.GH's ProtectComponent)

GAP = 40  # from the lesson Panel's right edge to the nearest object right of it (where its input wires come in)


def _layout(obj):
    obj.Attributes.ExpireLayout()
    obj.Attributes.PerformLayout()
    return obj.Attributes.Bounds


def _move(obj, dx, dy=0.0):
    """obj moved by dx, dy -- a Panel keeps its size (its bounds are set, not laid out)."""
    bounds = _layout(obj)
    pivot = obj.Attributes.Pivot
    obj.Attributes.Pivot = System.Drawing.PointF(pivot.X + dx, pivot.Y + dy)
    if isinstance(obj, GH_Panel):
        obj.Attributes.Bounds = System.Drawing.RectangleF(bounds.X + dx, bounds.Y + dy, bounds.Width, bounds.Height)
    _layout(obj)


def _clear_of_lesson(doc):
    """Everything right of the lesson Panel moved together, so the nearest of it starts GAP right of
    the Panel: the same gap in every lesson, whatever a component's inputs make its width."""
    lesson = [o for o in doc.Objects if o.NickName == 'Lesson']
    others = [o for o in doc.Objects if o.NickName != 'Lesson']
    if not lesson or not others:
        return
    right = _layout(lesson[0]).Right
    dx = right + GAP - min(_layout(o).Left for o in others)
    for o in others:
        _move(o, dx)


def _protect(doc, path, under=None):
    """A V Protect under the lesson's Panel (or under the object `under`), protecting the lesson with
    its original in itself: Revert puts the lesson back as it was shipped (also in the user's copy,
    wherever it is)."""
    protect = Grasshopper.Instances.ComponentServer.EmitObject(PROTECT)
    if protect is None:
        raise RuntimeError('V Protect is missing (miniVivisection.GH)')
    if under is None:
        lesson = [o for o in doc.Objects if o.NickName == 'Lesson']
        under = lesson[0] if lesson else None
    above = _layout(under) if under is not None else System.Drawing.RectangleF(20, 0, 0, 0)
    _place(protect, above.Left, above.Bottom + 30)  # protected, its pivot is its top left (ProtectAttributes.Layout)
    doc.AddObject(protect, False)
    flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
    kind = protect.GetType()
    kind.GetMethod('SetForTests', flags).Invoke(protect, [System.Array[str]([]), None, True])
    kind.GetMethod('Protect', flags).Invoke(protect, None)  # saves the lesson twice: without, then with its original
    if not kind.GetProperty('IsProtected', flags).GetValue(protect):
        raise RuntimeError('V Protect could not protect ' + path)


def save(doc, path, protect_under=None):
    """The lesson laid out (GAP right of its Panel), saved, and protected."""
    _clear_of_lesson(doc)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    doc.FilePath = path  # SaveQuiet doesn't set it; V Protect needs the lesson's file
    if not GH_DocumentIO(doc).SaveQuiet(path):
        raise RuntimeError('could not save ' + path)
    _protect(doc, path, protect_under)
    return path


# --- the lessons ------------------------------------------------------------------------------

GH1_TEXT = """# GH 1 -- Which branch failed: the failing runs, named

## The bug

[Divide](gh:Divide) splits every curve into *n* segments. It turns red for some curves -- but [Curves](gh:Curves) holds 12 curves in 4 branches: which ones fail?

## Try this

1. Look at Divide's balloon (hover [Divide](gh:Divide)): miniVivisection names the failing runs -- `{0;2}[0]`, `{0;2}[1]`, `{0;2}[2]`.
2. *Right-click Divide > V° Copy failing branches (1)*. Paste it into a Panel: `{0;2}`, ready for Tree Branch or V°Edit's *Index*. *Copy failing items (3)* copies the three items.
3. *Right-click Divide > V° Debug failing run 7 -- data `{0;2}[0]`, line 4*. Its editor opens, and run 7 pauses at line 4 -- that run only (the breakpoint's condition: `gh_item == "{0;2}[0]"`). Hover `n`: 0.
4. Fix it: [Counts](gh:Counts) has 0 for branch `{0;2}` -- *Right-click Counts > Manage Integer collection* and make it 5. Or guard the script: `if n < 1: ...`

## Without miniVivisection

Grasshopper says *Solution exception: 'NoneType' object is not iterable* -- not which of the 12 runs failed, nor with which data.
"""

GH1_SOURCE = '''#! python 3
"""Divide: the points that split crv into n segments."""
ts = crv.DivideByCount(n, True)
pts = [crv.PointAt(t) for t in ts]
'''


def gh1(folder):
    doc = GH_Document()
    curves = {}
    for b in range(4):
        curves[(0, b)] = [GH_Curve(rg.LineCurve(rg.Point3d(b * 12, i * 6, 0), rg.Point3d(b * 12 + 10, i * 6, 0))) for i in range(3)]
    crv = _place(persistent(Param_Curve(), 'Curves', curves), 480, 60)
    counts = _place(persistent(Param_Integer(), 'Counts', {(0, 0): [GH_Integer(5)], (0, 1): [GH_Integer(5)], (0, 2): [GH_Integer(0)], (0, 3): [GH_Integer(5)]}), 480, 130)
    divide = _place(script('Divide', 'Divide', GH1_SOURCE, [('crv', GH_ParamAccess.item, 'Curve'), ('n', GH_ParamAccess.item, 'int')], ['pts']), 620, 90)
    lesson = panel(GH1_TEXT + nav(1), 20, 20, height=500)
    for obj in (lesson, crv, counts, divide):
        doc.AddObject(obj, False)
    divide.Params.Input[0].AddSource(crv)
    divide.Params.Input[1].AddSource(counts)
    _show_as(lesson, 'Markdown')
    return save(doc, os.path.join(folder, 'gh1_which_branch_failed.gh'))


GH2_TEXT = """# GH 2 -- The one wrong item: a breakpoint condition, the run badge

## The bug

[Posts](gh:Posts) stands a post on each of 10 [Points](gh:Points), as high as [Heights](gh:Heights) says, times [Scale](gh:Scale). One post points down into the ground -- which one, and why?

## Try this

1. Click the V° button on Grasshopper's toolbar (on the right) until it shows *Always debug* (V° with the Debug icon).
2. Double-click [Posts](gh:Posts): its editor opens. Click left of line 4 for a breakpoint, then *right-click the line > Breakpoint Condition…* and type `h < 0`.
3. Move the [Scale](gh:Scale) slider: Grasshopper recomputes, and the run pauses only where `h < 0`. The badge at the top of the code names the run: `'Posts' run 7 · {0}[6] · …`. Hover `h`: -3.0.
4. While paused, move [Scale](gh:Scale) again: no *expired during a solution* dialog -- miniVivisection holds the change back and recomputes when you continue (F5 in the editor).
5. Fix it: one height was entered negative. Use `abs(h)` in the script, or fix [Heights](gh:Heights).
6. Click the V° button until it shows *Stop and record* again (the default).

## Without miniVivisection

You'd print `h` for all 10 runs and look for the odd one; and moving a slider while paused would stop Grasshopper with a breakpoint dialog.
"""

GH2_SOURCE = '''#! python 3
"""Posts: a vertical line on pt, h * s high."""
import Rhino.Geometry as rg
top = pt + rg.Vector3d(0, 0, h * s)
post = rg.Line(pt, top)
'''


def gh2(folder):
    doc = GH_Document()
    pts = _place(persistent(Param_Point(), 'Points', {(0,): [GH_Point(rg.Point3d(i * 4, 0, 0)) for i in range(10)]}), 480, 60)
    heights = [3.0, 4.0, 5.0, 4.5, 3.5, 4.0, -3.0, 5.5, 4.0, 3.0]  # one entered with the wrong sign
    h = _place(persistent(Param_Number(), 'Heights', {(0,): [GH_Number(v) for v in heights]}), 480, 130)
    scale = GH_NumberSlider()
    scale.CreateAttributes()
    scale.Slider.Minimum = System.Decimal(0.5)
    scale.Slider.Maximum = System.Decimal(3)
    scale.Slider.DecimalPlaces = 1
    scale.SetSliderValue(System.Decimal(1))
    scale.NickName = 'Scale'
    _place(scale, 470, 200)
    posts = _place(script('Posts', 'Posts', GH2_SOURCE, [('pt', GH_ParamAccess.item, 'Point3d'), ('h', GH_ParamAccess.item, 'double'), ('s', GH_ParamAccess.item, 'double')], ['post']), 640, 110)
    lesson = panel(GH2_TEXT + nav(2), 20, 20, height=580)
    for obj in (lesson, pts, h, scale, posts):
        doc.AddObject(obj, False)
    posts.Params.Input[0].AddSource(pts)
    posts.Params.Input[1].AddSource(h)
    posts.Params.Input[2].AddSource(scale)
    _show_as(lesson, 'Markdown')
    return save(doc, os.path.join(folder, 'gh2_the_one_wrong_item.gh'))


GH3_TEXT = """# GH 3 -- Stop a component that hangs: Ctrl+Alt+B

## The bug

[Halve](gh:Halve) counts how many steps of 2 take each of [Values](gh:Values) down to 0. Set [Run](gh:Run) to True, and Grasshopper -- and Rhino -- hang: one value never gets there.

## Try this

1. Keep Halve's editor closed, and the V° button on Grasshopper's toolbar on *Stop and record* (the default: V° with a green Run; a gray V° is *Normal*).
2. Set [Run](gh:Run) to True: Rhino hangs.
3. Press **Ctrl+Alt+B**: Halve is stopped (the rest of the solution too), and its editor opens at the line where it was, with an explanation: run 3, `{0}[2]`, and the values then -- `x` went below 0 and on.
4. Fix it: `while x > 0:`. Or press *Debug* in the editor to run it again in the debugger -- then Ctrl+Alt+B pauses it, and you can step.
5. On *Always debug*, Halve gets its editor out of sight: Ctrl+Alt+B then pauses it at once, and its editor comes up at the line. On *Normal*, Ctrl+Alt+B leaves it alone.
6. Set [Run](gh:Run) to False before you save, so the file doesn't hang when it opens.

## Without miniVivisection

Rhino is frozen: you wait, or end Rhino in the Task Manager -- and lose what wasn't saved.
"""

GH3_SOURCE = '''#! python 3
"""Halve: how many steps of 2 take x down to 0."""
steps = 0
if run:
    while x != 0:       # never true for an odd x
        x -= 2
        steps += 1
'''


def gh3(folder):
    doc = GH_Document()
    run = GH_BooleanToggle()
    run.CreateAttributes()
    run.Value = False
    run.NickName = 'Run'
    _place(run, 470, 60)
    values = _place(persistent(Param_Integer(), 'Values', {(0,): [GH_Integer(v) for v in (4, 6, 7, 8)]}), 480, 120)
    halve = _place(script('Halve', 'Halve', GH3_SOURCE, [('run', GH_ParamAccess.item, 'bool'), ('x', GH_ParamAccess.item, 'int')], ['steps']), 640, 80)
    lesson = panel(GH3_TEXT + nav(3), 20, 20, height=560)
    for obj in (lesson, run, values, halve):
        doc.AddObject(obj, False)
    halve.Params.Input[0].AddSource(run)
    halve.Params.Input[1].AddSource(values)
    _show_as(lesson, 'Markdown')
    return save(doc, os.path.join(folder, 'gh3_stop_a_hang.gh'))


GH4_TEXT = """# GH 4 -- Edit Grasshopper geometry in Rhino: V°Edit

## The bug

Three [Profiles](gh:Profiles) are lofted into a surface by [Loft](gh:Loft). The middle profile dips: the surface has a dent. Its points came from a measurement -- fixing them by numbers is slow.

## Try this

1. [V°Edit](gh:V°Edit), between Profiles and Loft, shows profile 1 in Rhino as a blue edit object ([Index](gh:Index): 1).
2. In Rhino, select it, turn on its control points (F10), and drag the dipping points up -- the Gumball, Move, anything that changes the curve.
3. The loft follows at once. The object turns red-orange, V°Edit says *1 edited*, and its *Edited* output is True for profile 1.
4. Save the definition: the edit is kept in the .gh file, not in the Rhino model. *Reset sel.* (with the object selected in Rhino) or *Reset all* takes it back.

## Without miniVivisection

You'd bake the curve, edit it, reference it back with a Curve parameter, and keep the two in step by hand.
"""


def _profile(y, dip):
    points = [rg.Point3d(x, y, 4 + (-dip if x == 10 else 0) + (0.5 if x in (5, 15) else 0)) for x in range(0, 21, 5)]
    return rg.Curve.CreateInterpolatedCurve(points, 3)


def gh4(folder):
    doc = GH_Document()
    profiles = _place(persistent(Param_Curve(), 'Profiles', {(0,): [GH_Curve(_profile(0, 0)), GH_Curve(_profile(10, 3.5)), GH_Curve(_profile(20, 0))]}), 495, 60)
    index = _place(persistent(Param_Integer(), 'Index', {(0,): [GH_Integer(1)]}), 495, 130)
    server = Grasshopper.Instances.ComponentServer
    vedit = server.EmitObjectProxy(System.Guid('b7d2e8a4-3c1f-4e6b-a9d0-5f2c7e1b8d63')).CreateInstance()  # V°Edit, by its id (its name has the chosen mark)
    vedit.NickName = 'V\u00b0Edit'  # the lesson's link gh:V°Edit finds it, whatever mark this Rhino uses
    _place(vedit, 620, 90)
    loft = server.FindObjectByName('Loft', True, True).CreateInstance()
    _place(loft, 780, 90)
    lesson = panel(GH4_TEXT + nav(4), 20, 20, height=480)
    for obj in (lesson, profiles, index, vedit, loft):
        doc.AddObject(obj, False)
    vedit.Params.Input[0].AddSource(profiles)
    vedit.Params.Input[1].AddSource(index)
    loft.Params.Input[0].AddSource(vedit.Params.Output[0])
    _show_as(lesson, 'Markdown')
    return save(doc, os.path.join(folder, 'gh4_v_edit.gh'))


GH5_TEXT = """# GH 5 -- A runtime error: the debugger stops where it happens

## The bug

[Spacing](gh:Spacing) compares each step of a path with the step before: 1.0 means as long, 2.0 twice as long. It turns red -- *float division by zero* -- but it loops over 12 [Points](gh:Points) in one run: at which step?

## Try this

1. Click the V° button on Grasshopper's toolbar until it shows *Always debug* (V° with the Debug icon).
2. Double-click [Spacing](gh:Spacing): its editor opens. Recompute: F5 on the canvas (*Solution > Recompute*).
3. The debugger pauses at the error itself: line 6, in `ratio()`. Hover `before`: a line 0 long.
4. In the editor's *Call Stack* tab, click the caller's frame: `ratio()` was called from the loop, line 13. Hover `i`: 8. The step before it, from point 6 to point 7, is 0 long -- those two points are the same.
5. Fix it: compare only with a step that has a length -- `if before is not None and before.Length > 0:` at line 12 -- or remove the duplicate point.
6. Click the V° button until it shows *Stop and record* again (the default).

## Without miniVivisection

Grasshopper gives the message, not the step: you'd print every step, run again, and read the list.
"""

GH5_SOURCE = '''#! python 3
"""Spacing: each step's length compared with the step before."""
import Rhino.Geometry as rg

def ratio(step, before):
    return step.Length / before.Length

ratios = []
before = None
for i in range(1, len(pts)):
    step = rg.Line(pts[i - 1], pts[i])
    if before is not None:
        ratios.append(ratio(step, before))
    before = step
'''


def gh5(folder):
    doc = GH_Document()
    points = [rg.Point3d(k * 5 + (k % 3), (k % 2) * 3, 0) for k in range(12)]
    points[7] = points[6]   # measured twice: a step of length 0
    pts = _place(persistent(Param_Point(), 'Points', {(0,): [GH_Point(p) for p in points]}), 480, 60)
    spacing = _place(script('Spacing', 'Spacing', GH5_SOURCE, [('pts', GH_ParamAccess.list, 'Point3d')], ['ratios']), 640, 70)
    lesson = panel(GH5_TEXT + nav(5), 20, 20, height=560)
    for obj in (lesson, pts, spacing):
        doc.AddObject(obj, False)
    spacing.Params.Input[0].AddSource(pts)
    _show_as(lesson, 'Markdown')
    return save(doc, os.path.join(folder, 'gh5_runtime_error.gh'))


GH6_TEXT = """# GH 6 -- Threads in a component: results in the wrong order

## The bug

[Measure](gh:Measure) finds each curve's length on a thread of its own (6 threads, so the slow ones don't wait for each other) and tags each curve with its length. No error -- but the tags are wrong: the first curve, 9 long, says 1.5; the shortest, 1.5 long, says 12.0.

## Try this

1. Click the V° button on Grasshopper's toolbar until it shows *Always debug* (V° with the Debug icon). Double-click [Measure](gh:Measure): its editor opens.
2. Set a breakpoint on line 19 (`tags = ...`), after the threads are joined, and recompute (F5 on the canvas).
3. Paused: hover `lengths` -- the numbers are sorted, not in the curves' order. Each thread appended its result when it finished, and short curves finish first.
4. *V°Threads*: the six `measure-N` threads have ended; only the component's own thread is there, paused.
5. Set a breakpoint inside `measure()` (line 12, `lengths.append`) and recompute: it doesn't pause. Grasshopper runs the component on Rhino's UI thread, which waits in `join()`; a pause in a worker would freeze Rhino, so miniVivisection lets the worker run on (the freeze guard) and says so in *V°Log*.
6. Add `# async:true` as line 2 (the breakpoint moves to line 13) and recompute: the debug run goes to a thread of its own, and the breakpoint pauses in `measure-4`, `measure-1`, ... in the order they finish. *V°Threads* shows them; hover `length` in each; Continue (F5 in the editor) for the next.
7. Fix it: one place per curve -- `lengths = [None] * len(curves)` before the threads, and `lengths[i] = length` in `measure()`.
8. Click the V° button until it shows *Stop and record* again (the default).

## Without miniVivisection

Nothing is red. You'd compare tags with curves one by one, add prints from six threads at once, and read them in whatever order they come.
"""

GH6_SOURCE = '''#! python 3
"""Measure: every curve's length, on a thread each; a tag at its middle."""
import threading
import time
import Rhino.Geometry as rg

lengths = []

def measure(i, crv):
    length = crv.GetLength()
    time.sleep(0.05 * length)       # measuring takes as long as the curve is long
    lengths.append(length)          # when it's done

threads = [threading.Thread(target=measure, args=(i, c), name="measure-%d" % i) for i, c in enumerate(curves)]
for t in threads:
    t.start()
for t in threads:
    t.join()
tags = [rg.TextDot("%.1f" % length, c.PointAtNormalizedLength(0.5)) for c, length in zip(curves, lengths)]
'''


def gh6(folder):
    doc = GH_Document()
    sizes = [9.0, 3.0, 12.0, 6.0, 1.5, 15.0]
    curves = [GH_Curve(rg.LineCurve(rg.Point3d(i * 4, 0, 0), rg.Point3d(i * 4, size, 0))) for i, size in enumerate(sizes)]
    crv = _place(persistent(Param_Curve(), 'Curves', {(0,): curves}), 480, 60)
    measure = _place(script('Measure', 'Measure', GH6_SOURCE, [('curves', GH_ParamAccess.list, 'Curve')], ['tags', 'lengths']), 640, 70)
    lesson = panel(GH6_TEXT + nav(6), 20, 20, height=740)
    for obj in (lesson, crv, measure):
        doc.AddObject(obj, False)
    measure.Params.Input[0].AddSource(crv)
    _show_as(lesson, 'Markdown')
    return save(doc, os.path.join(folder, 'gh6_threads_in_a_component.gh'))


GH7_TEXT = """# GH 7 -- ghpythonlib.parallel: a flatten that doesn't flatten

## The bug

[Rings](gh:Rings) finds where each of the [Curves](gh:Curves) crosses the plane at height [h](gh:h) -- in parallel, with `ghpythonlib.parallel.run` -- and tags each crossing with its curve's number. It asks `parallel.run` to flatten the results into one list of points (`True`, the third argument), and fails at line 16: *'list' value cannot be converted to Rhino.Geometry.Point3d*.

## Try this

1. Click the V° button on Grasshopper's toolbar until it shows *Always debug* (V° with the Debug icon). Double-click [Rings](gh:Rings): its editor opens.
2. Recompute (F5 on the canvas): the debugger stops at the error, line 16.
3. Hover `points`: not points, but one list per curve. In *V°Python*, `[len(x) for x in points]` gives `[1, 0, 2, 1, 2]`. In Rhino 8, `parallel.run` flattens only .NET collections; `crossings()` returns a Python list, so nothing was flattened. (Had it been, which point came from which curve would be lost: curve 1 has none, curve 2 two.)
4. Set a breakpoint inside `crossings()` (line 11) and recompute: it pauses once, for curve 0. `parallel.run` computes the first item on the component's own thread and the others on .NET's worker threads, where a pause would freeze Rhino (*V°Log* says so). Look at the workers' results after `parallel.run` returns.
5. Fix it: `per_curve = parallel.run(crossings, curves)`, then `points = [p for pts in per_curve for p in pts]` and `tags = [rg.TextDot(str(i), p) for i, pts in enumerate(per_curve) for p in pts]` -- every tag names its own curve.
6. Click the V° button until it shows *Stop and record* again (the default).

## Without miniVivisection

The red message says a list can't be a point -- but which list, and why one? You'd print the result of `parallel.run`, and from the workers, in whatever order they finish.
"""

GH7_SOURCE = '''#! python 3
"""Rings: where each curve crosses the plane at height h -- in parallel; a tag with its curve's number."""
import Rhino.Geometry as rg
import ghpythonlib.parallel as parallel

plane = rg.Plane(rg.Point3d(0, 0, h), rg.Vector3d.ZAxis)


def crossings(crv):
    """The points where crv crosses the plane: none, one or more."""
    events = rg.Intersect.Intersection.CurvePlane(crv, plane, 0.001)
    return [e.PointA for e in events] if events else []


points = parallel.run(crossings, curves, True)   # True: flattened into one list of points
tags = [rg.TextDot(str(i), p) for i, p in enumerate(points)]
'''


def gh7(folder):
    doc = GH_Document()
    up = lambda x, top: rg.LineCurve(rg.Point3d(x, 0, 0), rg.Point3d(x, 0, top))
    vee = lambda x: rg.PolylineCurve([rg.Point3d(x, 0, 0), rg.Point3d(x + 2, 0, 10), rg.Point3d(x + 4, 0, 0)])
    curves = [up(0, 10), up(6, 3), vee(10), up(18, 10), vee(22)]  # crossings at h = 5: 1, 0, 2, 1, 2
    crv = _place(persistent(Param_Curve(), 'Curves', {(0,): [GH_Curve(c) for c in curves]}), 480, 60)
    h = _place(persistent(Param_Number(), 'h', {(0,): [GH_Number(5.0)]}), 480, 130)
    rings = _place(script('Rings', 'Rings', GH7_SOURCE, [('curves', GH_ParamAccess.list, 'Curve'), ('h', GH_ParamAccess.item, 'double')], ['tags', 'points']), 640, 80)
    lesson = panel(GH7_TEXT + nav(7), 20, 20, height=710)
    for obj in (lesson, crv, h, rings):
        doc.AddObject(obj, False)
    rings.Params.Input[0].AddSource(crv)
    rings.Params.Input[1].AddSource(h)
    _show_as(lesson, 'Markdown')
    return save(doc, os.path.join(folder, 'gh7_parallel_run.gh'))


def _toggle(nickname, x, y):
    toggle = GH_BooleanToggle()
    toggle.CreateAttributes()
    toggle.Value = False  # saved off: the lesson doesn't run (or hang) when it opens
    toggle.NickName = nickname
    return _place(toggle, x, y)


def _slider(nickname, low, high, value, decimals, x, y):
    slider = GH_NumberSlider()
    slider.CreateAttributes()
    slider.Slider.Minimum = System.Decimal(low)
    slider.Slider.Maximum = System.Decimal(high)
    slider.Slider.DecimalPlaces = decimals
    slider.SetSliderValue(System.Decimal(value))
    slider.NickName = nickname
    return _place(slider, x, y)


def _script_input(component, as_path=False):
    """The component runs the script of its "script" input (its menu > Script input); with
    as_path, the input's text names the file (the input's menu > Input Is Path)."""
    component.UsingScriptInputParam = True
    param = [p for p in component.Params.Input if p.Name == 'script'][0]
    if as_path:
        any_member = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        param.GetType().GetProperty('TreatInputAsPath', any_member).SetValue(param, True)
    return param


def _input(component, name):
    return [p for p in component.Params.Input if p.Name == name][0]


# The component's own script, unused: it runs its script input's.
INPUT_SOURCE = '''#! python 3
# This component runs the script of its "script" input.
'''

GH8_TEXT = """# GH 8 -- A long-running component: pause it, fix it, let it run on

## The bug

[Clock](gh:Clock) draws a watch face and its second hand at every mark -- one mark every 0.25 s, so it computes for 15 s while Grasshopper and Rhino wait. The face is right, but the hands start at 3 instead of 12, and go round the wrong way.

## Try this

1. Click the V° button on Grasshopper's toolbar until it shows *Always debug* (V° with the Debug icon): Clock gets its editor, out of sight.
2. Set [Run](gh:Run) to True, and press **Ctrl+Alt+B** while Clock computes: it pauses at its next line, and its editor comes up there. The canvas still pans and shows data meanwhile.
3. Hover `tick` and `angle`: `angle` grows from 0 -- the direction of 3 o'clock (angles count from the x axis, counter-clockwise). Add `hands` to the *V°Watch* tab: the hands so far are drawn in Rhino.
4. Fix it without stopping. In the *V°Python* tab, type `def angle_of(tick): return math.pi / 2 - tick * 2 * math.pi / 60` and press Enter twice (the empty line ends the block). Continue (F5 in the editor): the hands from here on point the right way, clockwise.
5. Fix the script the same way, for the next run. Set [Run](gh:Run) to False, and click the V° button until it shows *Stop and record* again: then Ctrl+Alt+B stops Clock instead, and its editor shows where it was (GH 3).
6. Rhino and Grasshopper wait while Clock computes: Grasshopper runs components on Rhino's UI thread. Add `# async:true` as line 2 of Clock's script and set Run to True again: Clock computes on a thread of its own, and Rhino's views and the canvas stay usable. Ctrl+Alt+B still stops it (pauses it, on *Always debug*).

## Without miniVivisection

Fifteen seconds of a frozen Rhino, then a wrong result. You'd print the angles, run again, and wait again.
"""

GH8_SOURCE = '''#! python 3
"""Clock: a watch face, and its second hand at every mark -- one mark every 0.25 s."""
import math
import time
import Rhino.Geometry as rg

RADIUS = 50.0
STEP = 0.25          # seconds per mark: 60 marks, 15 s


def angle_of(tick):
    # The hand's direction for mark `tick` (0 to 59).
    return tick * 2 * math.pi / 60


def at(angle, radius):
    return rg.Point3d(radius * math.cos(angle), radius * math.sin(angle), 0)


face = [rg.Circle(RADIUS).ToNurbsCurve()]
for mark in range(60):
    a = math.pi / 2 - mark * 2 * math.pi / 60        # mark 0 at 12, clockwise
    face.append(rg.LineCurve(at(a, RADIUS * (0.85 if mark % 5 == 0 else 0.93)), at(a, RADIUS)))

hands = []
if run:
    for tick in range(60):
        angle = angle_of(tick)
        hand = rg.LineCurve(rg.Point3d.Origin, at(angle, RADIUS * 0.8))
        hands.append(hand)
        time.sleep(STEP)
'''


def gh8(folder):
    doc = GH_Document()
    run = _toggle('Run', 470, 60)
    clock = _place(script('Clock', 'Clock', GH8_SOURCE, [('run', GH_ParamAccess.item, 'bool')], ['face', 'hands']), 640, 60)
    lesson = panel(GH8_TEXT + nav(8), 20, 20, height=710)
    for obj in (lesson, run, clock):
        doc.AddObject(obj, False)
    clock.Params.Input[0].AddSource(run)
    _show_as(lesson, 'Markdown')
    return save(doc, os.path.join(folder, 'gh8_long_running.gh'))


GH9_TEXT = """# GH 9 -- A script from a Panel: edit it in the Script Editor

## The bug

[Twist](gh:Twist) stacks [n](gh:n) squares, each turned [angle](gh:angle) degrees more than the one below: a smooth twist, 5° per level. Instead they jump around. Twist's script isn't inside Twist: its *script* input is connected to the Panel [Twist script](<gh:Twist script>), and Twist runs whatever text arrives there.

## Try this

1. *Right-click Twist > V° Edit the script input in the Script Editor*. The Panel's script opens in the Script Editor as a file of its own: colors, line numbers, completion.
2. Line 8: `Rotate` wants its angle in radians -- `i * angle` is in degrees.
3. Fix it: `square.Rotate(math.radians(i * angle), rg.Vector3d.ZAxis, rg.Point3d.Origin)`, and `import math` at the top.
4. Save (Ctrl+S), or close the Script Editor: the text goes back into the Panel, and Twist recomputes -- a smooth twist. Move the [angle](gh:angle) slider.
5. The Panel shows the script as code too: double-click it to edit it right there.
6. A Panel's script that hangs: Ctrl+Alt+B (*Stop and record*, the V° button's default) stops it, and the Panel opens for editing right there, at the line where it stopped.

## Without miniVivisection

You'd edit Python in a Panel: no colors, no line numbers -- an error's *line 8* counted by hand -- and nothing shows where a hanging Panel script was.
"""

GH9_SCRIPT = '''#! python 3
"""Twist: n squares stacked up, each turned a little more than the one below."""
import Rhino.Geometry as rg

squares = []
for i in range(n):
    square = rg.Rectangle3d(rg.Plane.WorldXY, rg.Interval(-5, 5), rg.Interval(-5, 5)).ToNurbsCurve()
    square.Rotate(i * angle, rg.Vector3d.ZAxis, rg.Point3d.Origin)    # angle: degrees
    square.Translate(rg.Vector3d(0, 0, i * 1.5))
    squares.append(square)
'''


def gh9(folder):
    doc = GH_Document()
    n = _slider('n', 1, 40, 12, 0, 470, 50)
    angle = _slider('angle', 0, 20, 5, 1, 470, 90)
    source = panel(GH9_SCRIPT, 470, 140, width=470, height=170)
    source.NickName = 'Twist script'
    twist = _place(script('Twist', 'Twist', INPUT_SOURCE, [('n', GH_ParamAccess.item, 'int'), ('angle', GH_ParamAccess.item, 'double')], ['squares']), 1000, 80)
    script_input = _script_input(twist)
    lesson = panel(GH9_TEXT + nav(9), 20, 20, height=600)
    for obj in (lesson, n, angle, source, twist):
        doc.AddObject(obj, False)
    script_input.AddSource(source)
    _input(twist, 'n').AddSource(n)
    _input(twist, 'angle').AddSource(angle)
    _show_as(lesson, 'Markdown')  # the "Twist script" Panel stays on Autodetect: shown as code
    return save(doc, os.path.join(folder, 'gh9_script_from_a_panel.gh'))


GH10_TEXT = """# GH 10 -- A script from a file: Ctrl+Alt+B opens the file where it was

## The bug

[Spiral](gh:Spiral) puts [n](gh:n) points on a spiral and a smooth curve through them: with n = 60, a second's work. Set [Run](gh:Run) to True, and Grasshopper and Rhino hang for over a minute. Spiral's script is the file `gh10_spiral.py` next to this definition: the Panel [Script file](<gh:Script file>) gives its name to Spiral's *script* input (right-click that input: *Input Is Path* is checked; a name without a folder is found next to the definition).

## Try this

1. Keep the V° button on Grasshopper's toolbar on *Stop and record* (the default: V° with a green Run). Set [Run](gh:Run) to True: Rhino hangs.
2. Press **Ctrl+Alt+B**: Spiral stops, and `gh10_spiral.py` itself opens in the Script Editor at the line where it was, with the values then: `i` is far past `n` -- in the hundreds or thousands.
3. Line 10: `range(n * n)` -- 3600 steps, not 60. Fix it: `range(n)`.
4. Save (Ctrl+S), or close the Script Editor: Grasshopper reads the file again and Spiral recomputes -- 60 points in about a second.
5. To open the file another time: *Right-click Spiral > V° Edit the script input in the Script Editor*.
6. Set [Run](gh:Run) to False before you save the definition, so it doesn't hang when it opens.

## Without miniVivisection

Rhino hangs: you wait, or end it in the Task Manager. Then you open the file yourself, guess where it was, and print `i`.
"""

GH10_FILE = 'gh10_spiral.py'

GH10_SCRIPT = '''#! python 3
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
'''


def gh10(folder):
    doc = GH_Document()
    run = _toggle('Run', 470, 60)
    n = _place(persistent(Param_Integer(), 'n', {(0,): [GH_Integer(60)]}), 480, 110)
    name = panel(GH10_FILE, 470, 160, width=140, height=24)
    name.NickName = 'Script file'
    spiral = _place(script('Spiral', 'Spiral', INPUT_SOURCE, [('run', GH_ParamAccess.item, 'bool'), ('n', GH_ParamAccess.item, 'int')], ['spiral', 'pts']), 680, 80)
    script_input = _script_input(spiral, as_path=True)
    lesson = panel(GH10_TEXT + nav(10), 20, 20, height=620)
    for obj in (lesson, run, n, name, spiral):
        doc.AddObject(obj, False)
    script_input.AddSource(name)
    _input(spiral, 'run').AddSource(run)
    _input(spiral, 'n').AddSource(n)
    _show_as(lesson, 'Markdown')
    _show_as(name, 'Text')  # a file name, not a script: plain text (Autodetect would show it as Python code)
    # The script itself, next to the definition: the Panel names it without a folder.
    with open(os.path.join(folder, GH10_FILE), 'w', encoding='utf-8', newline='\n') as f:
        f.write(GH10_SCRIPT)
    return save(doc, os.path.join(folder, 'gh10_script_from_a_file.gh'))


def _show_as(panel, mode):
    """The Panel's "Show as" (miniVivisection.GH's ScriptPanels): 'Markdown', 'Python', 'CSharp',
    'Text' or 'Auto' -- saved with it. After it's in its document."""
    panels = _assembly('miniVivisection.GH').GetType('MiniVivisection.GH.ScriptPanels')
    show_as = panels.GetNestedType('ShowAs', System.Reflection.BindingFlags.NonPublic)
    flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    panels.GetMethod('SetShowAs', flags).Invoke(None, [panel, System.Enum.Parse(show_as, mode)])


GH11_TEXT = """# GH 11 -- Notes in Markdown

This Panel is written in **Markdown** and shown rendered: *right-click it > V° Show as* -- **Markdown (rendered)** is checked.

## The bug

*Ring* should compute the area between two circles, as the **Notes** Panel describes. With outer = 10 and inner = 6 it says `50.27` -- the notes' table says `201.06`.

## Try this

1. Read the **Notes** Panel: the formula is `area = pi * (outer**2 - inner**2)`.
2. Double-click the word *formula* in the Notes: its Markdown source opens right there, the caret on that word. Click elsewhere: rendered again (Esc drops your changes).
3. Double-click the **Ring script** Panel -- Ring's script comes from it: the Python opens in place. Line 6 squares the *difference* of the radii, not each radius. Fix it: `area = math.pi * (outer ** 2 - inner ** 2)`, and click elsewhere: Ring recomputes.
4. Add a line to the Notes' *Inputs* list: Enter after the last item starts the next one. Try **bold**, *italic*, `code` and [a link](https://www.rhino3d.com).
5. *Right-click any Panel > V° Show as*: *Autodetect* (code or Markdown, as the text is), *Python code*, *C# code*, *Markdown (rendered)* or *Plain text* -- saved with the definition.

## Links

Markdown links lead to components, views and Rhino. Click one -- a double-click still edits (V°Settings > *Markdown* > *Markdown links: follow with*: Click, Ctrl+click or Off):

- [The Ring component](gh:Ring) -- the canvas glides there, a red circle marks it for a moment
- [The overview](ghview:Overview) -- a named view of this canvas (*View > Named Views*)
- [The V° logo in the Rhino example](rhino:../00_overview.3dm#Logo) -- Rhino opens *00_overview.3dm* (asking to save your model first) and zooms to its logo
- [Its Markdown text](rhinoview:../00_overview.3dm#MarkdownText) -- that file's named view; [Perspective](rhinoview:Perspective) -- a Rhino viewport by its title

Write them as `[text](gh:Nickname)` -- a name with spaces in angle brackets: `[text](<gh:Ring script>)` -- and a file before the name, relative to this definition: `rhino:../00_overview.3dm#Logo`. In Rhino, *00_overview.3dm*'s links lead back here: click them.

## Without miniVivisection

A Panel shows Markdown as raw, centered text with `**` and `#` everywhere; a script in a Panel has no line numbers or colors, and you edit it in a plain text box.
"""

GH11_NOTES = """# Ring

The **area** of the ring between two circles around the origin, and the circles.

## Inputs

- `outer` -- the outer radius
- `inner` -- the inner radius, smaller than `outer`

## Output

- `area` -- the outer circle's area minus the inner one's
- `circles` -- both circles

The formula:

```
area = pi * (outer**2 - inner**2)
```

| outer | inner | area   |
|-------|-------|--------|
| 10    | 6     | 201.06 |
| 3     | 1     | 25.13  |

Its script: [the Ring script Panel](<gh:Ring script>) -- a name with spaces, in angle brackets.

> Keep these notes next to the component: they're saved with the definition.
"""

GH11_SCRIPT = '''#! python 3
"""Ring: the area between two circles around the origin, and the circles."""
import math
import Rhino.Geometry as rg

area = math.pi * (outer - inner) ** 2
circles = [rg.Circle(outer).ToNurbsCurve(), rg.Circle(inner).ToNurbsCurve()]
'''


def gh11(folder):
    doc = GH_Document()
    outer = _slider('outer', 1, 20, 10, 1, 520, 470)
    inner = _slider('inner', 0, 19, 6, 1, 520, 510)
    script_panel = panel(GH11_SCRIPT, 520, 560, width=520, height=150)
    script_panel.NickName = 'Ring script'
    ring = _place(script('Ring', 'Ring', INPUT_SOURCE, [('outer', GH_ParamAccess.item, 'double'), ('inner', GH_ParamAccess.item, 'double')], ['area', 'circles']), 1100, 500)
    script_input = _script_input(ring)
    notes = panel(GH11_NOTES, 520, 20, width=420, height=420)
    notes.NickName = 'Notes'
    lesson = panel(GH11_TEXT + nav(11), 20, 20, height=840)
    for obj in (lesson, notes, outer, inner, script_panel, ring):
        doc.AddObject(obj, False)
    # The named view the links go to: all of it
    doc.Properties.ViewList.Add(Grasshopper.GUI.Canvas.GH_NamedView('Overview', System.Drawing.PointF(640, 420), 0.6, Grasshopper.GUI.Canvas.GH_NamedViewType.center))
    script_input.AddSource(script_panel)
    _input(ring, 'outer').AddSource(outer)
    _input(ring, 'inner').AddSource(inner)
    _show_as(lesson, 'Markdown')
    _show_as(notes, 'Markdown')
    return save(doc, os.path.join(folder, 'gh11_markdown_notes.gh'))


GH12_TEXT = """# GH 12 -- Markdown with Rhino's parser

The **Checklist** Panel is Markdown, parsed by *Markdig* -- Rhino's own, as the Script Editor's preview (V°Settings > *Markdown* > *Markdown parser*: **Rhino**, the default).

## The bug

Two things in the Checklist don't come out as meant: the last task, *Tag the release*, runs into the one before it instead of getting a box of its own, and the *Results* table runs together into one line.

## Try this

1. Look at the tasks: `- [x]` is a ticked box, `- [ ]` an open one, and the indented ones are nested. Click a box: it is checked or unchecked in the Panel's text.
2. Double-click *Tag the release*: its source opens right there -- `-[ ] Tag the release`. A list item needs a space after the dash: `- [ ] Tag the release`. Click elsewhere: a box of its own.
3. Double-click the table: a Markdown table needs a rule under its first row. Add `|---|---|` as the second line of the table, and click elsewhere: a table.
4. Set V°Settings > *Markdown* > *Markdown parser* to **miniVivisection**: the plug-in's simpler parser draws the boxes too, but doesn't resolve the footnote and shows the inner quote's `>`. Set it back to **Rhino**.
5. Turn V°Settings > *Markdown* > *Markdown in Panels* off: the Panels show their source, as Grasshopper does. Turn it on again.

## Without miniVivisection

A Panel shows the checklist as raw text -- brackets, pipes and stars, centered -- and nothing tells you that a space or a rule row is missing.
"""

GH12_CHECKLIST = """# Release checklist

## Before the release

- [x] Run the live tests
- [ ] Update the manual
  - [x] The feature list
  - [ ] The PDFs
-[ ] Tag the release

## Results

| Test | Result |
| t_ghexamples | passed |
| t_ghscriptpanel | passed |

> **Note**
> > A quote in a quote: one step in.

~~Ship on Friday~~ -- ship when the list is done[^1].

[^1]: Footnotes come out at the bottom.
"""


def gh12(folder):
    doc = GH_Document()
    checklist = panel(GH12_CHECKLIST, 520, 20, width=420, height=470)
    checklist.NickName = 'Checklist'
    lesson = panel(GH12_TEXT + nav(12), 20, 20, height=600)
    for obj in (lesson, checklist):
        doc.AddObject(obj, False)
    _show_as(lesson, 'Markdown')
    _show_as(checklist, 'Markdown')
    return save(doc, os.path.join(folder, 'gh12_markdown_parser.gh'))


# The lessons' files, in order: the links at the end of each lesson go to the one before and after
LESSON_FILES = ['gh1_which_branch_failed.gh', 'gh2_the_one_wrong_item.gh', 'gh3_stop_a_hang.gh', 'gh4_v_edit.gh', 'gh5_runtime_error.gh',
                'gh6_threads_in_a_component.gh', 'gh7_parallel_run.gh', 'gh8_long_running.gh', 'gh9_script_from_a_panel.gh',
                'gh10_script_from_a_file.gh', 'gh11_markdown_notes.gh', 'gh12_markdown_parser.gh']


def _title(n):
    """GH n's title, its heading's words after the dash: "Which branch failed"."""
    heading = globals()['GH%d_TEXT' % n].strip().split('\n')[0]
    return heading.split(' -- ', 1)[1].strip() if ' -- ' in heading else heading.lstrip('# ').strip()


def nav(n):
    """The end of lesson GH n: links to the lesson before and after (where there is one), files
    beside it -- a click opens it, or shows it when it's open already."""
    parts = []
    if n > 1:
        parts.append('[<-- GH %d: %s](%s)' % (n - 1, _title(n - 1), LESSON_FILES[n - 2]))
    if n < len(LESSON_FILES):
        parts.append('[GH %d: %s -->](%s)' % (n + 1, _title(n + 1), LESSON_FILES[n]))
    parts.insert(1 if n > 1 else 0, '[All examples](%s)' % examples_overview.GH_FILE)
    protect = ('\n\n*Changed the lesson? **Revert** under this text puts it back as it was shipped '
               '(V°Protect: green while it is the original, red when changed).*')
    return protect + '\n\n---\n\n' + ' · '.join(parts) + '\n'


def gh0(folder):
    """The overview: every example linked (tools/examples_overview.py), a slider to change, V Protect under it."""
    doc = GH_Document()
    lesson = panel(examples_overview.markdown('gh'), 20, 20, width=560, height=1320)
    try_me = _slider('Try me', 0, 10, 5, 0, 620, 40)
    for obj in (lesson, try_me):
        doc.AddObject(obj, False)
    doc.Properties.ViewList.Add(Grasshopper.GUI.Canvas.GH_NamedView('Overview', System.Drawing.PointF(320, 500), 0.6, Grasshopper.GUI.Canvas.GH_NamedViewType.center))
    _show_as(lesson, 'Markdown')
    # V Protect right under the slider it is tried with, in sight next to the text (not under its long Panel)
    return save(doc, os.path.join(folder, examples_overview.GH_FILE), protect_under=try_me)


def make(folder=FOLDER):
    """The lessons' .gh files (GH 10 writes its script file next to its definition); the overview: gh0."""
    return [gh1(folder), gh2(folder), gh3(folder), gh4(folder), gh5(folder), gh6(folder), gh7(folder), gh8(folder), gh9(folder), gh10(folder), gh11(folder), gh12(folder)]


if __name__ == '__main__':
    for written in [gh0(FOLDER)] + make():
        print('wrote ' + written)
