#! python 3
# Lesson 17 (a runtime error), as its TRY THIS says: Debug stops at the error itself, the
# values there tell why; RunPythonScript in "Stop and record" records the error.
import json
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino
from Rhino.Runtime.Code.Execution.Debugging import DebugControlsState

LESSON = os.path.join(lt.ROOT, r'examples\17_runtime_error.py')
with open(LESSON, encoding='utf-8') as _f:  # the line of the error: acos in turn_angle(), wherever the text above puts it
    ERROR_LINE = next(n for n, line in enumerate(_f, 1) if 'math.acos(cosine)' in line and line.lstrip().startswith('return'))


def paused_controls():
    hook = lt.hook()
    return list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))


def test():
    yield 2
    old = lt.static_call('MiniVivisectionSettings', 'GetText', 'RunCommand')
    lt.static_call('MiniVivisectionSettings', 'Set', 'RunCommand', 'Stop and record')
    try:
        record_path = lt.static_get('StoppedRuns', 'RecordPath')
        if os.path.exists(record_path):
            os.remove(record_path)
        ran = Rhino.RhinoApp.RunScript('_-RunPythonScript "%s"' % LESSON, False)
        lt.note('RunPythonScript ran: %s' % ran)
        yield 2
        with open(record_path, encoding='utf-8') as f:
            record = json.load(f)
        lt.check('Stop and record: the error recorded at its line', (record.get('Kind'), record.get('Line'), record.get('Function')), ('error', ERROR_LINE, 'turn_angle'))
        lt.note('recorded: ' + str(record.get('Error')))
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'RunCommand', old)
    yield 2
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_On "%s"' % LESSON, False)
    yield lt.wait_for(lambda: len(paused_controls()) > 0, 60)
    controls = paused_controls()
    lt.check('Debug: the debugger stopped (no breakpoint)', len(controls), 1)
    if controls:
        frame = controls[0].Results.CurrentThread.CurrentFrame
        lt.check('at the error itself: line %d, in turn_angle()' % ERROR_LINE, (frame.Reference.Position.LineNumber, str(frame.Event)), (ERROR_LINE, 'Exception'))
        cosine = lt.static_call('PythonBridge', 'Exec', 'print(repr(cosine))', True).strip()
        lt.check('cosine: a hair over 1', cosine, '1.0000000000000002')
        controls[0].Stop()
        yield lt.wait_for(lambda: controls[0].State not in (DebugControlsState.Paused, DebugControlsState.Debugging), 20)

lt.run('t_lesson17', test)
