#! python 3
# Scripts in Grasshopper Panels shown as code (miniVivisection.GH's ScriptPanels): which Panels
# are scripts (marked, recognized, or feeding a script input), drawn as code -- line numbers, a
# scroll bar along the bottom for a long line; images of the canvas saved for a look --, a C#
# Panel's round trip through the Script Editor (its menu), editing in place (a double-click:
# CodeBox) and at a stopped line, a script-input component's library files and script opened
# (its double-click), the setting off.
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

STATIC = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static

PYTHON = '''#! python 3
"""Twist: n squares stacked up, each turned a little more than the one below."""
import Rhino.Geometry as rg

squares = []
for i in range(n):  # one per level
    square = rg.Rectangle3d(rg.Plane.WorldXY, rg.Interval(-5, 5), rg.Interval(-5, 5)).ToNurbsCurve()   # a long line, past the Panel's right edge
    square.Rotate(i * angle, rg.Vector3d.ZAxis, rg.Point3d.Origin)
    squares.append(square)
'''

NOTES = 'THE BUG\nSomething fails.\n\nTRY THIS\n1. Hover it.\n'

CSHARP = '''using System;
using Rhino.Geometry;

// a point, and its distance
var p = new Point3d(1, 2, 3);
A = p.DistanceTo(Point3d.Origin);
'''

