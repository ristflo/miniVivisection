#! python 3
# A script component running the script of its "script" input: from a Panel's text it opens in
# the Script Editor as a working file, and a save goes back into the Panel; from a file's path
# (a Panel with the path) the file itself opens, and a save recomputes the component; after
# Ctrl+Alt+B stopped a hanging one, its script opens by itself -- a Panel's in the Panel, at the
# line, with the explanation.
import os
import sys
import tempfile
import threading
import time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def newest_working_copy(folder):
    # This run's: the folder keeps earlier runs' working copies (other components, other names)
    copies = [os.path.join(folder, f) for f in os.listdir(folder) if f.startswith('Doubling-')]
    return max(copies, key=os.path.getmtime)


def editor_shows(path):
    for window in lt.editors():
        access = lt.plugin_type('ScriptEditorAccess')
        import System
        code = System.Activator.CreateInstance(access, [window]).Code
        if code is not None and code.Uri is not None and code.Uri.IsFile and code.Uri.LocalPath.lower() == path.lower():
            return True
    return False


def test():
    yield 2
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Stop and record')
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess
    from Grasshopper.Kernel.Parameters import Param_Integer
    from Grasshopper.Kernel.Types import GH_Integer
    import make_gh_examples as mk
    inputs = lt.plugin_type('GrasshopperInputScripts')
    try:
        doc = GH_Document()
        x = mk._place(mk.persistent(Param_Integer(), 'x', {(0,): [GH_Integer(5)]}), 20, 120)
        run = mk._place(mk.script('Run', 'Run', '#! python 3\na = 0\n', [('x', GH_ParamAccess.item, 'int')], ['a']), 300, 80)
        run.UsingScriptInputParam = True
        source = mk._place(mk.panel('#! python 3\na = x * 2\n', 20, 20, width=200, height=60), 20, 20)
        source.NickName = 'Doubling'
        for obj in (x, run, source):
            doc.AddObject(obj, False)
        names = [p.Name for p in run.Params.Input]
        lt.note('inputs: %r' % names)
        run.Params.Input[names.index('script')].AddSource(source)
        run.Params.Input[names.index('x')].AddSource(x)
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        yield 3
        result = lambda: [g.Value for g in run.Params.Output[[p.Name for p in run.Params.Output].index('a')].VolatileData.AllData(True)]
        lt.check('the Panel\'s script runs: a = x * 2', result(), [10])

        # From the Panel: a working file, opened; a save goes back into the Panel.
        inputs.GetMethod('Open', lt.ANY).Invoke(None, [run, None, None])
        folder = lt.static_get('GrasshopperInputScripts', 'Folder')
        working = newest_working_copy(folder)
        yield lt.wait_for(lambda: editor_shows(working), 60)
        lt.check('the Panel\'s script open in the Script Editor', editor_shows(working), True)
        with open(working, 'w', encoding='utf-8') as f:
            f.write('#! python 3\na = x * 3\n')  # as the editor's Save
        yield 3
        lt.check('saved: back in the Panel', source.UserText, '#! python 3\na = x * 3\n')
        lt.check('and the component recomputed: a = x * 3', result(), [15])

        # From a file's path (in a Panel): the file itself; a save recomputes.
        own = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'input_script.py')
        os.makedirs(os.path.dirname(own), exist_ok=True)
        with open(own, 'w', encoding='utf-8') as f:
            f.write('#! python 3\na = x + 100\n')
        as_path = lambda on: script_input.GetType().GetProperty('TreatInputAsPath', lt.ANY).SetValue(script_input, on)
        script_input = run.Params.Input[names.index('script')]
        as_path(True)  # the input's "Treat input as path"
        source.UserText = own
        source.ExpireSolution(True)
        yield 3
        lt.check('the file\'s script runs: a = x + 100', result(), [105])
        inputs.GetMethod('Open', lt.ANY).Invoke(None, [run, None, None])
        yield lt.wait_for(lambda: editor_shows(own), 60)
        lt.check('the file itself open in the Script Editor', editor_shows(own), True)
        with open(own, 'w', encoding='utf-8') as f:
            f.write('#! python 3\na = x + 200\n')
        yield 3
        lt.check('saved: the component recomputed, a = x + 200', result(), [205])
        lt.check('the Panel still has the path', source.UserText, own)

        # Ctrl+Alt+B (Stop and record) on a hanging Panel script: its script opens by itself.
        as_path(False)
        source.UserText = '#! python 3\nimport time\nwhile True:\n    time.sleep(0.01)\n'

        def press():
            for _ in range(60):
                running = lt.static_get('GrasshopperRuns', 's_running')
                if running is not None:
                    time.sleep(1.5)
                    lt.static_call('BreakHotkey', 'Break')
                    return
                time.sleep(0.25)

        # The Panel large on screen: after the stop it opens for editing right there
        import System
        canvas = Grasshopper.Instances.ActiveCanvas
        canvas.Viewport.Zoom = 2.0
        canvas.Viewport.MidPoint = System.Drawing.PointF(120, 50)
        canvas.Refresh()
        threading.Thread(target=press, daemon=True).start()
        source.ExpireSolution(True)  # hangs here until the stop
        yield 4
        hang = newest_working_copy(folder)
        boxes = lambda: [c for c in canvas.Controls if c.GetType().Name == 'CodeBox']
        yield lt.wait_for(lambda: len(boxes()) > 0, 30)
        static = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        stop = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.ScriptPanels').GetMethod('StopOf', static).Invoke(None, [source])
        title = (lt.member(boxes()[0], '_stopTitle') or '') if boxes() else ''
        lt.note('stopped at line %d: %s' % (stop.Item1, title))
        lt.check('stopped: the Panel open for editing at the line (3 or 4), the explanation over it',
                 (len(boxes()), stop.Item1 in (3, 4), title.startswith("Stopped in 'Run'")), (1, True, True))
        for box in boxes():
            box.GetType().GetMethod('Close', lt.ANY).Invoke(box, [False])
        with open(hang, encoding='utf-8') as f:
            lt.check('with the hanging script', 'while True' in f.read(), True)
        source.UserText = '#! python 3\na = 0\n'
        source.ExpireSolution(True)
        yield 2

        # A file named by the Panel (Input Is Path), hanging: Ctrl+Alt+B stops it, and that
        # file itself opens. (A path internalized on the input itself can't run in Grasshopper
        # 8.36: the script parameter's delayed cast runs only for data from a source --
        # BaseParam.UpdateVolatileData, DataType == remote -- and "Input Is Path" is offered
        # only then; the internalized string stays an "Invalid Script".)
        hanging = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'input_hang.py')
        with open(hanging, 'w', encoding='utf-8') as f:
            f.write('#! python 3\nimport time\nwhile True:\n    time.sleep(0.01)\n')
        as_path(True)
        source.UserText = hanging
        threading.Thread(target=press, daemon=True).start()
        source.ExpireSolution(True)  # hangs here until the stop
        yield 4
        yield lt.wait_for(lambda: editor_shows(hanging), 30)
        lt.check('a file named by the Panel, stopped: that file open in the Script Editor', editor_shows(hanging), True)
        entries = [e.Text for e in list(lt.static_get('UserLog', 'Entries'))[-8:]]
        lt.check('no "nothing connected"', any('nothing connected' in e for e in entries), False)
        doc.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)


lt.run('t_ghinputscript', test)
