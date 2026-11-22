#! python 3
# Rhino 9, Grasshopper 9's Escape watcher: Grasshopper cancels a solution when Esc is down in any
# window of Rhino's process, from a thread of its own (GH_Document.GetEscapeKeyState). First, as a
# control, an Esc during a plain slow run aborts it ("Solution aborted by user."). Then, under Always
# debug, an Esc while the component is paused (Break now) is ignored (GrasshopperPauseGuard's
# postfix): after Continue the solution finishes, with its value and without that warning.
# The test Rhino gets no keys on its own desktop: the Esc is GrasshopperPauseGuard.SimulatedEscape,
# seen by Grasshopper's watcher through the same postfix.
#   X -> P (a = x * 2, slow: 6 s)
# Run: python tests/live/run_live.py --rhino 9 t_rhino9_gh_esc
import os
import sys
import threading
import time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

SLOW = '''#! python 3
import time
i = 0
while i < 600:
    time.sleep(0.01)
    i += 1
a = x * 2
'''
ABORTED = 'Solution aborted by user.'


def ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def test():
    yield 2
    lt.check('Rhino 9', Rhino.RhinoApp.Version.Major, 9)
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Normal')
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess, GH_RuntimeMessageLevel
    from Grasshopper.Kernel.Special import GH_NumberSlider
    import make_gh_examples as mk
    guard = lt.plugin_type('GrasshopperPauseGuard')
    escape = guard.GetField('SimulatedEscape', lt.ANY)
    ignored = lambda: lt.static_get('GrasshopperPauseGuard', 'EscapesIgnored')
    hook = lt.hook()
    paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))

    def hold_escape(after, seconds):
        time.sleep(after)
        lt.progress('Esc down')
        escape.SetValue(None, True)
        time.sleep(seconds)
        escape.SetValue(None, False)
        lt.progress('Esc up')

    try:
        doc = GH_Document()
        x = mk._place(GH_NumberSlider(), 20, 40)
        x.SetInitCode('0<3<10')
        x.NickName = 'X'
        p = mk._place(mk.script('P', 'P', SLOW, [('x', GH_ParamAccess.item, 'double')], ['a']), 250, 40)
        for obj in (x, p):
            doc.AddObject(obj, False)
        p.Params.Input[0].AddSource(x)
        a_index = [o.NickName for o in p.Params.Output].index('a')
        value = lambda: ([g.Value for g in p.Params.Output[a_index].VolatileData.AllData(True)] or [None])[0]
        warnings = lambda: [str(w) for w in p.RuntimeMessages(GH_RuntimeMessageLevel.Warning)]
        solving = lambda: str(doc.SolutionState) == 'Process'
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        yield lt.wait_for(lambda: value() == 6.0 and not solving(), 30)
        lt.check('first solution: a = 3*2', value(), 6.0)

        # The control: an Esc during a plain run -- Grasshopper 9 aborts the solution
        threading.Thread(target=hold_escape, args=(2, 0.5), daemon=True).start()
        x.SetSliderValue(System.Decimal(4))
        x.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield 1
        yield lt.wait_for(lambda: not solving(), 30)
        yield 1
        lt.note('plain run with Esc: a=%r, warnings %r' % (value(), warnings()))
        lt.check('control: Esc during a plain run aborts the solution (the simulated Esc reaches Grasshopper)', ABORTED in warnings(), True)
        lt.check('control: nothing ignored (nothing paused)', ignored(), 0)

        # Always debug: paused by Break now, Esc held meanwhile, then Continue
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
        yield lt.wait_for(lambda: lt.static_get('GrasshopperHiddenEditors', 'Count') == 1, 60)
        yield 3
        x.SetSliderValue(System.Decimal(2))  # a whole number: the slider is 0<3<10
        x.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield 1
        yield lt.wait_for(lambda: value() == 4.0 and not solving(), 30)
        lt.check('Always debug, no pause: a = 2*2', value(), 4.0)

        def press():
            time.sleep(1.5)
            lt.static_call('BreakHotkey', 'Break')

        threading.Thread(target=press, daemon=True).start()
        x.SetSliderValue(System.Decimal(5))
        x.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('P is paused (Break now)', len(controls), 1)
        if not controls:
            return
        yield 1
        threading.Thread(target=hold_escape, args=(0, 1.0), daemon=True).start()
        yield 2
        lt.check('Esc while paused: ignored by the watcher', ignored() > 0, True)
        lt.note('Esc presses ignored while paused: %d' % ignored())
        lt.check('still paused', len(paused()), 1)
        controls[0].Continue()
        yield lt.wait_for(lambda: len(paused()) == 0, 30)
        yield lt.wait_for(lambda: not solving(), 30)
        yield 1
        lt.note('after Continue: a=%r, warnings %r' % (value(), warnings()))
        lt.check('after Continue: the solution finished, a = 5*2', value(), 10.0)
        lt.check('after Continue: no "Solution aborted by user."', ABORTED in warnings(), False)
        doc.Enabled = False
    finally:
        escape.SetValue(None, False)
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)


lt.run('t_rhino9_gh_esc', test)
