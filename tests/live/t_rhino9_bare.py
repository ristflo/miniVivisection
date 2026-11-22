#! python 3
# Rhino 9 without miniVivisection: a debug run paused at an error -- how long the pause lasts
# and what the debugger's state is (the reference for t_rhino9).
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino
import Eto.Forms

lt.NO_PLUGIN = True
SCRIPT = '''#! python 3
x = 1
raise RuntimeError('paused here')
'''


def test():
    yield 2
    lt.check('miniVivisection not loaded', lt.assembly('miniVivisection') is None, True)
    path = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'rhino9_bare.py')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(SCRIPT)
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Open "%s"' % path, False)
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 120)
    yield 5
    model = lt.editors()[0].DataContext
    codes = lt.member(model, 'Codes')
    debug = lt.member(lt.member(model, 'Commands'), 'CodeDebugCommand')
    m = sorted([m for m in debug.GetType().GetMethods(lt.ANY) if m.Name == 'Execute'], key=lambda m: len(m.GetParameters()))[0]
    args = [None] * len(m.GetParameters())
    Eto.Forms.Application.Instance.AsyncInvoke(lambda: m.Invoke(debug, args))
    last = None
    seen_paused = False
    for tick in range(100):
        yield 0.2
        now = (str(codes.DebugControlsState), str(codes.IsRunning), str(Rhino.RhinoApp.CommandPrompt))
        seen_paused = seen_paused or now[0] == 'Paused'
        if now != last:
            lt.note('%5.1f s: debugger %s, running %s, prompt %r' % (tick * 0.2, now[0], now[1], now[2]))
            last = now
    history = str(Rhino.RhinoApp.CommandHistoryWindowText or '')
    lt.note('command history (end): ' + history[-1500:])
    lt.check('saw the debugger paused', seen_paused, True)


lt.run('t_rhino9_bare', test)
