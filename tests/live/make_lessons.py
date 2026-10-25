#! python 3
# Not a test: writes the Grasshopper lessons and their overview (tools/make_gh_examples) in Rhino, nothing checked.
#     python tests/live/run_live.py make_lessons --desktop 2
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino


def ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def run():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(ready, 120)
    import make_gh_examples
    for path in [make_gh_examples.gh0(make_gh_examples.FOLDER)] + make_gh_examples.make():
        lt.note('wrote ' + path)


lt.run('make_lessons', run)
