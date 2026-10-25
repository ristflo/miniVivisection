#! python 3
# GH 6 with "# async:true" and a breakpoint inside the worker (after its sleep): Rhino hung
# once ("stopped interacting with Windows") -- a Grasshopper component runs on the UI thread
# whatever its script says. Now its debug run goes to a thread of its own: the workers pause
# (each measure-N thread, continued), Rhino stays usable, the solution finishes.
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino


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
    import make_gh_examples as mk
    lines = mk.GH6_SOURCE.split('\n')
    lines.insert(1, '# async:true')
    mk.GH6_SOURCE = '\n'.join(lines)
    path = mk.gh6(os.path.join(tempfile.gettempdir(), 'minivivisection-live'))
    io = GH_DocumentIO()
    io.Open(path)
    doc = io.Document
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    Grasshopper.Instances.ActiveCanvas.Document = doc
    doc.Enabled = True
    doc.NewSolution(False)
    yield 3
    measure = [o for o in doc.Objects if o.NickName == 'Measure'][0]
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    hook = lt.hook()
    paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
    try:
        runs = lt.plugin_type('GrasshopperRuns')
        runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [measure])
        yield lt.wait_for(lambda: runs.GetMethod('HasVisibleEditor', lt.ANY).Invoke(None, [measure]), 60)
        yield 3
        code = runs.GetMethod('CodeOf', lt.ANY).Invoke(None, [measure])
        line = [i + 1 for i, l in enumerate(lines) if 'lengths.append(length)' in l][0]
        lt.note('script line 2: %r; breakpoint on line %d (lengths.append, after the sleep)' % (lines[1], line))
        breakpoint = CodeReferenceBreakpoint(code, line)
        code.DebugControls.Breakpoints.Add(breakpoint)
        measure.ExpireSolution(False)
        doc.ScheduleSolution(50)
        threads = []
        import System
        for _ in range(40):
            yield 1
            for each in paused():
                # As V°Python does: evaluated on the paused thread (not the UI thread).
                answer = {}
                def job():
                    answer['name'] = lt.static_call('PythonBridge', 'Exec', 'import threading; print(threading.current_thread().name)', True).strip()
                queued = lt.static_call('FreePause', 'TryRunOnPausedThread', each, System.Action(job))
                for _ in range(20):
                    if 'name' in answer:
                        break
                    yield 0.25
                threads.append(answer.get('name', 'not answered (queued %s)' % queued))
                each.Continue()
            if len(threads) >= 6 and not paused():
                break
        yield 3
        lt.note('paused in: %r' % threads)
        lt.check('async:true: the breakpoint in the worker pauses, in every measure thread', sorted(threads), ['measure-%d' % i for i in range(6)])
        lt.check('the solution finished', measure.Params.Output[1].VolatileDataCount, 6)
        lengths = sorted(round(g.Value, 1) for g in measure.Params.Output[1].VolatileData.AllData(True))
        lt.check('with every length', lengths, [1.5, 3.0, 6.0, 9.0, 12.0, 15.0])
        code.DebugControls.Breakpoints.Remove(breakpoint)
        doc.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)


lt.run('t_gh6async', test)
