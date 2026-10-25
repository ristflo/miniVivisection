#! python 3
# Review 2026-10-15, A1: objects added to the definition while a script component is in its
# solution (a paste, the search box, an Alt-drag copy do AddObject(update: true), which resets
# the document's solution state and starts a new solution inside the running one). The state
# is kept, the new solution is held back and done once the solution is over, and the component
# runs once, not again inside its own run.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

replays = []


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None)


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess
    import make_gh_examples as mk
    # The component adds a Panel to its own definition with update -- what a paste does
    source = ('#! python 3\n'
              'from Grasshopper.Kernel.Special import GH_Panel\n'
              'doc = ghenv.Component.OnPingDocument()\n'
              'panel = GH_Panel()\n'
              'panel.UserText = "pasted"\n'
              'doc.AddObject(panel, True)\n'
              'a = str(doc.SolutionState)\n')
    canvas_hooks = lt.plugin_type('GrasshopperPauseCanvas')
    held = lambda: canvas_hooks.GetProperty('HeldSolutions', lt.ANY).GetValue(None)
    lt.note('HeldSolutions at the start: %r' % held())
    paster = mk._place(mk.script('Paster', 'Paster', source, [], ['a']), 200, 80)
    doc = GH_Document()
    doc.Enabled = False
    doc.AddObject(paster, False)
    lt.note('after AddObject: %r, RunCount %r' % (held(), paster.RunCount))
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    # The canvas taking the document runs its solution at once (Enabled or not): counted from here
    held_before = held()
    guard = lt.plugin_type('GrasshopperPauseGuard')
    replayed = guard.GetEvent('Replayed', lt.ANY)
    handler = System.Action[int, int](lambda objects, documents: replays.append((objects, documents)))
    replayed.GetAddMethod(True).Invoke(None, [handler])
    try:
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        lt.note('HeldSolutions: %r -> %r, RunCount %r' % (held_before, held(), paster.RunCount))
        messages = '; '.join(paster.RuntimeMessages(Grasshopper.Kernel.GH_RuntimeMessageLevel.Error))
        lt.note('messages: ' + messages)
        lt.check('the component ran', messages, '')
        state = [str(g) for g in paster.Params.Output[0].VolatileData.AllData(True)]
        lt.check('the solution\'s state stayed Process after the add', state, ['Process'])
        held_after = canvas_hooks.GetProperty('HeldSolutions', lt.ANY).GetValue(None)
        lt.note('HeldSolutions: %r -> %r' % (held_before, held_after))
        held_count = held_after - held_before
        lt.check('the new solution asked for inside the run was held back', held_count >= 1, True)
        yield lt.wait_for(lambda: len(replays) > 0, 15)
        lt.check('and done once the solution was over (one new solution)', replays[:1], [(1, 1)])
        yield 2
        panels = [o for o in doc.Objects if o.GetType().Name == 'GH_Panel' and o.UserText == 'pasted']
        lt.check('the component ran once: one Panel added, not one more by a run inside the run', len(panels), 1)
        lt.check('the solution over', str(doc.SolutionState), 'PostProcess')
    finally:
        replayed.GetRemoveMethod(True).Invoke(None, [handler])
        doc.Enabled = False


lt.run('t_pastewhilepaused', test)
