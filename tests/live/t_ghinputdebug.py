#! python 3
# Debugging a script component's script input: with its script (a file) open in the Script
# Editor, Always debug runs it in the debugger -- a breakpoint there pauses, the values are the
# component's run's; the menu's "Debug the script input" pauses at the first line; a Panel's
# script changed after it was opened runs plainly (V°Log says so).
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def editor_code(path):
    import System
    access = lt.plugin_type('ScriptEditorAccess')
    for window in lt.editors():
        code = System.Activator.CreateInstance(access, [window]).Code
        if code is not None and code.Uri is not None and code.Uri.IsFile and code.Uri.LocalPath.lower() == path.lower():
            return code
    return None


def log_lines(word, count=8):
    path = os.path.join(os.environ.get('TEMP', '.'), 'rhinovivisection', 'minivivisection', 'minivivisection.log')
    try:
        with open(path, encoding='utf-8', errors='replace') as f:
            return [l.rstrip()[:220] for l in f if word in l][-count:]
    except OSError:
        return []


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
    from Rhino.Runtime.Code.Execution.Debugging import CodeReferenceBreakpoint
    import make_gh_examples as mk
    inputs = lt.plugin_type('GrasshopperInputScripts')
    hook = lt.hook()
    paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
    try:
        own = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'input_debug.py')
        os.makedirs(os.path.dirname(own), exist_ok=True)
        with open(own, 'w', encoding='utf-8') as f:
            f.write('#! python 3\ny = x * 10\na = y + 1\n')
        doc = GH_Document()
        x = mk._place(mk.persistent(Param_Integer(), 'x', {(0,): [GH_Integer(4)]}), 20, 120)
        run = mk._place(mk.script('Run', 'Run', '#! python 3\na = 0\n', [('x', GH_ParamAccess.item, 'int')], ['a']), 300, 80)
        run.UsingScriptInputParam = True
        source = mk._place(mk.panel(own, 20, 20, width=260, height=40), 20, 20)
        for obj in (x, run, source):
            doc.AddObject(obj, False)
        names = [p.Name for p in run.Params.Input]
        run.Params.Input[names.index('script')].AddSource(source)
        script_input = run.Params.Input[names.index('script')]
        as_path = lambda on: script_input.GetType().GetProperty('TreatInputAsPath', lt.ANY).SetValue(script_input, on)
        as_path(True)  # the Panel holds the file's path
        run.Params.Input[names.index('x')].AddSource(x)
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        yield 3
        result = lambda: [g.Value for g in run.Params.Output[[p.Name for p in run.Params.Output].index('a')].VolatileData.AllData(True)]
        lt.check('the file\'s script runs: a = x * 10 + 1', result(), [41])

        # Open it; Always debug; a breakpoint on line 3 in the Script Editor pauses the run.
        inputs.GetMethod('Open', lt.ANY).Invoke(None, [run, None, None])
        yield lt.wait_for(lambda: editor_code(own) is not None and editor_code(own).HasDebugControls, 60)
        yield 3
        code = editor_code(own)
        lt.check('the Script Editor\'s code for it has a debugger', code is not None and code.HasDebugControls, True)
        breakpoint = CodeReferenceBreakpoint(code, 3)
        code.DebugControls.Breakpoints.Add(breakpoint)
        lt.static_call('GrasshopperInputScripts', 'DebugOnce', run)  # opt-in: from the menu
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('Debug the script input: the breakpoint in the script input pauses', len(controls), 1)
        lt.note('log: ' + ' | '.join(log_lines('script input')))
        lt.note('breakpoints: %r' % [str(b.Reference.Uri) + ':' + str(b.Reference.Position.LineNumber) for b in code.DebugControls.Breakpoints])
        lt.note('solution: %s, component: %s, messages: %r' % (doc.SolutionState, run.Phase, [str(m) for m in run.RuntimeMessages(Grasshopper.Kernel.GH_RuntimeMessageLevel.Error)]))
        if controls:
            frame = controls[0].Results.CurrentThread.CurrentFrame
            lt.check('at line 3 of the file', (frame.Reference.Position.LineNumber, frame.Reference.Uri.LocalPath.lower()), (3, own.lower()))
            lt.check('the run\'s values: x = 4, y = 40', lt.static_call('PythonBridge', 'Exec', 'print(x, y)', True).strip(), '4 40')
            controls[0].Continue()
            yield lt.wait_for(lambda: len(paused()) == 0, 20)
            yield 2
            lt.check('continued: the component\'s result', result(), [41])
        code.DebugControls.Breakpoints.Remove(breakpoint)

        # The menu's "Debug the script input" (Stop and record otherwise): paused at its first line.
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Stop and record')
        inputs.GetMethod('DebugOnce', lt.ANY).Invoke(None, [run])
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('Debug the script input: paused', len(controls), 1)
        if controls:
            line = controls[0].Results.CurrentThread.CurrentFrame.Reference.Position.LineNumber
            lt.check('at its first line', line in (1, 2), True)
            frame = controls[0].Results.CurrentThread.CurrentFrame
            lt.note('the pause: %s line %s (the editor\'s code: %s)' % (frame.Reference.Uri, line, code.Uri))
            controls[0].Continue()
            yield lt.wait_for(lambda: len(paused()) == 0, 20)

        # A Panel's script, opened, then changed in the Panel: runs plainly, said in V°Log.
        as_path(False)
        source.UserText = '#! python 3\na = x + 1\n'
        source.ExpireSolution(True)
        yield 2
        inputs.GetMethod('Open', lt.ANY).Invoke(None, [run, None, None])
        yield 4
        source.UserText = '#! python 3\na = x + 2\n'
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
        source.ExpireSolution(True)
        yield 3
        lt.check('changed in the Panel since: runs plainly, a = x + 2', (len(paused()), result()), (0, [6]))
        entries = [e.Text for e in list(lt.static_get('UserLog', 'Entries'))[-6:]]
        lt.check('V' + chr(0xB0) + 'Log says why', any('changed in the Panel since it was opened' in e for e in entries), True)
        doc.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)


lt.run('t_ghinputdebug', test)
