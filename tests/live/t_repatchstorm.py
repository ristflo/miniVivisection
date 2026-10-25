#! python 3
# Review 2026-10-15, A2: Always debug's patch (GetBuildKind) is put back only when a run that
# must have called it didn't -- not for a component running its script input, nor a locked one
# (neither gets that far). Thirty recomputes of each: no repair.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None)


def test():
    yield 2
    old = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess
    from Grasshopper.Kernel.Special import GH_NumberSlider
    import make_gh_examples as mk
    try:
        lt.check('Always debug installed', lt.static_get('GrasshopperDebug', 'IsInstalled'), True)
        doc = GH_Document()
        slider = mk._place(GH_NumberSlider(), 20, 120)
        slider.Slider.Minimum, slider.Slider.Maximum = System.Decimal(0), System.Decimal(100)
        slider.Slider.Value = System.Decimal(5)
        run = mk._place(mk.script('Run', 'Run', '#! python 3\na = 0\n', [('x', GH_ParamAccess.item, 'double')], ['a']), 300, 80)
        run.UsingScriptInputParam = True
        source = mk._place(mk.panel('#! python 3\na = x * 2\n', 20, 20, width=200, height=60), 20, 20)
        locked = mk._place(mk.script('Locked', 'Locked', '#! python 3\na = x\n', [('x', GH_ParamAccess.item, 'double')], ['a']), 300, 200)
        for obj in (slider, run, source, locked):
            doc.AddObject(obj, False)
        names = [p.Name for p in run.Params.Input]
        run.Params.Input[names.index('script')].AddSource(source)
        run.Params.Input[names.index('x')].AddSource(slider)
        locked.Params.Input[0].AddSource(slider)
        locked.Locked = True
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        yield 2
        repairs = lt.static_get('GrasshopperRuns', 'Repairs')
        for i in range(30):
            slider.Slider.Value = System.Decimal(5 + i)
            slider.ExpireSolution(True)
        yield 3
        lt.check('the script-input component ran', [g.Value for g in run.Params.Output[0].VolatileData.AllData(True)], [68.0])
        lt.check('thirty recomputes: no repair of the patch', lt.static_get('GrasshopperRuns', 'Repairs') - repairs, 0)
        doc.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old)


lt.run('t_repatchstorm', test)
