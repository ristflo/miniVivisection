#! python 3
# Review 2026-10-15, E24: the Python lessons run plainly (RunPythonScript, Stop and record) and
# do what their text says -- an error recorded where the lesson is about one, a run to the end
# otherwise. Not run: 13 (needs Claude), 15 (a deadlock by design).
import json
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

LESSONS = os.path.join(lt.ROOT, r'examples')
SKIPPED = ('13', '15')
# The lesson's stated bug when run plainly: an error at its line, else nothing (a run to the end)
ERRORS = {'03': 'error', '17': 'error'}


def test():
    yield 2
    old = lt.static_call('MiniVivisectionSettings', 'GetText', 'RunCommand')
    lt.static_call('MiniVivisectionSettings', 'Set', 'RunCommand', 'Stop and record')
    record_path = lt.static_get('StoppedRuns', 'RecordPath')
    try:
        names = sorted(f for f in os.listdir(LESSONS) if f[:2].isdigit() and f.endswith('.py') and '_tools' not in f)
        lt.check('17 lessons', len(names), 17)
        for name in names:
            number = name[:2]
            if number in SKIPPED:
                lt.note('%s: not run' % name)
                continue
            if os.path.exists(record_path):
                os.remove(record_path)
            ran = Rhino.RhinoApp.RunScript('_-RunPythonScript "%s"' % os.path.join(LESSONS, name), False)
            yield 2
            record = None
            if os.path.exists(record_path):
                with open(record_path, encoding='utf-8') as f:
                    record = json.load(f)
            kind = record.get('Kind') if record else None
            lt.check('%s: %s' % (name, 'an error recorded' if number in ERRORS else 'ran to its end'), (ran, kind), (True, ERRORS.get(number)))
            if record:
                lt.note('%s: %s at line %s: %s' % (name, kind, record.get('Line'), str(record.get('Error'))[:120]))
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'RunCommand', old)


lt.run('t_lessons', test)
