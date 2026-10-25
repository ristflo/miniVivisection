#! python 3
# A long-running script component with "# async:true", run plainly (Stop and record): it computes
# on a thread of its own, so Rhino's UI thread answers meanwhile -- this test's own steps run
# while it computes, the canvas works (IsPausedIn), and it finishes with its result; the same
# script without async:true holds the UI thread to the end; Ctrl+Alt+B stops an async one that
# hangs.
import os
import sys
import threading
import time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

SLOW = '''#! python 3
{marker}
"""Slow: 4 s of work."""
import time
out = 0
if run:
    for i in range(40):
        time.sleep(0.1)
        out = i
'''

HANG = '''#! python 3
# async:true
import time
out = 0
while run:
    time.sleep(0.01)
'''


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def test():
    yield 2
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Stop and record')
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess, GH_RuntimeMessageLevel
    import make_gh_examples as mk
    off_ui = lambda: lt.static_get('GrasshopperRuns', 'SolvingOffUiThread')
    try:
        doc = GH_Document()
        toggle = mk._toggle('Run', 20, 20)
        slow = mk._place(mk.script('Slow', 'Slow', SLOW.format(marker='# async:true'), [('run', GH_ParamAccess.item, 'bool')], ['out']), 200, 20)
        plain = mk._place(mk.script('Plain', 'Plain', SLOW.format(marker='# (runs on the UI thread)'), [('run', GH_ParamAccess.item, 'bool')], ['out']), 200, 120)
        hang = mk._place(mk.script('Hang', 'Hang', HANG, [('run', GH_ParamAccess.item, 'bool')], ['out']), 200, 220)
        for obj in (toggle, slow, plain, hang):
            doc.AddObject(obj, False)
        mk._input(slow, 'run').AddSource(toggle)
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        yield 2
        result = lambda component: [g.Value for g in component.Params.Output[0].VolatileData.AllData(True)]

        # async:true: this test's steps (on the UI thread) run while Slow computes
        seen = []

        def answering():
            if off_ui():
                seen.append(lt.static_call('GrasshopperPauseCanvas', 'IsPausedIn', doc))
            return len(seen) >= 3

        started = time.time()
        toggle.Value = True
        toggle.ExpireSolution(False)
        doc.ScheduleSolution(50)  # Grasshopper's own timer: the run doesn't sit inside this test's step
        yield lt.wait_for(answering, 20)
        lt.check('async:true: the UI thread answers while it computes (3 times)', len(seen) >= 3, True)
        lt.check('and the canvas works meanwhile (pan, hover)', len(seen) > 0 and all(seen), True)
        yield lt.wait_for(lambda: not off_ui() and result(slow) == [39], 30)
        lt.check('it finished: out = 39', result(slow), [39])
        lt.note('the run took %.1f s' % (time.time() - started))

        # Without async:true: the UI thread is busy to the end -- no step runs meanwhile
        mk._input(plain, 'run').AddSource(toggle)
        seen_plain = []
        plain.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield lt.wait_for(lambda: result(plain) == [39] or seen_plain.append(lt.static_get('GrasshopperRuns', 'Solving')) and False, 30)
        lt.check('without async:true: no step ran while it computed', any(seen_plain), False)

        # Ctrl+Alt+B stops an async:true component that hangs (Stop and record)
        mk._input(hang, 'run').AddSource(toggle)

        def press():
            for _ in range(120):
                running = lt.static_get('GrasshopperRuns', 's_running')
                if running is not None and running.NickName == 'Hang' and off_ui():
                    time.sleep(1)
                    lt.progress('Ctrl+Alt+B')
                    lt.static_call('BreakHotkey', 'Break')
                    return
                time.sleep(0.25)
            lt.progress('Hang never ran off the UI thread')

        threading.Thread(target=press, daemon=True).start()
        hang.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield lt.wait_for(lambda: any('stopped by' in r for r in hang.RuntimeMessages(GH_RuntimeMessageLevel.Remark)), 30)
        lt.check('Ctrl+Alt+B stopped the hanging async:true component', any('stopped by' in r for r in hang.RuntimeMessages(GH_RuntimeMessageLevel.Remark)), True)
        toggle.Value = False
        toggle.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield 3
        doc.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)


lt.run('t_ghasyncplain', test)
