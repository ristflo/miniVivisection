#! python 3
# Low findings of the review of 2026-10-10. L23: a paused script whose Script Editor is
# closed for good is stopped (no one could continue it); "miniVivisection StopPaused" stops
# a paused script. L15: the Examples tab's lessons are working copies, not the shipped files.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino
from Rhino.Runtime.Code.Execution.Debugging import DebugControlsState


def controls_in(state):
    hook = lt.hook()
    return list(hook.GetType().GetMethod('ControlsIn', lt.ANY).Invoke(hook, [state]))


def start_and_pause(script):
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_On "%s"' % script, False)


def test():
    yield 2
    stop_file = os.path.join(lt.RESULT_DIR, 'lowfixes.stop')
    if os.path.exists(stop_file):
        os.remove(stop_file)
    script = lt.write_file('lowfixes_async.py',
                           '#! python 3\n# async:true\nimport os, time\ni = 0\n'
                           'while not os.path.exists(%r):\n    i += 1\n    time.sleep(0.002)\n' % stop_file)

    # StopPaused
    start_and_pause(script)
    yield lt.wait_for(lambda: len(controls_in(DebugControlsState.Debugging)) > 0, 60)
    controls = controls_in(DebugControlsState.Debugging)[0]
    yield 1
    lt.static_call('Threads', 'RequestBreak', controls, System.Int64(0))
    yield lt.wait_for(lambda: controls.State == DebugControlsState.Paused, 20)
    lt.check('paused', str(controls.State), 'Paused')
    Rhino.RhinoApp.RunScript('_-miniVivisection _StopPaused', False)
    yield lt.wait_for(lambda: controls.State not in (DebugControlsState.Paused, DebugControlsState.Debugging), 20)
    lt.check('StopPaused stopped it', controls.State not in (DebugControlsState.Paused, DebugControlsState.Debugging), True)

    # L15: the lessons
    working = lt.static_get('EditorExamples', 'WorkingFolder')
    lt.check('the lessons are working copies', os.path.exists(os.path.join(working, '01_watch_geometry.py')), True)
    lt.check('in the user\'s folder, not next to the plug-in', 'AppData' in working and 'Local' in working, True)

    # L23: paused again, then its editor closed for good
    yield 2
    start_and_pause(script)
    yield lt.wait_for(lambda: len(controls_in(DebugControlsState.Debugging)) > 0, 60)
    controls = controls_in(DebugControlsState.Debugging)[0]
    yield 1
    lt.static_call('Threads', 'RequestBreak', controls, System.Int64(0))
    yield lt.wait_for(lambda: controls.State == DebugControlsState.Paused, 20)
    lt.check('paused again', str(controls.State), 'Paused')
    editor = lt.hook().GetType().GetMethod('EditorOf', lt.ANY).Invoke(lt.hook(), [controls])
    hide = editor.GetType().GetProperty('HideOnClose', lt.ANY)
    if hide is not None and hide.CanWrite:
        hide.SetValue(editor, False)
    editor.Close()
    yield lt.wait_for(lambda: controls.State not in (DebugControlsState.Paused, DebugControlsState.Debugging), 20)
    lt.check('its editor closed: the paused script is stopped, not left waiting', controls.State not in (DebugControlsState.Paused, DebugControlsState.Debugging), True)
    open(stop_file, 'w').close()


lt.run('t_lowfixes', test)
