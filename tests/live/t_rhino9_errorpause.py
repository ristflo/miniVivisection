#! python 3
# Rhino 9: an uncaught error pauses at its line, as in Rhino 8 (Rhino9ErrorPause) -- Rhino 9 by
# itself runs on and disconnects at the error. A debug run without a breakpoint (lesson 17's
# shape): paused at the exception in turn_angle(), its `cosine` readable in the paused frame,
# still paused a few seconds later (the editor didn't disconnect), the editor's Stop enabled,
# Variables shown (Code.CanDebug during the run); Stop ends it as stopped. A second run:
# Continue lets the error end the run (the Terminal shows it). A script that catches its own
# error doesn't pause. Run: python tests/live/run_live.py --rhino 9 t_rhino9_errorpause
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino
import Eto.Forms
from Rhino.Runtime.Code.Execution.Debugging import DebugControlsState

ERROR_SCRIPT = '''#! python 3
import math


def turn_angle(before, here, after):
    ax, ay = here[0] - before[0], here[1] - before[1]
    bx, by = after[0] - here[0], after[1] - here[1]
    cosine = (ax * bx + ay * by) / (math.sqrt(ax * ax + ay * ay) * math.sqrt(bx * bx + by * by))
    return math.degrees(math.acos(cosine))


pts = [(9.5, 7.0), (9.6, 7.2), (9.71, 7.42)]
print(turn_angle(pts[0], pts[1], pts[2]))
'''
ERROR_LINE = 9  # return math.degrees(math.acos(cosine))

CAUGHT_SCRIPT = '''#! python 3
try:
    1 / 0
except ZeroDivisionError:
    pass
print('caught')
'''


def paused_controls():
    hook = lt.hook()
    return list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))


def command(model, name):
    return lt.member(lt.member(model, 'Commands'), name)


def execute(cmd):
    """The editor's command, asynchronously: it runs the script on the UI thread, so the pause
    would come inside this step, where the test can't look."""
    m = sorted([m for m in cmd.GetType().GetMethods(lt.ANY) if m.Name == 'Execute'], key=lambda m: len(m.GetParameters()))[0]
    args = [None] * len(m.GetParameters())
    Eto.Forms.Application.Instance.AsyncInvoke(lambda: m.Invoke(cmd, args))


def open_script(path):
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Open "%s"' % path, False)


def test():
    yield 2
    lt.check('Rhino 9', Rhino.RhinoApp.Version.Major, 9)
    if lt.assembly('miniVivisection') is None:
        lt.check('plug-in loaded', False, True)
        return
    lt.static_call('MiniVivisectionSettings', 'Set', 'LogLevel', 'Debug')
    error_path = lt.write_file('r9_errorpause.py', ERROR_SCRIPT)
    caught_path = lt.write_file('r9_errorcaught.py', CAUGHT_SCRIPT)

    open_script(error_path)
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 120)
    yield 5
    window = lt.editors()[0]
    model = window.DataContext
    codes = lt.member(model, 'Codes')
    lt.note('hooks: ' + str(lt.static_get('Rhino9ErrorPause', 'HookProblem')))
    lt.check('Rhino9ErrorPause in place', lt.static_get('Rhino9ErrorPause', 'HookProblem'), None)

    # --- 1: the pause holds; Stop ---------------------------------------------------------------
    debug = command(model, 'CodeDebugCommand')
    lt.check('Debug enabled without a breakpoint (Run and Debug buttons)', bool(debug.CanExecute), True)
    execute(debug)
    yield lt.wait_for(lambda: len(paused_controls()) > 0, 60)
    controls = paused_controls()
    lt.check('paused at the uncaught error (no breakpoint)', len(controls), 1)
    if not controls:
        return
    c = controls[0]
    frame = c.Results.CurrentThread.CurrentFrame
    lt.check('at the error itself: line %d, an exception' % ERROR_LINE, (frame.Reference.Position.LineNumber, str(frame.Event)), (ERROR_LINE, 'Exception'))
    cosine = lt.static_call('PythonBridge', 'Exec', 'print(repr(cosine))', True).strip()
    lt.check('cosine readable in the paused frame', cosine, '1.0000000000000002')
    yield 3
    lt.check('still paused 3 s later (the editor didn\'t disconnect)', str(c.State), 'Paused')
    lt.check('the editor knows it\'s paused', str(codes.DebugControlsState), 'Paused')
    lt.check('the editor\'s Stop can be pressed', bool(command(model, 'CodeDebugStopCommand').CanExecute), True)
    lt.check('Variables shown without a breakpoint (Codes.CanDebug)', bool(codes.CanDebug), True)
    c.Stop()
    yield lt.wait_for(lambda: c.State not in (DebugControlsState.Paused, DebugControlsState.Debugging) and not codes.IsRunning, 20)
    lt.check('stopped', len(paused_controls()), 0)
    terminal = str(lt.member(lt.member(model, 'Terminal'), 'Text'))
    lt.check('Stop: no error written', 'ValueError' in terminal, False)
    yield 2
    lt.check('after the run: Variables follow the breakpoints again', bool(codes.CanDebug), False)

    # --- 2: Continue -> the error ends the run --------------------------------------------------
    execute(debug)
    yield lt.wait_for(lambda: len(paused_controls()) > 0, 60)
    controls = paused_controls()
    lt.check('paused again at the error', len(controls), 1)
    if controls:
        c = controls[0]
        before = len([e for e in list(lt.static_get('UserLog', 'Entries')) if 'Stopped at an exception' in str(e.Text)])
        c.Continue()
        yield lt.wait_for(lambda: not codes.IsRunning, 20)
        yield 2
        terminal = str(lt.member(lt.member(model, 'Terminal'), 'Text'))
        lt.note('Terminal after Continue: %r' % terminal[-300:])
        result = lt.member(lt.member(codes, 'Current'), 'LastResult')
        lt.note("the run's result: %s" % result)
        lt.check('Continue: the run ended with the error (a failed run)', bool(result.Failure), True)
        lt.check('not paused any more', len(paused_controls()), 0)
        after = len([e for e in list(lt.static_get('UserLog', 'Entries')) if 'Stopped at an exception' in str(e.Text)])
        lt.note('"Stopped at an exception" entries after Continue: %d (Rhino 9 pauses once more, post mortem)' % (after - before))

    # --- 3: an error caught in its own frame doesn't pause --------------------------------------
    open_script(caught_path)
    yield 4
    execute(debug)
    paused = False
    for _ in range(40):
        yield 0.2
        if paused_controls():
            paused = True
            break
    lt.check('an error caught in its frame: no pause', paused, False)
    for c in paused_controls():
        c.Stop()
    yield 2
    lt.note('UserLog: ' + ' | '.join(str(e.Text) for e in list(lt.static_get('UserLog', 'Entries'))[-10:]))


lt.run('t_rhino9_errorpause', test)
