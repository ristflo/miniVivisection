#! python 3
# Smoke: the plug-in is loaded, and its freeze guards are in place.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt


def test():
    yield 2
    lt.check('plug-in loaded', lt.assembly('miniVivisection') is not None, True)
    lt.check('trace lock guard installed', lt.static_get('TraceLockGuard', 'HookProblem'), None)
    lt.check('FreePause installed or waiting for RhinoCode', lt.static_get('FreePause', 'HookProblem') in (None, 'not installed'), True)


lt.run('t_smoke', test)
