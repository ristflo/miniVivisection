#! python 3
# Hidden editors for Grasshopper components (Always debug): GH 3's Halve, its editor never
# opened, gets one out of sight; Ctrl+Alt+B (the hotkey's path) pauses the hang -- not stops
# it -- and brings the editor up; closed, it's hidden again; opened by the user, it comes up;
# Always debug off closes it.
import os
import sys
import threading
import time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

GH3 = os.path.join(lt.ROOT, r'examples\grasshopper\gh3_stop_a_hang.gh')


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
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
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Normal')
    yield 5
    lt.check('Normal: no hidden editor', (hidden(), visible()), (0, False))
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    try:
        yield lt.wait_for(lambda: hidden() == 1, 60)
        lt.check('Always debug: Halve has a hidden editor, not visible to Grasshopper (no cone on the canvas)', (hidden(), visible()), (1, False))
        editor_window = lambda: lt.member(lt.member(lt.member(runs.GetMethod('ContextOf', lt.ANY).Invoke(None, [halve]), 'ScriptEditor'), 'CodeEditor'), '_rce')
        out_of_taskbar = lambda: lt.static_call('ScriptRunCommand', 'IsOutOfTaskbar', editor_window())
        lt.check('hidden: out of the taskbar and Alt+Tab', out_of_taskbar(), True)
        yield 5  # the editor loads

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
        lt.check('Break now paused Halve (its editor never opened)', len(controls), 1)
        yield 2
        lt.check('the pause brought its editor up', (hidden(), visible()), (0, True))
        lt.check('shown: in the taskbar and Alt+Tab again', out_of_taskbar(), False)
        if controls:
            line = controls[0].Results.CurrentThread.CurrentFrame.Reference.Position.LineNumber
            lt.check('paused in the loop (lines 5-7)', line in (5, 6, 7), True)
            toggle.Value = False
            controls[0].Stop()
            yield 3
            lt.check('Stop ends it', len(paused()), 0)
        # Closed by the user: hidden again by the next scans.
        editor = runs.GetMethod('ContextOf', lt.ANY).Invoke(None, [halve])
        script_editor = editor.GetType().GetProperty('ScriptEditor', lt.ANY).GetValue(editor)
        rce = lt.member(lt.member(script_editor, 'CodeEditor'), '_rce')
        rce.Close()
        yield lt.wait_for(lambda: hidden() == 1, 30)
        lt.check('closed: hidden again', (hidden(), visible()), (1, False))
        # Opened by the user (double-click, its menu): it comes up.
        runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [halve])
        yield 1
        lt.check('opened by the user: shown', (hidden(), visible()), (0, True))
        rce = lt.member(lt.member(script_editor, 'CodeEditor'), '_rce')
        rce.Close()
        yield lt.wait_for(lambda: hidden() == 1, 30)
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Normal')
        yield lt.wait_for(lambda: hidden() == 0, 30)
        yield 1
        lt.check('Always debug off: the hidden editor closed', (hidden(), visible()), (0, False))
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)
    doc.Enabled = False


lt.run('t_ghhidden', test)
