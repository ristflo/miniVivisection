#! python 3
# Ctrl+Alt+B on a Grasshopper component that hangs, with Always debug and its editor open:
# it pauses (not stops) at its line, Continue lets it run on, a second Break now pauses it
# again, Stop ends it.
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
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    hook = lt.hook()
    paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
    try:
        runs = lt.plugin_type('GrasshopperRuns')
        runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [halve])
        yield lt.wait_for(lambda: runs.GetMethod('HasVisibleEditor', lt.ANY).Invoke(None, [halve]), 60)
        yield 3

        def press(after):
            # The hotkey's own path, from a thread of its own (the UI thread runs Halve).
            time.sleep(after)
            lt.progress('Break now')
            lt.static_call('BreakHotkey', 'Break')

        threading.Thread(target=press, args=(3,), daemon=True).start()
        toggle.Value = True
        toggle.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('Break now paused Halve (with its editor open)', len(controls), 1)
        if not controls:
            return
        frame = controls[0].Results.CurrentThread.CurrentFrame
        lt.check('paused in the loop (lines 5-7)', frame.Reference.Position.LineNumber in (5, 6, 7), True)
        seen = []

        def press_again():
            # Continue: Halve loops on (Rhino busy, as with any hang) -- seen running, then paused again.
            time.sleep(2)
            seen.append(str(controls[0].State))
            press(0)

        threading.Thread(target=press_again, daemon=True).start()
        controls[0].Continue()
        yield lt.wait_for(lambda: len(seen) > 0 and len(paused()) > 0, 30)
        lt.check('Continue: it ran on', seen, ['Debugging'])
        lt.check('a second Break now pauses it again', len(paused()), 1)
        toggle.Value = False
        paused()[0].Stop()
        yield 3
        lt.check('Stop ends it', len(paused()), 0)
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)
    doc.Enabled = False


lt.run('t_ghbreak', test)
