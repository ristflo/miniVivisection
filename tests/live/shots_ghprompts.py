#! python 3
# Screenshot of the prompts window in Grasshopper (tools/compose_screenshots.py, picture 16):
# V Claude with the real Claude Code and RhinoAI builds a small definition from one message,
# then Grasshopper's window and the prompts window are captured (raw: prompts_gh,
# prompts_window). Run it by name, with the user's session connected (WPF draws nothing while
# an RDP session is disconnected -- the prompts window would come out black; then it says so
# and writes no capture):
#     python tests/live/run_live.py --desktop hidden shots_ghprompts
# The first run costs a Claude message; it keeps that run's definition and transcript
# (%TEMP%\minivivisection-live\prompts_demo.gh / .txt), and later runs show them again instead
# of asking Claude anew (delete them for a new run).
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

MESSAGE = ("Build a twisted stack: a Number Slider 'Floors' (2 to 20, value 8) and one 'Twist' (0 to 180 degrees, value 90); "
           "a 10 x 6 rectangle per floor, 3 apart in Z, each turned about Z by Twist * i / (Floors - 1) degrees; loft them. "
           "Keep it tidy, left to right, and leave a short Markdown note on what it does.")
KEPT = os.path.join(os.environ.get('TEMP', '.'), 'minivivisection-live', 'prompts_demo')


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('miniVivisection.GH') is not None and lt.assembly('RhinoCodePluginGH') is not None)


def test():
    yield 2
    import shots
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    yield 3
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_DocumentIO
    editor = Grasshopper.Instances.DocumentEditor
    canvas = Grasshopper.Instances.ActiveCanvas
    editor.Location = System.Drawing.Point(30, 30)
    editor.Size = System.Drawing.Size(1200, 750)  # 960 x 600 at 100 %
    started = lt.static_call('MiniVivisectionSettings', 'Get', 'ClaudeStartRhinoAi')
    lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeStartRhinoAi', True)
    try:
        lt.static_call('GrasshopperPrompts', 'Show', 'claude')
        yield 1
        window = lt.static_get('GrasshopperPrompts', 'Window')
        window.ClientSize = Eto_size(600, 290)
        chat = lt.static_get('GrasshopperPrompts', 'Claude')
        chat.GetType().GetMethod('Type', lt.ANY).Invoke(chat, ['/clear'])
        yield 1
        view = lt.member(chat, '_view')
        if os.path.exists(KEPT + '.gh') and os.path.exists(KEPT + '.txt'):
            # the kept real run: its definition, and its transcript shown again
            io = GH_DocumentIO()
            io.Open(KEPT + '.gh')
            doc = io.Document
            Grasshopper.Instances.DocumentServer.AddDocument(doc)
            canvas.Document = doc
            doc.Enabled = True
            doc.NewSolution(False)
            with open(KEPT + '.txt', encoding='utf-8') as f:
                transcript = f.read()
            view.Input = ''
            view.GetType().GetMethod('Write', lt.ANY).Invoke(view, [transcript])
            lt.note('shown again: the kept run (%s)' % KEPT)
        else:
            doc = GH_Document()
            Grasshopper.Instances.DocumentServer.AddDocument(doc)
            canvas.Document = doc
            doc.Enabled = True
            chat.GetType().GetMethod('Type', lt.ANY).Invoke(chat, [MESSAGE])
            busy = lambda: chat.GetType().GetProperty('Busy', lt.ANY).GetValue(chat)
            yield lt.wait_for(lambda: busy(), 20)
            yield lt.wait_for(lambda: not busy(), 540)
            yield 2
            shown = chat.GetType().GetProperty('Shown', lt.ANY).GetValue(chat)
            lt.note('the chat:\n' + shown[-2500:])
            os.makedirs(os.path.dirname(KEPT), exist_ok=True)
            with open(KEPT + '.txt', 'w', encoding='utf-8') as f:
                f.write(shown[shown.find('> Build'):].rstrip('> \n') + '\n')
            io = GH_DocumentIO(doc)
            io.SaveQuiet(KEPT + '.gh')
        yield 2
        lt.check('a definition to show', doc.ObjectCount > 3, True)
        # the definition across the top of the canvas, the window over the bottom right
        objects = [o for o in doc.Objects if o.Attributes is not None and o.GetType().Name != 'GH_Group']
        box = None
        for o in objects:
            b = o.Attributes.Bounds
            box = b if box is None else System.Drawing.RectangleF.Union(box, b)
        zoom = min(1.0, (canvas.Width - 50) / max(1.0, box.Width), (canvas.Height * 0.5) / max(1.0, box.Height))
        for o in doc.Objects:
            o.Attributes.Selected = False
        for _ in range(2):  # again after a moment: a definition just opened restores its own saved view
            canvas.Viewport.Zoom = zoom
            canvas.Viewport.MidPoint = System.Drawing.PointF(box.X + box.Width / 2, box.Y + box.Height / 2 + (canvas.Height * 0.22) / zoom)
            canvas.Refresh()
            yield 1
        lt.note('canvas zoom %.2f for a definition %d x %d' % (canvas.Viewport.Zoom, box.Width, box.Height))
        content = shots.wpf(window.ControlObject, 1.25)
        drawn = content.GetPixel(content.Width // 2, content.Height // 2).A > 0
        lt.check('WPF draws the window (the session is connected)', drawn, True)
        if drawn:
            shots.save(shots.window(editor.Handle.ToInt64()), 'prompts_gh')
            shots.save(shots.editor(window, window.NativeHandle.ToInt64()), 'prompts_window')
            lt.note('shots: prompts_gh, prompts_window')
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeStartRhinoAi', started)


def Eto_size(w, h):
    import Eto.Drawing
    return Eto.Drawing.Size(w, h)


lt.run('shots_ghprompts', test)