UNMARKED = 'a = x * 2\n'  # no marker, one line: a script only because it feeds a Python script input


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def editor_shows(path):
    for window in lt.editors():
        code = System.Activator.CreateInstance(lt.plugin_type('ScriptEditorAccess'), [window]).Code
        if code is not None and code.Uri is not None and code.Uri.IsFile and code.Uri.LocalPath.lower() == path.lower():
            return True
    return False


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess
    import make_gh_examples as mk
    panels = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.ScriptPanels')
    lt.check('the patches are in', panels.GetProperty('Installed', STATIC).GetValue(None), True)
    script_of = lambda panel: panels.GetMethod('ScriptOf', STATIC).Invoke(None, [panel])
    language = lambda panel: str(lt.member(script_of(panel), 'Language')) if script_of(panel) is not None else 'None'
    was_on = lt.static_get('MiniVivisectionSettings', 'GrasshopperScriptPanels')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScriptPanels', True)
    try:
        doc = GH_Document()
        python = mk.panel(PYTHON, 20, 20, width=330, height=150)
        python.NickName = 'Python script'
        notes = mk.panel(NOTES, 20, 200, width=330, height=90)
        csharp = mk.panel(CSHARP, 380, 20, width=300, height=120)
        csharp.NickName = 'CSharp script'
        unmarked = mk.panel(UNMARKED, 380, 200, width=160, height=40)
        unmarked.NickName = 'Unmarked'
        run = mk._place(mk.script('Run', 'Run', mk.INPUT_SOURCE, [('x', GH_ParamAccess.item, 'int')], ['a']), 600, 200)
        mk._script_input(run).AddSource(unmarked)
        for obj in (python, notes, csharp, unmarked, run):
            doc.AddObject(obj, False)
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        yield 2
        lt.check('which Panels are scripts: marked, notes, recognized, feeding a Python script input',
                 [language(p) for p in (python, notes, csharp, unmarked)], ['Python', 'None', 'CSharp', 'Python'])

        # Drawn: an image of the canvas, before and after scrolling the long line into view
        canvas = Grasshopper.Instances.ActiveCanvas
        folder = os.path.join(tempfile.gettempdir(), 'minivivisection-live')
        os.makedirs(folder, exist_ok=True)

        def image(name):
            viewport = Grasshopper.GUI.Canvas.GH_Viewport()
            viewport.Width, viewport.Height = 1500, 700
            viewport.Zoom = 2.0
            viewport.MidPoint = System.Drawing.PointF(350, 160)
            bitmap = canvas.GenerateHiResImageTile(viewport, System.Drawing.Color.White)
            path = os.path.join(folder, name)
            bitmap.Save(path)
            lt.note('image: ' + path)

        image('script_panels.png')
        state = script_of(python)
        lt.check('drawn: the code laid out (its height known)', lt.member(state, 'ContentHeight') > 0, True)
        lt.check('a line longer than the Panel: a scroll bar along the bottom', lt.member(state, 'TrackControl').IsEmpty, False)
        lt.check('the short C# lines: no bottom bar', lt.member(script_of(csharp), 'TrackControl').IsEmpty, True)
        state.GetType().GetField('Horizontal').SetValue(state, 1.0)  # as dragging the bar to its end
        image('script_panels_scrolled.png')

        # A double-click opens the Panel's script in the Script Editor; a save goes back
        opened = panels.GetMethod('OpenInScriptEditor', STATIC).Invoke(None, [csharp, lt.member(script_of(csharp), 'Language')])
        lt.check('opened (the .rhp found)', opened, True)
        working = os.path.join(lt.static_get('GrasshopperInputScripts', 'Folder'), 'CSharp_script-%s.cs' % str(csharp.InstanceGuid)[:8])
        yield lt.wait_for(lambda: editor_shows(working), 90)
        lt.check('the C# Panel open in the Script Editor, as a .cs file', editor_shows(working), True)
        with open(working, 'w', encoding='utf-8') as f:
            f.write(CSHARP.replace('1, 2, 3', '4, 5, 6'))  # as the editor's Save
        yield 3
        lt.check('saved: back in the Panel', '4, 5, 6' in csharp.UserText, True)

        # Edited in place (a double-click): typing, auto-indent, undo; clicking away keeps it, Esc drops it
        boxes = lambda: [c for c in canvas.Controls if c.GetType().Name == 'CodeBox']
        code_box = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.CodeBox')
        canvas.Viewport.Zoom = 1.0
        canvas.Viewport.MidPoint = System.Drawing.PointF(185, 95)  # the Python Panel's middle
        canvas.Refresh()
        yield 1
        python_language = lt.member(script_of(python), 'Language')
        opened = code_box.GetMethod('Open', STATIC).Invoke(None, [python, python_language, 0, False, -1])
        lt.check('edited in place: the box over the Panel', (opened, len(boxes())), (True, 1))
        if boxes():
            box = boxes()[0]
            press = lambda what: box.GetType().GetMethod('Press', lt.ANY).Invoke(box, [what])
            press('Ctrl+End')
            press('if n > 0:')
            press('Enter')
            press('pass')
            text = lt.member(box, 'TextNow')
            lt.check('typed, and indented after the colon', text.replace('\r\n', '\n').endswith('if n > 0:\n    pass'), True)
            press('Ctrl+Z')
            lt.check('Ctrl+Z: the word typed undone', lt.member(box, 'TextNow').replace('\r\n', '\n').endswith('if n > 0:\n    '), True)
            box.GetType().GetMethod('Close', lt.ANY).Invoke(box, [True])  # as clicking elsewhere
            yield 1
            lt.check('kept: in the Panel', 'if n > 0:' in python.UserText, True)
        code_box.GetMethod('Open', STATIC).Invoke(None, [python, python_language, 0, False, -1])
        if boxes():
            box = boxes()[0]
            box.GetType().GetMethod('Press', lt.ANY).Invoke(box, ['zzz'])
            box.GetType().GetMethod('Press', lt.ANY).Invoke(box, ['Escape'])
            yield 1
            lt.check('Esc: the changes dropped, the box closed', ('zzz' in python.UserText, len(boxes())), (False, 0))

        # After a stop: the Panel edited in place at the line (what the .rhp asks after Ctrl+Alt+B),
        # the explanation over it; the Panel keeps the mark until its text changes
        title = "Stopped in 'Run' at line 6, run 1 (Ctrl+Alt+B)"
        at_line = panels.GetMethod('EditAtLine', STATIC).Invoke(None, [python, 6, title, System.Array[System.String](['Grasshopper ran this Panel\'s script without the debugger.'])])
        lt.check('opened at the stopped line', (at_line, lt.member(boxes()[0], 'CaretLine') if boxes() else None), (True, 6))
        lt.check('with the explanation over it', lt.member(boxes()[0], '_stopTitle') if boxes() else None, title)
        if boxes():
            box = boxes()[0]
            picture = System.Drawing.Bitmap(box.Width, box.Height)
            box.DrawToBitmap(picture, System.Drawing.Rectangle(0, 0, box.Width, box.Height))
            picture.Save(os.path.join(folder, 'code_box_stopped.png'))
            lt.note('image: ' + os.path.join(folder, 'code_box_stopped.png'))
        for box in boxes():
            box.GetType().GetMethod('Close', lt.ANY).Invoke(box, [False])
        yield 1
        stop_of = lambda: panels.GetMethod('StopOf', STATIC).Invoke(None, [python])
        lt.check('the Panel keeps the mark (line 6)', stop_of().Item1, 6)
        image('script_panels_stopped.png')
        python.UserText = python.UserText + '# changed\n'
        lt.check('its text changed: the mark goes', stop_of().Item1, 0)

        # "Show as": notes forced to Python code, saved with the definition and read back
        show_as = panels.GetNestedType('ShowAs', System.Reflection.BindingFlags.NonPublic)
        lt.check('notes: text', language(notes), 'None')
        set_show_as = panels.GetMethod('SetShowAs', STATIC)
        set_show_as.Invoke(None, [notes, System.Enum.Parse(show_as, 'Python')])
        lt.check('"Show as" Python: shown as code', language(notes), 'Python')
        set_show_as.Invoke(None, [csharp, System.Enum.Parse(show_as, 'Text')])
        lt.check('"Show as" Plain text: a C# script shown as text', language(csharp), 'None')
        from Grasshopper.Kernel import GH_DocumentIO
        saved = os.path.join(folder, 'show_as.gh')
        lt.check('saved', GH_DocumentIO(doc).SaveQuiet(saved), True)
        io = GH_DocumentIO()
        io.Open(saved)
        reread = {o.NickName: o for o in io.Document.Objects}
        lt.check('read back: notes still Python, the C# Panel still text',
                 (language(reread['Lesson']), language(reread['CSharp script'])), ('Python', 'None'))
        set_show_as.Invoke(None, [notes, System.Enum.Parse(show_as, 'Auto')])
        set_show_as.Invoke(None, [csharp, System.Enum.Parse(show_as, 'Auto')])

        # The component's grayed "Open Script Editor" and "Export Script": working ones in their places
        import clr
        clr.AddReference('System.Windows.Forms')
        import System.Windows.Forms as WinForms
        menu = WinForms.ContextMenuStrip()
        run.AppendMenuItems(menu)
        texts = [(i.Text.replace('&', ''), i.Available, i.Enabled) for i in menu.Items if i.Text and ('Script Editor' in i.Text or 'Export Script' in i.Text)]
        lt.note('menu: %r' % texts)
        lt.check('Open Script Editor and Export Script: enabled (the grayed ones hidden)',
                 sorted(t for t, _, enabled in texts if enabled and t in ('Open Script Editor', 'Export Script')), ['Export Script', 'Open Script Editor'])

        # A component with a script input and a library: double-click opens the library's files, then the script
        library = os.path.join(folder, 'panel_library')
        os.makedirs(library, exist_ok=True)
        for name in ('shapes.py', 'numbers.py'):
            with open(os.path.join(library, name), 'w', encoding='utf-8') as f:
                f.write('#! python 3\nVALUE = 1\n')
        run.UsingLibraryInputParam = True
        library_input = [p for p in run.Params.Input if p.Name == 'library'][0]
        library_path = mk.panel(library, 380, 260, width=200, height=24)  # as a user wires it: the folder's path
        library_path.NickName = 'Library folder'
        doc.AddObject(library_path, False)
        library_input.AddSource(library_path)
        run.ExpireSolution(True)
        yield 2
        lt.note('the library input holds: %s' % lt.static_call('GrasshopperInputScripts', 'Describe', library_input))
        files = list(lt.static_call('GrasshopperInputScripts', 'LibraryFiles', run))
        lt.check("the library's files found", sorted(os.path.basename(f) for f in files), ['numbers.py', 'shapes.py'])
        lt.static_call('GrasshopperInputScripts', 'OpenWithLibraries', run)
        unmarked_copy = os.path.join(lt.static_get('GrasshopperInputScripts', 'Folder'), 'Unmarked-%s.py' % str(unmarked.InstanceGuid)[:8])
        yield lt.wait_for(lambda: editor_shows(unmarked_copy), 90)
        lt.check('the script opened last, its tab in front', editor_shows(unmarked_copy), True)

        # Markdown: rendered; a double-click on a word opens the source with the caret at that word
        md = mk.panel('# Notes\n\nSome **bold** and *italic* text, with `code` and a [link](http://x).\n\n'
                      '- first item\n- second item\n\n> a quote\n\n```\nprint("code")\n```\n', 20, 650, width=330, height=260)
        md.NickName = 'Markdown'
        doc.AddObject(md, False)
        set_show_as.Invoke(None, [md, System.Enum.Parse(show_as, 'Markdown')])
        canvas.Viewport.Zoom = 1.0
        canvas.Viewport.MidPoint = System.Drawing.PointF(185, 780)
        canvas.Refresh()
        yield 1
        viewport = Grasshopper.GUI.Canvas.GH_Viewport()
        viewport.Width, viewport.Height = 800, 620
        viewport.Zoom = 2.0
        viewport.MidPoint = System.Drawing.PointF(185, 780)
        canvas.GenerateHiResImageTile(viewport, System.Drawing.Color.White).Save(os.path.join(folder, 'markdown_panel.png'))
        lt.note('image: ' + os.path.join(folder, 'markdown_panel.png'))
        canvas.Refresh()  # laid out again on the canvas itself (the click's coordinates)
        yield 1
        md_state = panels.GetMethod('MarkdownOf', STATIC).Invoke(None, [md])
        layout = lt.member(md_state, 'Markdown')
        lt.check('Markdown: laid out', layout is not None, True)
        if layout is not None:
            pieces = list(lt.member(layout, '_pieces'))
            bold = [p for p in pieces if lt.member(p, 'Text').startswith('bold')]
            lt.check('the rendered "bold" is there', len(bold), 1)
            if bold:
                bounds = lt.member(bold[0], 'Bounds')
                origin = lt.member(layout, 'Origin')
                scrolled = lt.member(layout, 'ScrolledBy')
                # a click on the "l" of "bold": its third character
                width = bounds.Width
                point = System.Drawing.PointF(origin.X + bounds.X + width * 0.6, origin.Y + bounds.Y - scrolled + bounds.Height / 2)
                where = layout.GetType().GetMethod('SourceAt').Invoke(layout, [point])
                lt.note('a click on "bold" -> source line %d, column %d' % (where.Item1, where.Item2))
                lt.check('the click maps to "bold" in the source (line 3, columns 7-10)', (where.Item1, 7 <= where.Item2 <= 10), (2, True))
                opened = code_box.GetMethod('Open', STATIC).Invoke(None, [md, System.Enum.Parse(lt.assembly('miniVivisection.GH').GetType('MiniVivisection.ScriptTokens+Language'), 'Markdown'), where.Item1 + 1, False, where.Item2])
                if opened and boxes():
                    box = boxes()[0]
                    lt.check('its source in the box, the caret there', (lt.member(box, 'CaretLine'), lt.member(box, 'CaretColumn')), (where.Item1 + 1, where.Item2))
                    picture = System.Drawing.Bitmap(box.Width, box.Height)
                    box.DrawToBitmap(picture, System.Drawing.Rectangle(0, 0, box.Width, box.Height))
                    picture.Save(os.path.join(folder, 'markdown_source.png'))
                    lt.note('image: ' + os.path.join(folder, 'markdown_source.png'))
                    press = lambda what: box.GetType().GetMethod('Press', lt.ANY).Invoke(box, [what])
                    press('Ctrl+End')
                    press('- third')
                    press('Enter')
                    lt.check('Enter in a list: the next item', lt.member(box, 'TextNow').replace('\r\n', '\n').endswith('- third\n- '), True)
                    box.GetType().GetMethod('Close', lt.ANY).Invoke(box, [False])
                else:
                    lt.check('the Markdown source opened in the box', opened, True)

        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScriptPanels', False)
        lt.check('the setting off: no script Panels', [language(p) for p in (python, csharp, unmarked)], ['None', 'None', 'None'])
        doc.Enabled = False
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScriptPanels', was_on)


lt.run('t_ghscriptpanel', test)
