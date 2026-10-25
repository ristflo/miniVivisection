#! python 3
# Another definition on the canvas closes the script editors of the one left (setting Grasshopper >
# "Close script editors when the definition changes"): GH 3's Halve, its editor opened; a new
# definition put on the canvas -- its editor closed; switched back, opened again, and the setting
# off: a switch leaves it open.
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
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_DocumentIO, GH_Document
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Normal')  # no hidden editors in the way
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperCloseEditors', True)
    io = GH_DocumentIO()
    lt.check('opens gh3', io.Open(GH3), True)
    doc = io.Document
    server = Grasshopper.Instances.DocumentServer
    canvas = Grasshopper.Instances.ActiveCanvas
    server.AddDocument(doc)
    canvas.Document = doc
    doc.Enabled = True
    yield 2
    halve = [o for o in doc.Objects if o.NickName == 'Halve'][0]
    runs = lt.plugin_type('GrasshopperRuns')
    visible = lambda: runs.GetMethod('HasVisibleEditor', lt.ANY).Invoke(None, [halve])
    runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [halve])
    yield lt.wait_for(visible, 30)
    lt.check('Halve\'s editor open', visible(), True)
    yield 3  # the editor loads

    # A new definition on the canvas (as File > New and File > Open put one there)
    other = GH_Document()
    server.AddDocument(other)
    canvas.Document = other
    yield 2
    lt.check('another definition: the editor of the one left closed', visible(), False)

    # Back, the editor opened again, and the setting off: it stays
    canvas.Document = doc
    yield 1
    runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [halve])
    yield lt.wait_for(visible, 30)
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperCloseEditors', False)
    try:
        canvas.Document = other
        yield 2
        lt.check('the setting off: the editor stays open', visible(), True)
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperCloseEditors', True)
    canvas.Document = doc
    yield 1
    rce = lt.member(lt.member(lt.member(runs.GetMethod('ContextOf', lt.ANY).Invoke(None, [halve]), 'ScriptEditor'), 'CodeEditor'), '_rce')
    rce.Close()
    yield 1
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)
    doc.Enabled = False


lt.run('t_gheditorsclose', test)
