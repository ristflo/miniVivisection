#! python 3
# Review 2026-10-27, M3 and M4: a script input's file and the Script Editor.
# - M4, "Input Is Path" (GH 10: the Panel names the user's own script file): an edit not saved stays
#   in the editor when the Script Editor is closed (it only hides) -- the file isn't written; Run
#   with the edit not saved asks: No computes the file as saved, Cancel does nothing, Yes saves it.
# - M3, a Panel's script: a save from the editor refused (the Panel had changed meanwhile), then
#   opened again as the message advises: the save is kept in a file of its own (and said), not lost.
import glob
import os
import shutil
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

GH10 = os.path.join(lt.ROOT, r'examples\grasshopper\gh10_script_from_a_file.gh')
SPIRAL = os.path.join(lt.ROOT, r'examples\grasshopper\gh10_spiral.py')
FLAGS = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static


def gh_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def same(a, b):
    return (a or '').replace('\r\n', '\n').rstrip() == (b or '').replace('\r\n', '\n').rstrip()


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(gh_ready, 120)
    import Grasshopper
    import Eto.Forms
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess
    from Grasshopper.Kernel.Parameters import Param_Integer
    from Grasshopper.Kernel.Types import GH_Integer
    import make_gh_examples as mk
    scripts = lt.plugin_type('GrasshopperInputScripts')
    access = lt.plugin_type('ScriptEditorAccess')
    folder = scripts.GetProperty('Folder', FLAGS).GetValue(None)
    work = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'inputpathunsaved')
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)

    def editor(name):
        for window in Eto.Forms.Application.Instance.Windows:
            if window.GetType().FullName == 'RhinoCodeEditor.Editor.RCE' and name in (System.Activator.CreateInstance(access, [window]).ScriptPath or ''):
                return window
        return None

    def shown(window):
        return System.Activator.CreateInstance(access, [window]).ScriptText

    def log_has(text, last=6):
        return any(text in e.Text for e in list(lt.static_get('UserLog', 'Entries'))[-last:])

    # --- M4: the user's own file (Input Is Path) --------------------------------------------------
    gh10 = os.path.join(work, 'gh10.gh')
    spiral_file = os.path.join(work, os.path.basename(SPIRAL))
    shutil.copy2(GH10, gh10)
    shutil.copy2(SPIRAL, spiral_file)
    Grasshopper.Instances.DocumentEditor.ScriptAccess_OpenDocument(gh10)
    yield 3
    doc = Grasshopper.Instances.ActiveCanvas.Document
    spiral = next(o for o in doc.Objects if o.NickName == 'Spiral')
    scripts.GetMethod('OpenWithLibraries', FLAGS).Invoke(None, [spiral])
    yield lt.wait_for(lambda: editor(os.path.basename(SPIRAL)) is not None, 30)
    yield 2
    window = editor(os.path.basename(SPIRAL))
    lt.check('the file itself is open', os.path.normcase(System.Activator.CreateInstance(access, [window]).ScriptPath or ''), os.path.normcase(spiral_file))
    with open(spiral_file, encoding='utf-8') as f:
        before = f.read()
    edited = before.rstrip('\n') + '\n# edited, not saved\n'
    System.Activator.CreateInstance(access, [window]).SetScriptText(edited)
    yield 1
    window.Close()  # Grasshopper's Script Editor only hides
    yield 3
    lt.check('closed: the editor hid', window.Visible, False)
    with open(spiral_file, encoding='utf-8') as f:
        lt.check('closed: the file not written', same(f.read(), before), True)
    lt.check('closed: the edit still in the editor\'s tab', '# edited, not saved' in (shown(window) or ''), True)

    # Run with the edit not saved: asked (answered here); the definition held still (no compute)
    doc.Enabled = False
    ask = scripts.GetField('AskSave', FLAGS)
    real_ask = ask.GetValue(None)
    in_grasshopper = scripts.GetMethod('InGrasshopper', FLAGS)
    answers = []

    def run(answer):
        def answered(path):
            answers.append(os.path.basename(path))
            return answer
        ask.SetValue(None, System.Func[System.String, Rhino.UI.ShowMessageResult](answered))
        args = System.Array[System.Object]([System.Array[System.Object]([window]), False, None])
        return in_grasshopper.Invoke(None, args)

    try:
        lt.check('Run, No: taken over (the file computed as saved)', run(Rhino.UI.ShowMessageResult.No), True)
        with open(spiral_file, encoding='utf-8') as f:
            lt.check('Run, No: the file not written', same(f.read(), before), True)
        lt.check('Run, No: asked about the file, said', (answers[-1:], log_has('aren\'t saved')), ([os.path.basename(spiral_file)], True))
        lt.check('Run, Cancel: taken over (nothing run)', run(Rhino.UI.ShowMessageResult.Cancel), True)
        with open(spiral_file, encoding='utf-8') as f:
            lt.check('Run, Cancel: the file not written', same(f.read(), before), True)
        run(Rhino.UI.ShowMessageResult.Yes)
        with open(spiral_file, encoding='utf-8') as f:
            lt.check('Run, Yes: saved into the file', '# edited, not saved' in f.read(), True)
    finally:
        ask.SetValue(None, real_ask)
    yield 2
    doc.IsModified = False

    # --- M3: a Panel's script, a refused save, opened again ---------------------------------------------
    doc2 = GH_Document()
    x = mk._place(mk.persistent(Param_Integer(), 'x', {(0,): [GH_Integer(5)]}), 20, 120)
    run_component = mk._place(mk.script('Run', 'Run', '#! python 3\na = 0\n', [('x', GH_ParamAccess.item, 'int')], ['a']), 300, 80)
    run_component.UsingScriptInputParam = True
    source = mk._place(mk.panel('#! python 3\na = x * 2\n', 20, 20, width=200, height=60), 20, 20)
    source.NickName = 'Unsaved check'
    for obj in (x, run_component, source):
        doc2.AddObject(obj, False)
    names = [p.Name for p in run_component.Params.Input]
    run_component.Params.Input[names.index('script')].AddSource(source)
    run_component.Params.Input[names.index('x')].AddSource(x)
    Grasshopper.Instances.DocumentServer.AddDocument(doc2)
    Grasshopper.Instances.ActiveCanvas.Document = doc2  # held still (not enabled): nothing computes
    open_input = scripts.GetMethod('Open', lt.ANY)
    open_input.Invoke(None, [run_component, None, None])
    yield 3
    # This Panel's working copy (the folder may keep earlier runs': other Panels, other ids)
    stem = 'Unsaved_check-' + source.InstanceGuid.ToString('N')[:8]
    working = os.path.join(folder, stem + '.py')
    lt.check('a working copy', os.path.exists(working), True)
    for old in glob.glob(os.path.join(folder, stem + '.unsaved-*')):
        os.remove(old)
    # The Panel changed meanwhile (edited in place), then a save from the editor: refused
    source.SetUserText('#! python 3\na = x * 5\n')
    with open(working, 'w', encoding='utf-8') as f:
        f.write('#! python 3\na = x * 3  # my saved edit\n')
    yield 3
    lt.check('the stale save refused: the Panel keeps its text', source.UserText, '#! python 3\na = x * 5\n')
    lt.check('and said', log_has('changed since its script was opened'), True)
    # Opened again, as the message advises: the Panel's text -- and the saved edit kept, said
    open_input.Invoke(None, [run_component, None, None])
    yield 3
    with open(working, encoding='utf-8') as f:
        lt.check('opened again: the working copy has the Panel\'s text', same(f.read(), '#! python 3\na = x * 5\n'), True)
    kept = glob.glob(os.path.join(folder, stem + '.unsaved-*'))
    kept_texts = [open(k, encoding='utf-8').read() for k in kept]
    lt.check('the refused save kept in a file of its own', any('my saved edit' in t for t in kept_texts), True)
    lt.check('and said', log_has('wasn\'t taken over', last=10), True)
    for path in kept:
        os.remove(path)
    window = editor('Unsaved_check')
    if window is not None:
        window.Close()
    doc2.IsModified = False


lt.run('t_ghinputpath_unsaved', test)
