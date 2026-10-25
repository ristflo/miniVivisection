#! python 3
# Not a test: writes every example in Rhino, nothing checked --
#   examples/00_overview.3dm -- the overview and the Markdown text example, protected (tools/make_rhino_examples),
#   examples/grasshopper/gh0_overview.gh and the Grasshopper lessons (tools/make_gh_examples).
#     python tests/live/run_live.py make_examples --desktop 2
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
    import make_rhino_examples as rhino
    doc = Rhino.RhinoDoc.ActiveDoc
    rhino.make(doc)
    lt.note('wrote ' + rhino.PATH)
    yield 1
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(ready, 120)
    import make_gh_examples as gh
    for path in [gh.gh0(gh.FOLDER)] + gh.make():
        lt.note('wrote ' + path)


lt.run('make_examples', run)
