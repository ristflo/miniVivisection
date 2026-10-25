#! python 3
# A runtime error right after an input was disconnected (a Ctrl+drag) doesn't pause -- the
# editor would come up in the middle of the wiring; the same error on a plain recompute does
# (Always debug, the component's hidden editor).
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

SOURCE = '''#! python 3
y = x * 2       # x missing (disconnected): None * 2 fails
'''


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess, GH_RuntimeMessageLevel
    from Grasshopper.Kernel.Special import GH_NumberSlider
    import make_gh_examples as mk
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    hook = lt.hook()
    paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
    try:
        doc = GH_Document()
        slider = mk._place(GH_NumberSlider(), 20, 40)
        slider.SetInitCode('0<3<10')
        double = mk._place(mk.script('Double', 'Double', SOURCE, [('x', GH_ParamAccess.item, 'double')], ['y']), 260, 40)
        doc.AddObject(slider, False)
        doc.AddObject(double, False)
        double.Params.Input[0].AddSource(slider)
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        yield lt.wait_for(lambda: lt.static_get('GrasshopperHiddenEditors', 'Count') == 1, 60)
        yield 5  # its editor loads; then a solution to follow the definition's wiring from
        doc.NewSolution(False)
        yield 2
        lt.check('connected: no error', str(double.RuntimeMessageLevel), 'Blank')

        # Ctrl+drag the wire away: the input loses its source, the definition recomputes.
        double.Params.Input[0].RemoveAllSources()
        double.Params.Input[0].ExpireSolution(False)
        doc.NewSolution(False)
        yield 3
        lt.check('disconnected: the error doesn\'t pause', len(paused()), 0)
        lt.check('the component shows it', str(double.RuntimeMessageLevel), 'Error')
        lt.check('V' + chr(0xB0) + 'Log says why', 'connected or disconnected' in list(lt.static_get('UserLog', 'Entries'))[-1].Text or
                 any('connected or disconnected' in e.Text for e in list(lt.static_get('UserLog', 'Entries'))[-5:]), True)

        # The same error, recomputed without a wiring change: paused there (Always debug).
        double.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('a plain recompute: the error pauses', len(controls), 1)
        if controls:
            lt.check('at the error', str(controls[0].Results.CurrentThread.CurrentFrame.Event), 'Exception')
            controls[0].Stop()
            yield 3
        doc.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)


lt.run('t_ghrewire', test)
