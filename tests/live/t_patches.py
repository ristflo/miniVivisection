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
# Rhino 9 has nothing non-generic to hook for these (its editor's classes are generic): the
# Grasshopper editor's BeginEdit / HasVisibleEditor / CodeEditorForm.BeginEdit / ExecuteCode
# (GrasshopperHiddenEditors repairs them) and the editor's OnItemDebuggerPaused (Rhino9ErrorPause).
ALLOWED_GENERIC_RHINO9 = {'BeginEdit', 'get_HasVisibleEditor', 'ExecuteCode', 'OnItemDebuggerPaused'}


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
    allowed = ALLOWED_GENERIC | (ALLOWED_GENERIC_RHINO9 if Rhino.RhinoApp.Version.Major >= 9 else set())
    lt.check('no patch on generic code but the watched ones', [n for n in on_generic if n not in allowed], [])

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
    if Rhino.RhinoApp.Version.Major >= 9:
        # Rhino 9: the script opened in the editor, its breakpoint set there, the editor's Debug
        for step in endurance9(script):
            yield step
        return
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


def endurance9(script):
    import Eto.Forms
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Open "%s"' % script, False)
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 120)
    yield 4
    model = lt.editors()[0].DataContext
    text_editor = lt.member(lt.member(model, 'Codes'), 'Current').Editor
    text_editor.Goto(Rhino.Runtime.Code.Text.TextPosition(4, 1))
    yield 1
    # The editor reopens the last session's scripts with their breakpoints: one already on
    # line 4 would be toggled off -- then toggled on again.
    # A code view that isn't ready yet takes no toggle: tried again for a while, then the
    # breakpoint added to the code directly.
    count = lambda: len(list(text_editor.Code.DebugControls.Breakpoints))
    before = count()
    text_editor.ToggleBreakpoint()
    yield 1
    if count() < before:
        text_editor.ToggleBreakpoint()
        yield 1
    for _ in range(10):
        if count() > 0:
            break
        text_editor.Goto(Rhino.Runtime.Code.Text.TextPosition(4, 1))
        text_editor.ToggleBreakpoint()
        yield 2
    if count() == 0:
        from Rhino.Runtime.Code.Execution.Debugging import CodeReferenceBreakpoint
        text_editor.Code.DebugControls.Breakpoints.Add(CodeReferenceBreakpoint(text_editor.Code, 4))
        yield 1
    uri = text_editor.Code.Uri
    lt.static_call('BreakpointSpecs', 'SetOneShot', uri, 4, 'i == 2500')
    debug = lt.member(lt.member(model, 'Commands'), 'CodeDebugCommand')
    lt.progress('debugging %s: %d breakpoint(s) (before %d), Debug enabled: %s' % (uri, count(), before, debug.CanExecute))
    m = sorted([m for m in debug.GetType().GetMethods(lt.ANY) if m.Name == 'Execute'], key=lambda m: len(m.GetParameters()))[0]
    args = [None] * len(m.GetParameters())
    Eto.Forms.Application.Instance.AsyncInvoke(lambda: m.Invoke(debug, args))
    yield lt.wait_for(lambda: len(paused_controls()) > 0, 120)
    controls = paused_controls()
    lt.check('paused after 2500 traced lines', len(controls), 1)
    if controls:
        i = lt.static_call('PythonBridge', 'Exec', 'print(i)', True).strip()
        lt.check('the condition still decides: i == 2500', i, '2500')
        controls[0].Stop()
    lt.static_call('BreakpointSpecs', 'EndOneShot', uri, 4)


lt.run('t_patches', test)
