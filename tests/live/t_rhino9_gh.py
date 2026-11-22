#! python 3
# Rhino 9, Grasshopper: the GH hooks wait for Grasshopper ("waiting for Grasshopper"), then install
# when it loads -- runs followed through GH_Component.TrySolveInstance, the pause guard with its
# Escape-watcher postfix, the canvas, Always debug's GetBuildKind, hidden editors on
# ScriptEditor<TOwner> -- and the hidden-editor flow of t_ghhidden on Rhino 9's component editor
# (IScriptEditor.GetWindow()): GH 3's Halve gets an editor out of sight, Break now pauses it and
# brings it up, closed it's hidden again, opened by the user it comes up, Always debug off closes it.
# Run: python tests/live/run_live.py --rhino 9 t_rhino9_gh
import os
import sys
import threading
import time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

GH3 = os.path.join(lt.ROOT, r'examples\grasshopper\gh3_stop_a_hang.gh')
HOOKS = ['GrasshopperRuns', 'GrasshopperDebug', 'GrasshopperPauseGuard', 'GrasshopperPauseCanvas', 'GrasshopperHiddenEditors']


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def problems():
    return dict((name, lt.static_get(name, 'HookProblem')) for name in HOOKS)


def patched(method):
    harmony = lt.assembly('0Harmony').GetType('HarmonyLib.Harmony')
    info = harmony.GetMethod('GetPatchInfo').Invoke(None, [method])
    return info is not None and (info.Prefixes.Count + info.Postfixes.Count + info.Transpilers.Count) > 0


def test():
    yield 2
    lt.check('Rhino 9', Rhino.RhinoApp.Version.Major, 9)
    lt.check('the Rhino 9 build', lt.static_get('MiniVivisectionPlugin', 'BuiltForRhino'), 9)
    # Installed when RhinoCodePluginGH loads (Runs, Debug, HiddenEditors) or Grasshopper.dll (PauseGuard, PauseCanvas)
    before = problems()
    lt.note('before Grasshopper: %s' % before)
    waiting = []
    if lt.assembly('RhinoCodePluginGH') is None:
        waiting += ['GrasshopperRuns', 'GrasshopperDebug', 'GrasshopperHiddenEditors']
    if lt.assembly('Grasshopper') is None:
        waiting += ['GrasshopperPauseGuard', 'GrasshopperPauseCanvas']
    lt.check('before Grasshopper: its hooks say "waiting for Grasshopper", none failed',
             dict((name, before[name]) for name in waiting), dict((name, 'waiting for Grasshopper') for name in waiting))
    lt.check('no startup warning about the editor\'s Run and Debug, no failed hidden editors',
             before['GrasshopperHiddenEditors'] in (None, 'waiting for Grasshopper'), True)
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    yield 3
    lt.check('Grasshopper loaded: every GH hook in place', problems(), dict((name, None) for name in HOOKS))
    lt.check('runs followed through TrySolveInstance (one solve call replaced)',
             lt.static_get('GrasshopperRuns', 's_solveCallsReplaced'), 1)
    ghdoc_type = lt.assembly('Grasshopper').GetType('Grasshopper.Kernel.GH_Document')
    escape = ghdoc_type.GetMethod('GetEscapeKeyState', lt.ANY)
    lt.check('Grasshopper 9 Escape watcher: GetEscapeKeyState patched', escape is not None and patched(escape), True)

    import Grasshopper
    from Grasshopper.Kernel import GH_DocumentIO
    io = GH_DocumentIO()
    lt.check('opens gh3', io.Open(GH3), True)
    doc = io.Document
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    Grasshopper.Instances.ActiveCanvas.Document = doc
    doc.Enabled = True
    doc.NewSolution(False)
    yield 3
    find = lambda nick: [o for o in doc.Objects if o.NickName == nick][0]
    halve, toggle = find('Halve'), find('Run')
    runs = lt.plugin_type('GrasshopperRuns')
    visible = lambda: runs.GetMethod('HasVisibleEditor', lt.ANY).Invoke(None, [halve])
    hidden = lambda: lt.static_get('GrasshopperHiddenEditors', 'Count')
    hook = lt.hook()
    paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))

    def editor_window():
        context = runs.GetMethod('ContextOf', lt.ANY).Invoke(None, [halve])
        script_editor = context.GetType().GetProperty('ScriptEditor', lt.ANY).GetValue(context)
        return script_editor.GetType().GetMethod('GetWindow').Invoke(script_editor, None)

    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Normal')
    yield 5
    lt.check('Normal: no hidden editor', (hidden(), visible()), (0, False))
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    try:
        yield lt.wait_for(lambda: hidden() == 1, 60)
        lt.check('Always debug: Halve has a hidden editor, not visible to Grasshopper (no cone on the canvas)', (hidden(), visible()), (1, False))
        window = editor_window()
        lt.check('its window: a Rhino 9 editor (GH1Editor)', window is not None and 'GH1Editor' in window.GetType().FullName, True)
        lt.check('hidden: cloaked', lt.is_cloaked(window), True)
        lt.check('hidden: out of the taskbar and Alt+Tab', lt.static_call('ScriptRunCommand', 'IsOutOfTaskbar', window), True)
        # Many solutions: HasVisibleEditor (generic code) may be recompiled -- the repair keeps the editor hidden.
        for k in range(40):
            halve.ExpireSolution(True)
            yield 0.05
        yield 4
        lt.note('hidden editors\' patches put back: %d time(s)' % lt.static_get('GrasshopperHiddenEditors', 'Repairs'))
        lt.check('after 40 solutions: still hidden, still not visible to Grasshopper', (hidden(), visible()), (1, False))

        def press(after):
            time.sleep(after)
            lt.progress('Break now')
            lt.static_call('BreakHotkey', 'Break')

        threading.Thread(target=press, args=(3,), daemon=True).start()
        toggle.Value = True
        toggle.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.note('patches put back -- Always debug before a run: %d, after a run: %d; hidden editors: %d' % (
            lt.static_get('GrasshopperDebug', 'RepairsBeforeRun'), lt.static_get('GrasshopperRuns', 'Repairs'),
            lt.static_get('GrasshopperHiddenEditors', 'Repairs')))
        lt.check('Break now paused Halve (its editor never opened)', len(controls), 1)
        yield 2
        lt.check('the pause brought its editor up', (hidden(), visible()), (0, True))
        lt.check('shown: not cloaked any more', lt.is_cloaked(editor_window()), False)
        if controls:
            line = controls[0].Results.CurrentThread.CurrentFrame.Reference.Position.LineNumber
            lt.check('paused in the loop (lines 5-7)', line in (5, 6, 7), True)
            toggle.Value = False
            controls[0].Stop()
            yield 3
            lt.check('Stop ends it', len(paused()), 0)
        editor_window().Close()  # closed by the user: hidden again by the next scans
        yield lt.wait_for(lambda: hidden() == 1, 30)
        lt.check('closed: hidden again', (hidden(), visible()), (1, False))
        runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [halve])  # opened by the user: it comes up
        yield 1
        lt.check('opened by the user: shown', (hidden(), visible()), (0, True))
        editor_window().Close()
        yield lt.wait_for(lambda: hidden() == 1, 30)
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Normal')
        yield lt.wait_for(lambda: hidden() == 0, 30)
        yield 1
        lt.check('Always debug off: the hidden editor closed', (hidden(), visible()), (0, False))
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)
    doc.Enabled = False


lt.run('t_rhino9_gh', test)
