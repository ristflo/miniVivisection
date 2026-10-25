#! python 3
# Ctrl+Alt+B on a hanging script component while another debugger is busy: the component,
# running plainly on Rhino's UI thread, is stopped first -- it's what holds Rhino. (Before the
# fix the hotkey asked the other debugger for a pause, and Rhino froze: in a user's session
# that debugger was one a failed "Debug the script input" run had left "debugging"; that run
# works now, so here the busy debugger is a Script Editor's async debug run, still running.)
import os
import sys
import tempfile
import threading
import time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino
from Rhino.Runtime.Code.Execution.Debugging import DebugControlsState

BUSY = '''#! python 3
# async:true
import time
for i in range(6000):
    time.sleep(0.01)
'''

HANG = '''#! python 3
import time
while True:
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
    hook = lt.hook()
    debugging = lambda: list(hook.GetType().GetMethod('ControlsIn', lt.ANY).Invoke(hook, [DebugControlsState.Debugging]))
    path = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'busy_async.py')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(BUSY)
    try:
        # A debugger busy elsewhere: an async script running in the Script Editor's debugger
        Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_On "%s"' % path, False)
        yield lt.wait_for(lambda: len(debugging()) > 0, 60)
        yield 2
        busy = debugging()
        lt.check('another debugger is busy (running, not paused)', len(busy) == 1 and str(busy[0].State) == 'Debugging', True)

        doc = GH_Document()
        run = mk._place(mk.script('Hang', 'Hang', '#! python 3\na = 1\n', [], ['a']), 300, 80)
        doc.AddObject(run, False)
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        yield 2

        # The component hangs; Ctrl+Alt+B (the hotkey's path) once it runs
        def press():
            for _ in range(120):
                if lt.static_get('GrasshopperRuns', 's_running') is not None:
                    time.sleep(1.5)
                    lt.progress('Ctrl+Alt+B')
                    lt.static_call('BreakHotkey', 'Break')
                    return
                time.sleep(0.25)
            lt.progress('the component never ran')

        threading.Thread(target=press, daemon=True).start()
        run.GetType().GetMethod('SetSource').Invoke(run, [HANG])
        run.ExpireSolution(True)  # hangs here until the stop -- or for good, as before the fix
        yield 3
        remarks = list(run.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
        lt.note('remark: ' + (remarks[0] if remarks else '(none)'))
        lt.check('Ctrl+Alt+B stopped the hanging component, the other debugger busy or not', any('stopped by' in r for r in remarks), True)
        lt.check('the other run wasn\'t paused', [str(c.State) for c in debugging()], ['Debugging'])
        for box in [c for c in Grasshopper.Instances.ActiveCanvas.Controls if c.GetType().Name == 'CodeBox']:
            box.GetType().GetMethod('Close', lt.ANY).Invoke(box, [False])
        doc.Enabled = False
        for controls in debugging():
            controls.Stop()
        yield 2
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)


lt.run('t_ghbreakstale', test)
