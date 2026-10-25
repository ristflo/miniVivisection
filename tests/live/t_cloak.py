#! python 3
# H5 (review 2026-10-10): RunPythonScript / ScriptEditor Run with Debug make the Script
# Editor invisible (DWM-cloaked) until the script pauses. When starting the debug run
# fails after the editor was made, the editor must not stay invisible.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System


def test():
    yield 2
    lt.check('no Script Editor open yet', len(lt.editors()), 0)
    lt.note('debug run of a script the editor can\'t take (AddCode fails after OpenEditor)')
    started = lt.static_call('ScriptRunCommand', 'DebugInEditor', System.Object(), 'live test')
    lt.check('the debug run reports the failure (the caller runs it plainly)', started, False)
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 30)
    yield 2
    editors = lt.editors()
    lt.check('the editor was made', len(editors), 1)
    for editor in editors:
        lt.check('not kept invisible', lt.static_call('ScriptRunCommand', 'IsKeptInvisible', editor), False)
        lt.check('not cloaked (DWM)', lt.is_cloaked(editor), False)
        lt.check('not minimized', str(editor.WindowState), 'Normal')
    yield 1
    lt.check('still not cloaked a second later (no timer cloaks it again)', all(not lt.is_cloaked(e) for e in lt.editors()), True)


lt.run('t_cloak', test)
