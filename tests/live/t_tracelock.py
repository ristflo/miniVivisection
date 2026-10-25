#! python 3
# H1 (review 2026-10-10): a script paused on a thread of its own holds RhinoCode's shared
# trace lock; another debugger attaching on Rhino's UI thread must wait with the message
# loop going (TraceLockGuard), so the paused script can still be continued -- not freeze.
import os
import sys
import time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino
import Eto.Forms
from Rhino.Runtime.Code.Execution.Debugging import DebugControls, DebugAction, DebugControlsState


class StandIn(DebugControls):
    __namespace__ = 'MiniVivisectionLiveTests'

    def Pause(self):
        return DebugAction.Continue

    def Proceed(self, action):
        pass


def controls_in(state):
    hook = lt.hook()
    method = hook.GetType().GetMethod('ControlsIn', lt.ANY)
    return list(method.Invoke(hook, [state]))


def test():
    stop_file = os.path.join(lt.RESULT_DIR, 'tracelock.stop')
    if os.path.exists(stop_file):
        os.remove(stop_file)
    script = lt.write_file('tracelock_async.py',
                           '#! python 3\n# async:true\nimport os, time\ni = 0\n'
                           'while not os.path.exists(%r):\n    i += 1\n    time.sleep(0.002)\n' % stop_file)
    yield 2
    lt.note('starting the async script in the debugger')
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_On "%s"' % script, False)
    debugging = lambda: len(controls_in(DebugControlsState.Debugging)) > 0
    yield lt.wait_for(debugging, 60)
    running = controls_in(DebugControlsState.Debugging)
    if not lt.check('the async script runs in the debugger', len(running), 1):
        return
    controls = running[0]
    yield 1
    lt.note('Break now')
    lt.static_call('Threads', 'RequestBreak', controls, System.Int64(0))
    yield lt.wait_for(lambda: controls.State == DebugControlsState.Paused, 20)
    lt.check('Break now paused it', str(controls.State), 'Paused')
    hook = lt.hook()
    on_ui = hook.GetType().GetMethod('IsPausedThread', lt.ANY).Invoke(hook, [controls])
    lt.check('paused on a thread of its own', on_ui, False)

    # The user continues the paused script 2 s from now (a timer: the step below waits).
    continuer = Eto.Forms.UITimer()
    continuer.Interval = 2.0

    def go_on(sender, e):
        continuer.Stop()
        controls.Continue()

    continuer.Elapsed += go_on
    continuer.Start()
    waits = lt.static_get('TraceLockGuard', 'Waits')
    started = time.time()
    other = StandIn()
    lt.note('another debugger attaches on the UI thread')
    other.Attach(System.Guid.NewGuid())  # another debugger starting on the UI thread
    elapsed = time.time() - started
    other.Detach()
    lt.check('the UI thread waited for the other pause (with its message loop going)', lt.static_get('TraceLockGuard', 'Waits'), waits + 1)
    lt.check('and went on once it was continued (s)', 1.5 < elapsed < 15, True)
    lt.note('waited %.1f s' % elapsed)
    yield 1
    lt.check('the async script runs again', str(controls.State), 'Debugging')
    open(stop_file, 'w').close()
    yield lt.wait_for(lambda: len(controls_in(DebugControlsState.Debugging)) == 0, 20)
    lt.check('and ended', len(controls_in(DebugControlsState.Debugging)), 0)


lt.run('t_tracelock', test)
