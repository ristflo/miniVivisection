#! python 3
# Regression for the crash of 2026-10-17 (real Claude Code + RhinoAI; run by name): Always debug, GH 2's lesson
# (Posts, a hidden editor) and a new definition; a message in the new one, then the canvas
# switched to GH 2 and another message there -- what the user did before Rhino crashed.
import os
import shutil
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

LESSON = os.path.join(lt.ROOT, r'examples\grasshopper\gh2_the_one_wrong_item.gh')


def ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def test():
    yield 2
    old = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    started = lt.static_call('MiniVivisectionSettings', 'Get', 'ClaudeStartRhinoAi')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeStartRhinoAi', True)
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
        chat = lt.static_get('GrasshopperPrompts', 'Claude')
        busy = lambda: chat.GetType().GetProperty('Busy', lt.ANY).GetValue(chat)
        ask = lambda text: chat.GetType().GetMethod('Type', lt.ANY).Invoke(chat, [text])
        ask('Place a Python 3 Script component named Tower with an input n and an output a = n * 2, and a Number Slider (1 to 10) wired into n.')
        yield lt.wait_for(lambda: busy(), 20)
        yield lt.wait_for(lambda: not busy(), 300)
        lt.progress('first answer done; B has %d objects' % b.ObjectCount)
        yield 2
        canvas.Document = a
        a.NewSolution(False)
        yield 4
        lt.progress('switched to A; asking there')
        ask('What does this definition do? Be brief.')
        yield lt.wait_for(lambda: busy(), 20)
        yield lt.wait_for(lambda: not busy(), 300)
        yield 3
        # The canvas switched to the other definition in the middle of an answer: the answer stops
        before = b.ObjectCount
        ask('Add three Number Sliders named S1, S2, S3, one below the other, and a Panel showing their sum.')
        yield lt.wait_for(lambda: busy(), 20)
        yield 4
        canvas.Document = b
        yield lt.wait_for(lambda: not busy(), 20)
        shown = chat.GetType().GetProperty('Shown', lt.ANY).GetValue(chat)
        lt.check('switched in the middle: the answer stopped', 'Claude\'s answer is stopped' in shown and not busy(), True)
        lt.check('nothing of it landed in the other definition', b.ObjectCount, before)
        lt.note('the chat:\n' + shown[-2500:])
        lt.check('Rhino survived', True, True)
        a.Enabled = False
        b.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old)
        lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeStartRhinoAi', started)


lt.run('t_ghtwodocs_real', test)
