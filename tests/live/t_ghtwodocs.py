#! python 3
# Regression for the crash of 2026-10-17 15:18 (not reproduced): two definitions with Python components under
# Always debug (hidden editors), changes made to the second as V Claude's tools make them (one
# undo step), the canvas switched to the first in the middle and back, undo, switched again.
import os
import shutil
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import json
import livetest as lt
import System
import Rhino

LESSON = os.path.join(lt.ROOT, r'examples\grasshopper\gh2_the_one_wrong_item.gh')


def ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def gh_call(tool, **args):
    t = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.GrasshopperTools')
    answer = t.GetMethod('Call').Invoke(None, [tool, json.dumps(args)])
    lt.progress('%s -> %s' % (tool, answer[:120].replace('\n', ' | ')))
    return answer


def stock(name):
    import Grasshopper
    for proxy in Grasshopper.Instances.ComponentServer.ObjectProxies:
        if proxy.Desc.Name == name and not proxy.Obsolete:
            obj = Grasshopper.Instances.ComponentServer.EmitObject(proxy.Guid)
            obj.CreateAttributes()
            return obj
    raise RuntimeError(name)


def test():
    yield 2
    old = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_DocumentIO
    canvas = Grasshopper.Instances.ActiveCanvas
    try:
        copy = os.path.join(os.environ['TEMP'], 'minivivisection-live', 'gh2_copy.gh')
        shutil.copy(LESSON, copy)
        io = GH_DocumentIO()
        io.Open(copy)
        a = io.Document
        Grasshopper.Instances.DocumentServer.AddDocument(a)
        canvas.Document = a
        a.Enabled = True
        a.NewSolution(False)
        yield 6
        b = GH_Document()
        Grasshopper.Instances.DocumentServer.AddDocument(b)
        canvas.Document = b
        b.Enabled = True
        lt.static_call('GrasshopperPrompts', 'Show', 'claude')
        yield 1
        undo = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.ClaudeUndo')
        before = undo.GetMethod('Begin').Invoke(None, None)
        lt.progress('B: placing, as RhinoAI does')
        py = stock('Python 3 Script')
        py.Attributes.Pivot = System.Drawing.PointF(300, 100)
        b.AddObject(py, False)
        slider = stock('Number Slider')
        slider.Attributes.Pivot = System.Drawing.PointF(50, 100)
        b.AddObject(slider, False)
        b.NewSolution(False)
        yield 3
        gh_call('gh_set_script', id=str(py.InstanceGuid), code='#! python 3\nimport time\nfor i in range(30):\n    time.sleep(0.05)\na = x\n', inputs=[{'name': 'x'}], outputs=['a'])
        gh_call('gh_edit_objects', ids=[str(py.InstanceGuid)], nickname='Rotating Tower')
        yield 2
        lt.progress('switch to A in the middle of the answer')
        canvas.Document = a
        a.NewSolution(False)
        yield 1
        # the answer goes on: tools now find the active definition (A)
        gh_call('gh_get_selection')
        gh_call('gh_set_value', id=str(slider.InstanceGuid), value=5)
        py.Params.Input[0].AddSource(slider)
        b.NewSolution(False)
        yield 4
        summary = undo.GetMethod('Commit').Invoke(None, [before, 'V' + chr(0xB0) + 'Claude: test'])
        lt.note('commit: %s' % summary)
        yield 2
        for i in range(3):
            canvas.Document = b
            b.NewSolution(False)
            yield 2
            canvas.Document = a
            a.NewSolution(False)
            yield 2
        canvas.Document = b
        b.UndoServer.PerformUndo()
        yield 3
        canvas.Document = a
        yield 3
        # the message's start in the other definition: its undo snapshot, the context, the session
        for round_ in range(3):
            lt.progress('Begin on A (round %d): %d objects' % (round_, a.ObjectCount))
            snap = undo.GetMethod('Begin').Invoke(None, None)
            lt.progress('context: ' + str(lt.static_call('GrasshopperBridge', 'Context', True)))
            lt.progress('key: ' + str(lt.static_call('GrasshopperBridge', 'DocumentKey')))
            yield 1
            lt.progress('commit on A: %s' % undo.GetMethod('Commit').Invoke(None, [snap, 'V' + chr(0xB0) + 'Claude: a']))
            canvas.Document = b
            yield 2
            snap = undo.GetMethod('Begin').Invoke(None, None)
            yield 1
            undo.GetMethod('Commit').Invoke(None, [snap, 'V' + chr(0xB0) + 'Claude: b'])
            canvas.Document = a
            yield 2
        lt.check('Rhino survived', True, True)
        a.Enabled = False
        b.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old)


lt.run('t_ghtwodocs', test)
