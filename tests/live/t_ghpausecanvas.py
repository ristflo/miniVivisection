#! python 3
# Grasshopper's canvas while a script component is paused: panning (a right-drag) works,
# deleting an object is refused until the pause ends, and works again after Stop.
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


def mouse(canvas, kind, button, x, y):
    """The canvas's own mouse handling (OnMouseDown / OnMouseMove / OnMouseUp), as a user's."""
    import System.Windows.Forms as wf
    args = wf.MouseEventArgs(getattr(wf.MouseButtons, button), 1, x, y, 0)
    canvas.GetType().GetMethod('OnMouse' + kind, lt.ANY).Invoke(canvas, [args])


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_DocumentIO
    io = GH_DocumentIO()
    io.Open(GH3)
    doc = io.Document
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    canvas = Grasshopper.Instances.ActiveCanvas
    canvas.Document = doc
    doc.Enabled = True
    doc.NewSolution(False)
    yield 3
    find = lambda nick: [o for o in doc.Objects if o.NickName == nick][0]
    halve, toggle, lesson = find('Halve'), find('Run'), find('Lesson')
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    hook = lt.hook()
    paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
    try:
        yield lt.wait_for(lambda: lt.static_get('GrasshopperHiddenEditors', 'Count') == 1, 60)
        yield 5

        def press():
            time.sleep(3)
            lt.static_call('BreakHotkey', 'Break')

        threading.Thread(target=press, daemon=True).start()
        toggle.Value = True
        toggle.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('paused in Halve', len(controls), 1)
        if not controls:
            return
        lt.check('the document is solving (the pause is inside it)', str(doc.SolutionState), 'Process')
        lt.check('the canvas sees it paused', lt.static_call('GrasshopperPauseCanvas', 'IsPausedIn', doc), True)

        # Pan: a right-drag across the canvas.
        before = (canvas.Viewport.Tx, canvas.Viewport.Ty)
        mouse(canvas, 'Down', 'Right', 300, 300)
        for step in range(1, 6):
            mouse(canvas, 'Move', 'Right', 300 + 20 * step, 300 + 10 * step)
        mouse(canvas, 'Up', 'Right', 400, 350)
        yield 1
        after = (canvas.Viewport.Tx, canvas.Viewport.Ty)
        lt.note('viewport %r -> %r' % (before, after))
        lt.check('panning works while paused', after != before, True)

        # Delete: refused while paused.
        lt.check('deleting refused while paused', doc.RemoveObject(lesson, False), False)
        lt.check('the object is still there', any(o.InstanceGuid == lesson.InstanceGuid for o in doc.Objects), True)

        toggle.Value = False
        controls[0].Stop()
        yield 3
        lt.check('stopped', len(paused()), 0)
        lt.check('after the pause: deleting works', doc.RemoveObject(lesson, False), True)
        doc.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)


lt.run('t_ghpausecanvas', test)
