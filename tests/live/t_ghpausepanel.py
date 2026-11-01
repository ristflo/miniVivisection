#! python 3
# A Grasshopper script paused at a breakpoint is shown where it is edited: its editor --
# minimized here first -- comes up, for a component's own script, a script input's file and a
# Panel's script (its working copy in the Script Editor); the Panel shows the Script Editor's
# breakpoints in its gutter, and while paused the line and "Paused at the breakpoint on line 3",
# gone when the run continues.
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


def editor_with(test):
    """The Script Editor window and its code whose code passes test(code), or (None, None)."""
    import System
    access = lt.plugin_type('ScriptEditorAccess')
    for window in lt.editors():
        code = System.Activator.CreateInstance(access, [window]).Code
        if code is not None and test(code):
            return window, code
    return None, None


def of_file(path):
    return lambda code: code.Uri is not None and code.Uri.IsFile and code.Uri.LocalPath.lower() == path.lower()


def shown_on(panel):
    """The breakpoints and the pause the Panel shows."""
    panels = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.ScriptPanels')
    shown = panels.GetMethod('DebugOf', lt.ANY).Invoke(None, [panel])
    return list(shown.Item1), shown.Item2, shown.Item3


def test():
    yield 2
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Stop and record')
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    import Eto.Forms
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess
    from Grasshopper.Kernel.Parameters import Param_Integer
    from Grasshopper.Kernel.Types import GH_Integer
    from Rhino.Runtime.Code.Execution.Debugging import CodeReferenceBreakpoint
    import make_gh_examples as mk
    inputs = lt.plugin_type('GrasshopperInputScripts')
    hook = lt.hook()
    paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
    minimized = Eto.Forms.WindowState.Minimized

    def pause_at_breakpoint(window, code, line, start):
        """Minimizes the editor, sets a breakpoint, starts the run: the controls paused, or None."""
        window.WindowState = minimized
        breakpoint = CodeReferenceBreakpoint(code, line)
        code.DebugControls.Breakpoints.Add(breakpoint)
        start()
        return breakpoint

    try:
        doc = GH_Document()
        x = mk._place(mk.persistent(Param_Integer(), 'x', {(0,): [GH_Integer(4)]}), 20, 160)
        run = mk._place(mk.script('FromPanel', 'FromPanel', '#! python 3\na = 0\n', [('x', GH_ParamAccess.item, 'int')], ['a']), 340, 80)
        run.UsingScriptInputParam = True
        text = '#! python 3\ny = x * 10\na = y + 1\n'
        source = mk._place(mk.panel(text, 20, 20, width=260, height=110), 20, 20)
        own = mk._place(mk.script('Own', 'Own', '#! python 3\n# own script\nb = x * 2\nb = b + 1\n', [('x', GH_ParamAccess.item, 'int')], ['b']), 340, 240)
        for obj in (x, run, source, own):
            doc.AddObject(obj, False)
        names = [p.Name for p in run.Params.Input]
        run.Params.Input[names.index('script')].AddSource(source)
        run.Params.Input[names.index('x')].AddSource(x)
        own.Params.Input[[p.Name for p in own.Params.Input].index('x')].AddSource(x)
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        yield 3

        # 1. A Panel's script: its working copy opened in the Script Editor; a breakpoint there
        #    is shown in the Panel's gutter.
        inputs.GetMethod('Open', lt.ANY).Invoke(None, [run, None, None])
        yield lt.wait_for(lambda: editor_with(lambda c: c.Uri is not None and c.Uri.IsFile and 'gh-inputs' in c.Uri.LocalPath)[1] is not None, 60)
        yield 3
        window, code = editor_with(lambda c: c.Uri is not None and c.Uri.IsFile and 'gh-inputs' in c.Uri.LocalPath)
        lt.check('the Panel\'s working copy is open, with a debugger', code is not None and code.HasDebugControls, True)
        breakpoint = pause_at_breakpoint(window, code, 3, lambda: lt.static_call('GrasshopperInputScripts', 'DebugOnce', run))
        yield lt.wait_for(lambda: shown_on(source)[0] == [3], 10)
        lt.check('the Panel shows the breakpoint in its gutter', shown_on(source)[0], [3])
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('the breakpoint in the Panel\'s script pauses', len(controls), 1)
        yield lt.wait_for(lambda: shown_on(source)[1] == 3, 10)
        lt.check('the Panel shows the pause at line 3', shown_on(source)[1:], (3, 'Paused at the breakpoint on line 3'))
        lt.check('the Script Editor came up (not minimized)', window.WindowState != minimized and window.Visible, True)
        lt.check('a double-click on the paused Panel brings it up', lt.static_call('GrasshopperScriptPauses', 'Raise', source), True)
        if controls:
            controls[0].Continue()
            yield lt.wait_for(lambda: len(paused()) == 0, 20)
        yield lt.wait_for(lambda: shown_on(source)[1] == 0, 10)
        lt.check('continued: the Panel shows no pause', shown_on(source)[1], 0)
        code.DebugControls.Breakpoints.Remove(breakpoint)
        yield lt.wait_for(lambda: shown_on(source)[0] == [], 10)
        lt.check('the breakpoint removed: gone from the Panel', shown_on(source)[0], [])

        # 2. A script input's file: the Script Editor showing the file comes up.
        file = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'pause_file.py')
        os.makedirs(os.path.dirname(file), exist_ok=True)
        with open(file, 'w', encoding='utf-8') as f:
            f.write('#! python 3\ny = x * 10\na = y + 2\n')
        source.UserText = file
        script_input = run.Params.Input[names.index('script')]
        script_input.GetType().GetProperty('TreatInputAsPath', lt.ANY).SetValue(script_input, True)
        source.ExpireSolution(True)
        yield 3
        inputs.GetMethod('Open', lt.ANY).Invoke(None, [run, None, None])
        yield lt.wait_for(lambda: editor_with(of_file(file))[1] is not None, 60)
        yield 3
        window, code = editor_with(of_file(file))
        breakpoint = pause_at_breakpoint(window, code, 3, lambda: lt.static_call('GrasshopperInputScripts', 'DebugOnce', run))
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('the breakpoint in the file pauses', len(controls), 1)
        yield 1
        lt.check('the Script Editor with the file came up', window.WindowState != minimized and window.Visible, True)
        if controls:
            controls[0].Continue()
            yield lt.wait_for(lambda: len(paused()) == 0, 20)
        code.DebugControls.Breakpoints.Remove(breakpoint)

        # 3. A component's own script: its own editor comes up (Always debug).
        runs = lt.plugin_type('GrasshopperRuns')
        runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [own])
        yield lt.wait_for(lambda: runs.GetMethod('HasVisibleEditor', lt.ANY).Invoke(None, [own]), 60)
        yield 3
        code = lt.static_call('GrasshopperRuns', 'CodeOf', own)
        window = lt.static_call('GrasshopperRuns', 'EditorFor', code) if code is not None else None
        lt.check('the component\'s editor is open, with a debugger', window is not None and code.HasDebugControls, True)
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
        # Scheduled, not solved here: the pause would hold this step (the solution runs inside it)
        breakpoint = pause_at_breakpoint(window, code, 4, lambda: (own.ExpireSolution(False), doc.ScheduleSolution(50)))
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('the breakpoint in the component\'s own script pauses', len(controls), 1)
        yield 1
        lt.check('its editor came up', window.WindowState != minimized and window.Visible, True)
        if controls:
            controls[0].Continue()
            yield lt.wait_for(lambda: len(paused()) == 0, 20)
        code.DebugControls.Breakpoints.Remove(breakpoint)
        doc.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)


lt.run('t_ghpausepanel', test)
