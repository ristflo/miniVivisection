#! python 3
# Batch 11 (review 2026-10-10: M6, M7): ScriptEditor Run with Debug, the "Run and Debug
# buttons" setting off. Another script (X) is the editor's active tab; Run Debug Y must run Y
# -- not X, which was active while Y's tab was still opening -- in the debugger, without a
# breakpoint.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino


def marker(name):
    return os.path.join(lt.RESULT_DIR, name + '.ran')


def script(name):
    return lt.write_file(name + '.py', '#! python 3\nimport time\nopen(%r, "w").close()\ntime.sleep(1)\n' % marker(name))


def test():
    yield 2
    for name in ('x_script', 'y_script'):
        if os.path.exists(marker(name)):
            os.remove(marker(name))
    x, y = script('x_script'), script('y_script')
    old = lt.static_call('MiniVivisectionSettings', 'GetBool', 'DebugRunButton')
    lt.static_call('MiniVivisectionSettings', 'Set', 'DebugRunButton', False)
    try:
        Rhino.RhinoApp.RunScript('_ScriptEditor', False)
        yield lt.wait_for(lambda: len(lt.editors()) > 0, 60)
        yield 3
        editor = lt.editors()[0]
        state = lt.member(editor, 'ActiveState')
        code = Rhino.Runtime.Code.RhinoCode.CreateCode(System.Uri(x))
        add = [m for m in state.GetType().GetMethods(lt.ANY) if m.Name == 'AddCode' and len(m.GetParameters()) == 2
               and m.GetParameters()[0].ParameterType.FullName == 'Rhino.Runtime.Code.Code'][0]
        add.Invoke(state, [code, System.Activator.CreateInstance(add.GetParameters()[1].ParameterType, True)])
        yield 3
        lt.note('X is the active tab; now ScriptEditor Run Debug Y')
        Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_On "%s"' % y, False)
        yield lt.wait_for(lambda: os.path.exists(marker('y_script')), 30)
        lt.check('Y ran (in the debugger, no breakpoint, buttons off)', os.path.exists(marker('y_script')), True)
        lt.check('X didn\'t run', os.path.exists(marker('x_script')), False)
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'DebugRunButton', old)


lt.run('t_rundebug', test)
