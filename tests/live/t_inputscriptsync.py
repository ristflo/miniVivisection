#! python 3
# A script input in the Script Editor and its Panel or file kept in step (GH 9, GH 10):
# - opened by a double-click, the editor shows the Panel's text;
# - an edit not saved goes back when the Script Editor is closed (it only hides): the Panel has
#   it, the component recomputes;
# - opened again, the editor shows the Panel's text -- also after the Panel changed while the
#   editor had changes not saved, which are then kept in a file of their own (and said);
# - a script file (GH 10): an edit not saved goes into the file when the Script Editor is closed.
import glob
import os
import shutil
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

GH9 = os.path.join(lt.ROOT, r'examples\grasshopper\gh9_script_from_a_panel.gh')
GH10 = os.path.join(lt.ROOT, r'examples\grasshopper\gh10_script_from_a_file.gh')
SPIRAL = os.path.join(lt.ROOT, r'examples\grasshopper\gh10_spiral.py')
FLAGS = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static


def gh_ready():
    asm = lt.assembly('Grasshopper')
    return asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None and lt.assembly('miniVivisection.GH') is not None


def same(a, b):
    return (a or '').replace('\r\n', '\n').rstrip() == (b or '').replace('\r\n', '\n').rstrip()


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(gh_ready, 120)
    import Grasshopper
    import Eto.Forms
    scripts = lt.plugin_type('GrasshopperInputScripts')
    access = lt.plugin_type('ScriptEditorAccess')
    folder = scripts.GetProperty('Folder', FLAGS).GetValue(None)
    work = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'inputscriptsync')
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)

    def editor(name):
        for window in Eto.Forms.Application.Instance.Windows:
            if window.GetType().FullName == 'RhinoCodeEditor.Editor.RCE' and name in (System.Activator.CreateInstance(access, [window]).ScriptPath or ''):
                return window
        return None

    def shown(window):
        return System.Activator.CreateInstance(access, [window]).ScriptText

    def double_click(component):
        scripts.GetMethod('OpenWithLibraries', FLAGS).Invoke(None, [component])

    # --- GH 9: a Panel's script ---------------------------------------------------------------
    gh9 = os.path.join(work, 'gh9.gh')
    shutil.copy2(GH9, gh9)
    Grasshopper.Instances.DocumentEditor.ScriptAccess_OpenDocument(gh9)
    yield 3
    doc = Grasshopper.Instances.ActiveCanvas.Document
    panel = next(o for o in doc.Objects if o.NickName == 'Twist script')
    twist = next(o for o in doc.Objects if o.NickName == 'Twist')
    double_click(twist)
    yield lt.wait_for(lambda: editor('Twist_script') is not None, 30)
    yield 2
    window = editor('Twist_script')
    lt.check('opened: the editor shows the Panel\'s text', same(shown(window), panel.UserText), True)

    # An edit, not saved; the Script Editor closed: back in the Panel, Twist recomputed
    fixed = panel.UserText.replace('\r\n', '\n').replace('import Rhino.Geometry as rg', 'import math\nimport Rhino.Geometry as rg') \
        .replace('square.Rotate(i * angle,', 'square.Rotate(math.radians(i * angle),')
    System.Activator.CreateInstance(access, [window]).SetScriptText(fixed)
    yield 1
    lt.check('an edit not saved: the Panel doesn\'t have it yet', 'math.radians' in panel.UserText, False)
    window.Close()
    yield lt.wait_for(lambda: 'math.radians' in panel.UserText, 10)
    lt.check('closed: the editor hid', window.Visible, False)
    lt.check('closed: the edit is in the Panel', 'math.radians' in panel.UserText, True)
    yield 2
    errors = list(twist.RuntimeMessages(Grasshopper.Kernel.GH_RuntimeMessageLevel.Error))
    lt.check('Twist recomputed with it, no error', (errors, twist.Params.Output[0].VolatileDataCount > 0), ([], True))

    # Opened again: the Panel's text
    double_click(twist)
    yield lt.wait_for(lambda: editor('Twist_script') is not None and editor('Twist_script').Visible, 30)
    yield 2
    window = editor('Twist_script')
    lt.check('opened again: the Panel\'s text', same(shown(window), panel.UserText), True)

    # The Panel changed while the editor had changes not saved: opened again, the Panel's text,
    # the changes kept in a file of their own
    System.Activator.CreateInstance(access, [window]).SetScriptText(panel.UserText.replace('\r\n', '\n') + '# not saved\n')
    yield 1
    panel.SetUserText(panel.UserText.replace('\r\n', '\n') + '# changed in the Panel\n')
    yield 1
    double_click(twist)
    yield 3
    window = editor('Twist_script')
    lt.check('the Panel changed meanwhile: the editor shows it', same(shown(window), panel.UserText), True)
    kept = glob.glob(os.path.join(folder, 'Twist_script-*.unsaved-*.py'))
    kept_text = open(kept[-1], encoding='utf-8').read() if kept else ''
    lt.check('the changes not saved, kept in a file', (len(kept) > 0, '# not saved' in kept_text), (True, True))
    entries = [e.Text for e in list(lt.static_get('UserLog', 'Entries'))[-6:]]
    lt.check('and said', any('changes are kept in' in e for e in entries), True)
    for path in kept:
        os.remove(path)
    window.Close()
    yield 2
    doc.Enabled = False
    doc.IsModified = False

    # --- GH 10: a script file ----------------------------------------------------------------
    gh10 = os.path.join(work, 'gh10.gh')
    spiral_file = os.path.join(work, os.path.basename(SPIRAL))
    shutil.copy2(GH10, gh10)
    shutil.copy2(SPIRAL, spiral_file)
    Grasshopper.Instances.DocumentEditor.ScriptAccess_OpenDocument(gh10)
    yield 3
    doc = Grasshopper.Instances.ActiveCanvas.Document
    spiral = next(o for o in doc.Objects if o.NickName == 'Spiral')
    double_click(spiral)
    yield lt.wait_for(lambda: editor(os.path.basename(SPIRAL)) is not None, 30)
    yield 2
    window = editor(os.path.basename(SPIRAL))
    path = System.Activator.CreateInstance(access, [window]).ScriptPath
    lt.check('the file itself is open', os.path.normcase(path), os.path.normcase(spiral_file))
    with open(spiral_file, encoding='utf-8') as f:
        before = f.read()
    lt.check('opened: the editor shows the file', same(shown(window), before), True)
    System.Activator.CreateInstance(access, [window]).SetScriptText(before.rstrip('\n') + '\n# edited, then closed\n')
    yield 1
    window.Close()
    yield lt.wait_for(lambda: '# edited, then closed' in open(spiral_file, encoding='utf-8').read(), 10)
    with open(spiral_file, encoding='utf-8') as f:
        lt.check('closed: the edit is in the file', '# edited, then closed' in f.read(), True)
    doc.Enabled = False
    doc.IsModified = False


lt.run('t_inputscriptsync', test)
