#! python 3
# A Panel's script that feeds a script component's script input (GH 9: "Twist script" -> Twist),
# opened from the Panel's menu ("Edit the script in the Script Editor"): opened as the
# component's script input -- the editor's code given the component's inputs (n, angle) --, and
# the Script Editor's Run computes it in Grasshopper with them, the editor's text taken into the
# Panel first (not run as a Rhino script, which has no n).
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

LESSON = os.path.join(lt.ROOT, r'examples\grasshopper\gh9_script_from_a_panel.gh')


def gh_ready():
    asm = lt.assembly('Grasshopper')
    return asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None and lt.assembly('miniVivisection.GH') is not None


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(gh_ready, 120)
    import Grasshopper
    Grasshopper.Instances.DocumentEditor.ScriptAccess_OpenDocument(LESSON)
    yield 3
    doc = Grasshopper.Instances.ActiveCanvas.Document
    panel = next(o for o in doc.Objects if o.NickName == 'Twist script')
    twist = next(o for o in doc.Objects if o.NickName == 'Twist')
    flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    scripts = lt.plugin_type('GrasshopperInputScripts')
    fed = scripts.GetMethod('FedComponent', flags).Invoke(None, [panel])
    lt.check('the Panel feeds Twist\'s script input', fed is not None and fed.InstanceGuid == twist.InstanceGuid, True)

    # The Panel's menu: its script, as Twist's script input
    scripts.GetMethod('OpenPanel', flags).Invoke(None, [panel, '.py'])
    access = lt.plugin_type('ScriptEditorAccess')
    folder = scripts.GetProperty('Folder', flags).GetValue(None)

    def editor():
        import Eto.Forms
        for window in Eto.Forms.Application.Instance.Windows:
            if window.GetType().FullName != 'RhinoCodeEditor.Editor.RCE':
                continue
            path = System.Activator.CreateInstance(access, [window]).ScriptPath or ''
            if os.path.normcase(os.path.dirname(path)) == os.path.normcase(folder) and 'Twist_script' in path:
                return window
        return None
    yield lt.wait_for(lambda: editor() is not None, 30)
    window = editor()
    lt.check('open in the Script Editor (the working copy)', window is not None, True)
    if window is None:
        return
    yield 2
    code = System.Activator.CreateInstance(access, [window]).Code
    names = [p.Name for p in code.Inputs]
    lt.note('the editor\'s inputs: %s' % names)
    lt.check('the editor knows Twist\'s inputs', ('n' in names, 'angle' in names), (True, True))

    # Run: in Grasshopper, the editor's text taken into the Panel first
    text = System.Activator.CreateInstance(access, [window]).ScriptText
    code.Text.Set(text.rstrip('\n') + '\n# edited in the Script Editor\n')
    task = System.Threading.Tasks.Task.CompletedTask
    ran = scripts.GetMethod('InGrasshopper', flags).Invoke(None, [System.Array[System.Object]([window]), False, task])
    lt.check('Run: computed in Grasshopper (not as a Rhino script)', ran, True)
    yield 3
    lt.check('the Panel has the editor\'s text', '# edited in the Script Editor' in panel.UserText, True)
    errors = list(twist.RuntimeMessages(Grasshopper.Kernel.GH_RuntimeMessageLevel.Error))
    lt.check('Twist computed with its inputs, no error', (errors, twist.Params.Output[0].VolatileDataCount > 0), ([], True))
    # A Panel's script on its own (not feeding a script input) stays a Rhino script: Run is the editor's
    doc.Enabled = False
    doc.IsModified = False


lt.run('t_panelscriptrun', test)
