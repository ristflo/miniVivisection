#! python 3
# V°Help in a Grasshopper component's script editor: the manual opens there, as a tab (RhinoCode's
# Markdown started first -- that editor starts only Python), not in Windows' app for .md.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

GH3 = os.path.join(lt.ROOT, r'examples\grasshopper\gh3_stop_a_hang.gh')


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def test():
    yield 2
    from Rhino.Runtime.Code import RhinoCode
    from Rhino.Runtime.Code.Languages import LanguageSpec
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_DocumentIO
    io = GH_DocumentIO()
    io.Open(GH3)
    doc = io.Document
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    Grasshopper.Instances.ActiveCanvas.Document = doc
    halve = [o for o in doc.Objects if o.NickName == 'Halve'][0]
    runs = lt.plugin_type('GrasshopperRuns')
    runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [halve])
    yield lt.wait_for(lambda: runs.GetMethod('HasVisibleEditor', lt.ANY).Invoke(None, [halve]), 60)
    yield 4
    lt.note('Markdown started before V' + chr(0xB0) + 'Help: %s' % (RhinoCode.Languages.QueryLatest(LanguageSpec.Markdown) is not None))
    context = runs.GetMethod('ContextOf', lt.ANY).Invoke(None, [halve])
    window = lt.member(lt.member(lt.member(context, 'ScriptEditor'), 'CodeEditor'), '_rce')
    lt.plugin_type('HelpButton').GetMethod('Open', lt.ANY).Invoke(None, [window])
    yield 4
    lt.check('Markdown started', RhinoCode.Languages.QueryLatest(LanguageSpec.Markdown) is not None, True)
    access = System_access(window)
    active = str(access.Code.Uri) if access.Code is not None else ''
    lt.note('the editor shows: %s' % active)
    lt.check('the manual is the component editor\'s tab now', active.endswith('miniVivisection_Manual.md'), True)
    entries = [e.Text for e in list(lt.static_get('UserLog', 'Entries'))]
    lt.check('not opened with Windows\' app', any("Can't open the manual" in t for t in entries), False)
    doc.Enabled = False


def System_access(window):
    access_type = lt.plugin_type('ScriptEditorAccess')
    import System
    return System.Activator.CreateInstance(access_type, [window])


lt.run('t_vhelp_gh', test)
