#! python 3
# Batch 9 (review 2026-10-10: M3): Grasshopper's pause guard. While a script is paused in a
# solution, ExpireSolution calls are held back and done once the solution is over: each
# object once, keeping whether it asked to recompute, and one new solution per document --
# not one per object. (The pause is stood in for: AnyPaused says yes during the solution.)
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

replays = []


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None)


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import clr
    clr.AddReference('Grasshopper')
    import Grasshopper
    globals()['clr'] = clr
    from Grasshopper.Kernel import GH_Document
    from Grasshopper.Kernel.Parameters import Param_Number
    from Grasshopper.Kernel.Types import GH_Number
    from Grasshopper.Kernel.Data import GH_Path
    p = Param_Number()
    p.PersistentData.Append(GH_Number(1), GH_Path(0))
    q = Param_Number()
    q.PersistentData.Append(GH_Number(2), GH_Path(0))
    source = ('#! python 3\n'
              'doc = ghenv.Component.OnPingDocument()\n'
              'p = doc.FindObject(System.Guid("%s"), True)\n'
              'q = doc.FindObject(System.Guid("%s"), True)\n'
              'import System\n'
              'for _ in range(3):\n'
              '    p.ExpireSolution(True)\n'
              'q.ExpireSolution(False)\n') % (p.InstanceGuid, q.InstanceGuid)
    source = source.replace("doc = ghenv", "import System\ndoc = ghenv", 1)
    py3 = lt.assembly('RhinoCodePluginGH').GetType('RhinoCodePluginGH.Components.Python3Component')
    create = [m for m in py3.GetMethods() if m.Name == 'Create' and len(m.GetParameters()) == 4
              and m.GetParameters()[1].ParameterType.FullName == 'System.String'][0]
    component = create.Invoke(None, ['expirer', source, None, False])
    doc = GH_Document()
    doc.Enabled = False  # no solution before the stand-in pause is in place
    for obj in (p, q, component):
        doc.AddObject(obj, False)
    Grasshopper.Instances.DocumentServer.AddDocument(doc)

    guard = lt.plugin_type('GrasshopperPauseGuard')
    any_paused = guard.GetProperty('AnyPaused', lt.ANY)
    real = any_paused.GetValue(None)
    replayed = guard.GetEvent('Replayed', lt.ANY)
    handler = System.Action[int, int](lambda objects, documents: replays.append((objects, documents)))
    replayed.GetAddMethod(True).Invoke(None, [handler])
    try:
        # "a script is paused in this solution": a .NET delegate (no Python inside the solution's callbacks)
        always = System.Delegate.CreateDelegate(clr.GetClrType(System.Func[bool]), clr.GetClrType(System.Environment).GetProperty('Is64BitProcess').GetGetMethod())
        any_paused.SetValue(None, always)
        lt.static_call('Log', 'Configure', lt.plugin_type('LogLevel').GetField('Info').GetValue(None), 'Breakpoints=Trace')
        doc.Enabled = True
        lt.note('AnyPaused now: %s' % any_paused.GetValue(None).Invoke())
        doc.NewSolution(False)
        lt.check('the script component ran', component.RuntimeMessageLevel.ToString() != 'Error', True)
        lt.note('messages: ' + '; '.join(component.RuntimeMessages(Grasshopper.Kernel.GH_RuntimeMessageLevel.Error)))
    finally:
        any_paused.SetValue(None, real)
    yield lt.wait_for(lambda: len(replays) > 0, 15)
    lt.check('replayed once: 2 objects (p three times, q once), 1 new solution', replays[:1], [(2, 1)])
    replayed.GetRemoveMethod(True).Invoke(None, [handler])
    doc.Enabled = False


lt.run('t_pauseguard', test)
