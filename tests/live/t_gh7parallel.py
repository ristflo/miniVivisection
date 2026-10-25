#! python 3
# GH 7, ghpythonlib.parallel, as its panel says: flatten=True doesn't flatten (Rhino 8 flattens
# only .NET collections), so line 16 fails; Always debug pauses at the error with one list per
# curve; a breakpoint inside the worker function pauses once, for curve 0, on the component's
# thread -- Rhino never freezes; the fix tags every point with its own curve.
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

FIX = ('points = parallel.run(crossings, curves, True)   # True: flattened into one list of points\n'
       'tags = [rg.TextDot(str(i), p) for i, p in enumerate(points)]')
FIXED = ('per_curve = parallel.run(crossings, curves)\n'
         'points = [p for pts in per_curve for p in pts]\n'
         'tags =[rg.TextDot(str(i), p) for i, pts in enumerate(per_curve) for p in pts]')


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def open_doc(path):
    import Grasshopper
    from Grasshopper.Kernel import GH_DocumentIO
    io = GH_DocumentIO()
    io.Open(path)
    doc = io.Document
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    Grasshopper.Instances.ActiveCanvas.Document = doc
    doc.Enabled = True
    doc.NewSolution(False)
    return doc


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_RuntimeMessageLevel
    from Rhino.Runtime.Code.Execution.Debugging import CodeReferenceBreakpoint
    import make_gh_examples as mk
    folder = os.path.join(tempfile.gettempdir(), 'minivivisection-live')
    doc = open_doc(mk.gh7(folder))
    yield 3
    rings = [o for o in doc.Objects if o.NickName == 'Rings'][0]
    errors = list(rings.RuntimeMessages(GH_RuntimeMessageLevel.Error))
    lt.check("the bug: line 16 fails, a list isn't a point", len(errors) == 1 and "'list' value cannot be converted" in errors[0] and '[16:' in errors[0], True)
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    hook = lt.hook()
    paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
    try:
        runs = lt.plugin_type('GrasshopperRuns')
        runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [rings])
        yield lt.wait_for(lambda: runs.GetMethod('HasVisibleEditor', lt.ANY).Invoke(None, [rings]), 60)
        yield 3
        code = runs.GetMethod('CodeOf', lt.ANY).Invoke(None, [rings])

        # 2-3: recomputed, the debugger stops at the error; one list per curve.
        rings.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('Always debug: paused at the error', len(controls), 1)
        if controls:
            frame = controls[0].Results.CurrentThread.CurrentFrame
            lt.check('at line 16', (frame.Reference.Position.LineNumber, str(frame.Event)), (16, 'Exception'))
            seen = lt.static_call('PythonBridge', 'Exec', 'print([len(x) for x in points])', True).strip()
            lt.check('one list per curve: [1, 0, 2, 1, 2]', seen, '[1, 0, 2, 1, 2]')
            controls[0].Stop()
            yield lt.wait_for(lambda: len(paused()) == 0, 20)

        # 4: inside the worker function -- once, for curve 0, on the component's thread.
        inside = CodeReferenceBreakpoint(code, 11)
        code.DebugControls.Breakpoints.Add(inside)
        rings.ExpireSolution(False)
        doc.ScheduleSolution(50)
        pauses = []
        for _ in range(12):
            yield 1
            for each in paused():
                if str(each.Results.CurrentThread.CurrentFrame.Event) == 'Exception':
                    each.Stop()  # line 16 again: not this step's
                    continue
                name = lt.static_call('PythonBridge', 'Exec', 'import threading; print(threading.current_thread().name, crv.PointAtStart.X)', True).strip()
                pauses.append(name)
                each.Continue()
        lt.note('pauses inside crossings(): %r' % pauses)
        lt.check("inside crossings(): one pause -- curve 0 (x = 0), the component's own thread", pauses, ['MainThread 0.0'])
        code.DebugControls.Breakpoints.Remove(inside)
        doc.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)

    # 5: the fix -- every tag names its own curve.
    assert FIX in mk.GH7_SOURCE
    mk.GH7_SOURCE = mk.GH7_SOURCE.replace(FIX, FIXED)
    fixed = open_doc(mk.gh7(os.path.join(folder, 'fixed')))
    yield 3
    rings = [o for o in fixed.Objects if o.NickName == 'Rings'][0]
    tags = [g.Value.Text for g in rings.Params.Output[0].VolatileData.AllData(True)]
    lt.check('the fix: 6 tags, each with its curve -- 0, 2, 2, 3, 4, 4', tags, ['0', '2', '2', '3', '4', '4'])
    lt.check('the fix: the points output has the 6 points', rings.Params.Output[1].VolatileDataCount, 6)
    fixed.Enabled = False


lt.run('t_gh7parallel', test)
