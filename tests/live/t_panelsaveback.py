#! python 3
# Review 2026-10-15, C5: a script input's Panel opened in the Script Editor, then changed in
# the Panel itself (edited in place): a save from the editor's stale copy is refused and said,
# and the Panel keeps its text; opened again, a save goes back -- with an undo step.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None)


def newest_working_copy(folder):
    files = [os.path.join(folder, f) for f in os.listdir(folder)] if os.path.isdir(folder) else []
    return max(files, key=os.path.getmtime) if files else None


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess
    from Grasshopper.Kernel.Parameters import Param_Integer
    from Grasshopper.Kernel.Types import GH_Integer
    import make_gh_examples as mk
    inputs = lt.plugin_type('GrasshopperInputScripts')
    doc = GH_Document()
    x = mk._place(mk.persistent(Param_Integer(), 'x', {(0,): [GH_Integer(5)]}), 20, 120)
    run = mk._place(mk.script('Run', 'Run', '#! python 3\na = 0\n', [('x', GH_ParamAccess.item, 'int')], ['a']), 300, 80)
    run.UsingScriptInputParam = True
    source = mk._place(mk.panel('#! python 3\na = x * 2\n', 20, 20, width=200, height=60), 20, 20)
    source.NickName = 'Doubling'
    for obj in (x, run, source):
        doc.AddObject(obj, False)
    names = [p.Name for p in run.Params.Input]
    run.Params.Input[names.index('script')].AddSource(source)
    run.Params.Input[names.index('x')].AddSource(x)
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    Grasshopper.Instances.ActiveCanvas.Document = doc
    doc.Enabled = True
    doc.NewSolution(False)
    yield 3
    result = lambda: [g.Value for g in run.Params.Output[[p.Name for p in run.Params.Output].index('a')].VolatileData.AllData(True)]
    lt.check('the Panel\'s script runs', result(), [10])
    try:
        # Opened in the editor: a working copy of the Panel's text
        inputs.GetMethod('Open', lt.ANY).Invoke(None, [run, None, None])
        folder = lt.static_get('GrasshopperInputScripts', 'Folder')
        yield 3
        working = newest_working_copy(folder)
        lt.check('a working copy', working is not None and working.endswith('.py'), True)
        # The Panel changed meanwhile (edited in place: Grasshopper's own way)
        source.RecordUndoEvent('Edit Panel')
        source.SetUserText('#! python 3\na = x * 5\n')
        source.ExpireSolution(True)
        yield 3
        lt.check('the Panel\'s new text runs', result(), [25])
        # A save from the stale copy: refused
        with open(working, 'w', encoding='utf-8') as f:
            f.write('#! python 3\na = x * 3\n')
        yield 3
        lt.check('the stale save is refused: the Panel keeps its text', source.UserText, '#! python 3\na = x * 5\n')
        entries = [e.Text for e in list(lt.static_get('UserLog', 'Entries'))[-6:]]
        lt.check('and said', any('changed since its script was opened' in e for e in entries), True)
        # Opened again: a save goes back, with an undo step
        inputs.GetMethod('Open', lt.ANY).Invoke(None, [run, None, None])
        yield 3
        undo_before = doc.UndoServer.UndoCount
        with open(working, 'w', encoding='utf-8') as f:
            f.write('#! python 3\na = x * 7\n')
        yield 3
        lt.check('the save goes back into the Panel', source.UserText, '#! python 3\na = x * 7\n')
        lt.check('and the component recomputed', result(), [35])
        lt.check('with an undo step', doc.UndoServer.UndoCount, undo_before + 1)
    finally:
        doc.Enabled = False


lt.run('t_panelsaveback', test)
