#! python 3
# Batch 13 (review 2026-10-10: M17): a closed Script Editor is let go -- the Run/Debug
# buttons' list and timer, the V Log page's handlers on the static user log.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino


def buttons():
    return lt.static_get('DebugRunButton', 's_all').Count


def log_handlers():
    added = lt.plugin_type('UserLog').GetField('Added', lt.ANY)
    handler = added.GetValue(None) if added is not None else None
    return 0 if handler is None else len(handler.GetInvocationList())


def test():
    yield 2
    before_handlers = log_handlers()
    Rhino.RhinoApp.RunScript('_ScriptEditor', False)
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 60)
    yield lt.wait_for(lambda: buttons() > 0, 30)
    lt.check('the editor got its Run/Debug buttons', buttons(), 1)
    yield 3
    with_editor = log_handlers()
    lt.note('user log handlers: %d before the editor, %d with it' % (before_handlers, with_editor))
    editor = lt.editors()[0]
    hide = editor.GetType().GetProperty('HideOnClose', lt.ANY)
    if hide is not None and hide.CanWrite:
        hide.SetValue(editor, False)  # closed for good, not hidden
    editor.Close()
    yield 3
    lt.check('closed: its buttons are let go', buttons(), 0)
    lt.check('and the buttons\' timer stopped', lt.static_get('DebugRunButton', 's_timer'), None)
    lt.check('the V Log page\'s handlers are gone', log_handlers() <= before_handlers, True)


lt.run('t_lifetime', test)
