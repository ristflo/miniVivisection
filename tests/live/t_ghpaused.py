#! python 3
# While a Python component is paused in its solution: what's downstream of it is marked, and a
# change elsewhere is computed at once -- also downstream of the paused component, from its last
# finished run's data -- not only when the pause ends; after it, everything is computed properly.
#   X -> P (a = x * 2, slow) -> Add (a + y);  Y -> Add, Y -> Negative
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


def ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def stock(name):
    import Grasshopper
    for proxy in Grasshopper.Instances.ComponentServer.ObjectProxies:
        if proxy.Desc.Name == name and not proxy.Obsolete:
            obj = Grasshopper.Instances.ComponentServer.EmitObject(proxy.Guid)
            obj.CreateAttributes()
            return obj
    raise RuntimeError(name)


def value(obj, index=0):
    data = [g.Value for g in obj.Params.Output[index].VolatileData.AllData(True)]
    return data[0] if data else None


def test():
    yield 2
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess
    from Grasshopper.Kernel.Special import GH_NumberSlider
    import make_gh_examples as mk
    hook = lt.hook()
    paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
    marked = lambda: lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.PausedSolution').GetProperty('MarkedCount').GetValue(None)
    try:
        doc = GH_Document()
        x = mk._place(GH_NumberSlider(), 20, 40)
        x.SetInitCode('0<3<10')
        x.NickName = 'X'
        y = mk._place(GH_NumberSlider(), 20, 160)
        y.SetInitCode('0<1<10')
        y.NickName = 'Y'
        p = mk._place(mk.script('P', 'P', SLOW, [('x', GH_ParamAccess.item, 'double')], ['a']), 250, 40)
        add = stock('Addition')
        add.Attributes.Pivot = System.Drawing.PointF(450, 80)
        neg = stock('Negative')
        neg.Attributes.Pivot = System.Drawing.PointF(450, 200)
        for obj in (x, y, p, add, neg):
            doc.AddObject(obj, False)
        p.Params.Input[0].AddSource(x)
        add.Params.Input[0].AddSource(p.Params.Output[[o.NickName for o in p.Params.Output].index('a')])
        add.Params.Input[1].AddSource(y)
        neg.Params.Input[0].AddSource(y)
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        yield lt.wait_for(lambda: lt.static_get('GrasshopperHiddenEditors', 'Count') == 1, 60)
        yield 3
        doc.NewSolution(False)
        yield lt.wait_for(lambda: value(add) is not None and len(paused()) == 0, 30)
        lt.check('first solution: Add = 3*2 + 1', value(add), 7.0)
        lt.check('Negative = -1', value(neg), -1.0)

        # X moved: P runs again (its last data, 6, remembered), and is paused in that run
        def press():
            time.sleep(1.5)
            lt.static_call('BreakHotkey', 'Break')

        threading.Thread(target=press, daemon=True).start()
        x.SetSliderValue(System.Decimal(4))
        x.ExpireSolution(False)
        doc.ScheduleSolution(50)  # from a timer: this step mustn't be inside the paused solution
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('P is paused', len(controls), 1)
        if not controls:
            return
        lt.check('P remembered its last data', lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.PausedSolution').GetMethod('HasLast').Invoke(None, [p]), True)
        yield 1
        lt.check('what depends on P is marked (Add)', marked(), 1)

        # Y moved while P is paused: Negative and Add computed now -- Add from P's last data (6)
        y.SetSliderValue(System.Decimal(5))
        y.ExpireSolution(True)
        yield lt.wait_for(lambda: value(neg) == -5.0, 5)
        lt.check('while paused: Negative = -5', value(neg), -5.0)
        lt.check('while paused: Add = 6 (P\'s last) + 5', value(add), 11.0)
        lt.check('still paused', len(paused()), 1)
        lt.check('P not run again', str(p.Params.Output[[o.NickName for o in p.Params.Output].index('a')].VolatileDataCount), '0')

        controls[0].Continue()
        yield lt.wait_for(lambda: len(paused()) == 0, 30)
        yield lt.wait_for(lambda: value(add) == 13.0, 15)
        a_index = [o.NickName for o in p.Params.Output].index('a')
        lt.note('after: P.a=%r; Add inputs %r %r; Add phase %s; held %d; doc %s' % (
            [g.Value for g in p.Params.Output[a_index].VolatileData.AllData(True)],
            [g.Value for g in add.Params.Input[0].VolatileData.AllData(True)], [g.Value for g in add.Params.Input[1].VolatileData.AllData(True)],
            add.Phase, len(list(lt.static_call('GrasshopperPauseGuard', 'HeldItems'))), doc.SolutionState))
        lt.check('after the pause: Add = 4*2 + 5', value(add), 13.0)
        yield 1
        lt.check('no marks after the pause', marked(), 0)
        doc.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)


lt.run('t_ghpaused', test)
