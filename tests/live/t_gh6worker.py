#! python 3
# gh6: a breakpoint inside the worker function -- on its first line (10) and on its last
# (12, after the sleep) -- must never pause (the freeze guard), and the solution must end.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

GH6 = os.path.join(lt.ROOT, r'examples\grasshopper\gh6_threads_in_a_component.gh')


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_DocumentIO
    from Rhino.Runtime.Code.Execution.Debugging import CodeReferenceBreakpoint
    io = GH_DocumentIO()
    lt.check('opens gh6', io.Open(GH6), True)
    doc = io.Document
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    Grasshopper.Instances.ActiveCanvas.Document = doc
    doc.Enabled = True
    doc.NewSolution(False)
    yield 3
    measure = [o for o in doc.Objects if o.NickName == 'Measure'][0]
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    try:
        runs = lt.plugin_type('GrasshopperRuns')
        runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [measure])
        yield lt.wait_for(lambda: runs.GetMethod('HasVisibleEditor', lt.ANY).Invoke(None, [measure]), 60)
        yield 3
        code = runs.GetMethod('CodeOf', lt.ANY).Invoke(None, [measure])
        hook = lt.hook()
        paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
        for line in (10, 12):
            bp = CodeReferenceBreakpoint(code, line)
            code.DebugControls.Breakpoints.Add(bp)
            lt.note('line %d: breakpoint set' % line)
            measure.ExpireSolution(False)
            doc.ScheduleSolution(50)
            yield 8
            lt.note('line %d: 8 s later' % line)
            lt.check("line %d: a breakpoint in a worker doesn't pause" % line, len(paused()), 0)
            lt.check('line %d: the solution finished' % line, measure.Params.Output[1].VolatileDataCount, 6)
            code.DebugControls.Breakpoints.Remove(bp)
            yield 1
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)
    doc.Enabled = False


lt.run('t_gh6worker', test)
