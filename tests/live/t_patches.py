#! python 3
# miniVivisection's Harmony patches survive .NET's tiered compilation, which recompiles a
# method once it has run often: a patch on code shared by a generic class's instantiations is
# lost then (found 2026-10-10 in Grasshopper). Guard: no patched method may be generic but
# Always debug's GetBuildKind (watched and put back). Endurance: 2500 traced lines -- the
# debugger's hottest patched methods recompiled -- and a breakpoint condition still decides.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino
from Rhino.Runtime.Code.Execution.Debugging import DebugControlsState

ALLOWED_GENERIC = {'GetBuildKind'}


def generic(method):
    if method.IsGenericMethod:
        return True
    t = method.DeclaringType
    while t is not None:
        if t.IsGenericType:
            return True
        t = t.DeclaringType
    return False


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None)


def paused_controls():
    hook = lt.hook()
    return list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_ScriptEditor', False)
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 60)
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    yield 5

    # The guard
    harmony = lt.assembly('0Harmony').GetType('HarmonyLib.Harmony')
    patched = list(harmony.GetMethod('GetAllPatchedMethods').Invoke(None, None))
    lt.note('%d patched methods' % len(patched))
    on_generic = sorted(set(m.Name for m in patched if generic(m)))
    lt.check('no patch on generic code but GetBuildKind (watched)', [n for n in on_generic if n not in ALLOWED_GENERIC], [])

    # The script component's menu, now added in Grasshopper's own (non-generic) menu code
    import clr
    clr.AddReference('System.Windows.Forms')
    from System.Windows.Forms import ContextMenuStrip
    py3 = lt.assembly('RhinoCodePluginGH').GetType('RhinoCodePluginGH.Components.Python3Component')
    create = [m for m in py3.GetMethods() if m.Name == 'Create' and len(m.GetParameters()) == 4
              and m.GetParameters()[1].ParameterType.FullName == 'System.String'][0]
    component = create.Invoke(None, ['menu test', '#! python 3\n', None, False])
    menu = ContextMenuStrip()
    component.AppendMenuItems(menu)  # the whole menu, as a right-click builds it
    texts = [item.Text for item in menu.Items]
    lt.check('the menu has miniVivisection\'s items', any(t.endswith(' Debug this component') and t.startswith('V') for t in texts), True)

    # Endurance: 2500 traced lines, then the condition
    script = lt.write_file('endurance.py', '#! python 3\ntotal = 0\nfor i in range(3000):\n    total += i\nprint(total)\n')
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_Off "%s"' % script, False)  # opens nothing: plain
    editor = lt.editors()[0]
    state = lt.member(editor, 'ActiveState')
    code = Rhino.Runtime.Code.RhinoCode.CreateCode(System.Uri(script))
    add = [m for m in state.GetType().GetMethods(lt.ANY) if m.Name == 'AddCode' and len(m.GetParameters()) == 2
           and m.GetParameters()[0].ParameterType.FullName == 'Rhino.Runtime.Code.Code'][0]
    add.Invoke(state, [code, System.Activator.CreateInstance(add.GetParameters()[1].ParameterType, True)])
    yield 3
    active = lt.member(lt.member(state, 'ActiveCode'), 'Code')
    from Rhino.Runtime.Code.Execution.Debugging import CodeReferenceBreakpoint
    active.DebugControls.Breakpoints.Add(CodeReferenceBreakpoint(active, 4))
    lt.static_call('BreakpointSpecs', 'SetOneShot', active.Uri, 4, 'i == 2500')
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_On "%s"' % script, False)
    yield lt.wait_for(lambda: len(paused_controls()) > 0, 120)
    controls = paused_controls()
    lt.check('paused after 2500 traced lines', len(controls), 1)
    if controls:
        i = lt.static_call('PythonBridge', 'Exec', 'print(i)', True).strip()
        lt.check('the condition still decides: i == 2500', i, '2500')
        controls[0].Stop()
    lt.static_call('BreakpointSpecs', 'EndOneShot', active.Uri, 4)


lt.run('t_patches', test)
