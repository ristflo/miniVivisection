#! python 3
# V Claude in Grasshopper with the real Claude Code and RhinoAI (costs a few cents; run it by
# name, it's not in the default list): one message builds a slider and a circle wired to it
# through RhinoAI's g1 tools and miniVivisection's gh tools; the answer is one undo step, and
# /undo takes it back. RhinoAI's server is started for it (the setting is restored after).
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

MESSAGE = ("Place a Number Slider with the nickname Size (min 1, max 10, value 4) and a Circle component to its right, "
           "and wire the slider into the circle's radius. Then check with gh_get_data that the circle's radius is 4.")


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('miniVivisection.GH') is not None)


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    yield 3
    import Grasshopper
    from Grasshopper.Kernel import GH_Document
    started = lt.static_call('MiniVivisectionSettings', 'Get', 'ClaudeStartRhinoAi')
    lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeStartRhinoAi', True)
    try:
        doc = GH_Document()
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        lt.static_call('GrasshopperPrompts', 'Show', 'claude')
        yield 1
        chat = lt.static_get('GrasshopperPrompts', 'Claude')
        chat.GetType().GetMethod('Type', lt.ANY).Invoke(chat, [MESSAGE])
        busy = lambda: chat.GetType().GetProperty('Busy', lt.ANY).GetValue(chat)
        yield lt.wait_for(lambda: busy(), 20)
        yield lt.wait_for(lambda: not busy(), 300)
        shown = chat.GetType().GetProperty('Shown', lt.ANY).GetValue(chat)
        lt.note('the chat:\n' + shown[-3000:])
        sliders = [o for o in doc.Objects if o.GetType().Name == 'GH_NumberSlider']
        circles = [o for o in doc.Objects if o.Name == 'Circle']
        lt.check('a slider named Size', [s.NickName for s in sliders], ['Size'])
        lt.check('a circle', len(circles), 1)
        if sliders and circles:
            radius = [p for p in circles[0].Params.Input if p.Name == 'Radius'][0]
            lt.check('wired: slider -> radius', [s.InstanceGuid for s in radius.Sources], [sliders[0].InstanceGuid])
            lt.check('the slider at 4', System.Convert.ToDouble(sliders[0].CurrentValue), 4.0)
        lt.check('the chat says the definition changed', '[the definition changed:' in shown, True)
        lt.check('one undo record of Claude', doc.UndoServer.UndoNames[0].startswith('V' + chr(0xB0) + 'Claude: '), True)
        chat.GetType().GetMethod('Type', lt.ANY).Invoke(chat, ['/undo'])
        yield 2
        lt.check('/undo: the definition empty again', doc.ObjectCount, 0)
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeStartRhinoAi', started)


lt.run('t_ghclaude_real', test)
