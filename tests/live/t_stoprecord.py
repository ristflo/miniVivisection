#! python 3
# Batch 6 (review 2026-10-10: M1, M2, L2): RunPythonScript, "Stop and record". A script that
# loops on Rhino's UI thread is stopped by Break now from another thread (the hotkey's): it
# must end with a "stopped" record at its loop's line -- recorded before the interrupt, so
# never as an "error: KeyboardInterrupt".
import json
import os
import sys
import threading
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino


def setting(key):
    return lt.static_call('MiniVivisectionSettings', 'GetText', key)


def test():
    yield 2
    old = setting('RunCommand')
    lt.static_call('MiniVivisectionSettings', 'Set', 'RunCommand', 'Stop and record')
    try:
        record_path = lt.static_get('StoppedRuns', 'RecordPath')
        if os.path.exists(record_path):
            os.remove(record_path)
        script = lt.write_file('stop_me.py', '#! python 3\nimport time\ni = 0\nwhile True:\n    i += 1\n    time.sleep(0.001)\n')
        # Break now 3 s after the start, from a thread of its own (as the hotkey's).
        threading.Timer(3.0, lambda: lt.static_call('StoppedRuns', 'Stop')).start()
        lt.note('RunPythonScript runs a script that loops on the UI thread')
        Rhino.RhinoApp.RunScript('_-RunPythonScript "%s"' % script, False)  # returns when it ended
        lt.note('the script ended')
        yield 2
        lt.check('a record was written', os.path.exists(record_path), True)
        if os.path.exists(record_path):
            with open(record_path, encoding='utf-8') as f:
                record = json.load(f)
            lt.check('as stopped (not an error)', record.get('Kind'), 'stopped')
            lt.check('in the loop', record.get('Line') in (4, 5, 6), True)
            lt.check('the script file', os.path.basename(record.get('File') or ''), 'stop_me.py')
            lt.check('with its values', any(v[0] == 'i' for v in record.get('Values') or []), True)
        lt.check('no RunPythonScript run left', lt.static_get('StoppedRuns', 'Active'), False)
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'RunCommand', old)
        lt.check('the setting is restored', setting('RunCommand'), old)


lt.run('t_stoprecord', test)
