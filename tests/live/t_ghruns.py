#! python 3
# H4/M13 (review 2026-10-10): Grasshopper script components followed run by run. A Python 3
# component with more inputs than outputs (the old code asked the outputs' target path with
# an input index and threw), x a list on {0}, y a list on {0;1}; run 3 fails. Its failure must
# name the run's real data: x {0}[2], y {0;1}[1] (longest list: y repeats its last item).
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

SOURCE = '#! python 3\nif x == 3:\n    raise ValueError("bad item %s" % x)\n'


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    if asm is None:
        return False
    instances = asm.GetType('Grasshopper.Instances')
    return instances.GetProperty('DocumentEditor').GetValue(None) is not None and lt.assembly('RhinoCodePluginGH') is not None


def state_of(component):
    t = lt.plugin_type('GrasshopperRuns')
    return t.GetMethod('StateOf', lt.ANY).Invoke(None, [component])


def test():
    yield 2
    lt.note('opening Grasshopper')
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import clr
    clr.AddReference('Grasshopper')
    import Grasshopper
    from Grasshopper.Kernel import GH_Document
    from Grasshopper.Kernel.Parameters import Param_Number
    from Grasshopper.Kernel.Data import GH_Path
    from Grasshopper.Kernel.Types import GH_Number
    py3 = lt.assembly('RhinoCodePluginGH').GetType('RhinoCodePluginGH.Components.Python3Component')
    create = [m for m in py3.GetMethods() if m.Name == 'Create' and len(m.GetParameters()) == 4
              and m.GetParameters()[1].ParameterType.FullName == 'System.String'][0]
    component = create.Invoke(None, ['Py3 runs', SOURCE, None, False])
    lt.check('inputs x, y', [p.NickName for p in component.Params.Input], ['x', 'y'])
    for p in list(component.Params.Output)[1:]:
        component.Params.UnregisterOutputParameter(p)  # more inputs than outputs
    lt.check('one output', component.Params.Output.Count, 1)

    x = Param_Number()
    for v in (1, 2, 3, 4):
        x.PersistentData.Append(GH_Number(v), GH_Path(0))
    y = Param_Number()
    for v in (10, 20):
        y.PersistentData.Append(GH_Number(v), GH_Path(0, 1))
    doc = GH_Document()
    for obj in (x, y, component):
        doc.AddObject(obj, False)
    component.Params.Input[0].AddSource(x)
    component.Params.Input[1].AddSource(y)
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    Grasshopper.Instances.ActiveCanvas.Document = doc
    doc.Enabled = True
    lt.note('solving')
    doc.NewSolution(False)
    yield 3
    lt.check('the component ran 4 times', component.RunCount, 4)
    state = state_of(component)
    failures = list(lt.member(state, 'Failures'))
    lt.check('one failing run', len(failures), 1)
    if failures:
        run = lt.member(failures[0], 'Run')
        lt.check('run 3 failed', lt.member(run, 'Run'), 3)
        lt.check('its inputs, per input', lt.member(run, 'Inputs'), 'x {0}[2], y {0;1}[1]')
        lt.check('its item: the item input with the longest list (tells the runs apart)', lt.member(run, 'Item'), '{0}[2]')
        lt.check('data seen in one run only', dict(lt.member(state, 'RunsPerData'))['{0}[2]'], 1)
        lt.check('its line', lt.member(failures[0], 'Line'), 3)
    remarks = [m for m in component.RuntimeMessages(Grasshopper.Kernel.GH_RuntimeMessageLevel.Remark)]
    lt.check('a remark names the failing run', any('run 3 failed' in r for r in remarks), True)
    lt.note('remark: ' + (remarks[0] if remarks else '(none)'))

    # A solution with no runs (the required input emptied): the old failures are over.
    component.Params.Input[0].RemoveAllSources()
    doc.NewSolution(False)
    yield 2
    lt.check('no stale failures after a solution without runs', len(list(lt.member(state_of(component), 'Failures'))), 0)
    doc.Enabled = False
    Grasshopper.Instances.DocumentServer.RemoveDocument(doc)


lt.run('t_ghruns', test)
