#! python 3
# The changes of 2026-10-17 (afternoon): V Protect (setup, protect, changed on disk and in the
# open definition, Revert, back to setup); V Text's Text output; the plug-in's Grasshopper menu
# items named "V° ..."; V Claude's tools refuse another definition than the answer's; a Panel's
# Markdown opened in the Script Editor shows its source, to edit.
import os
import shutil
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import json
import livetest as lt
import System
import System.Collections.Generic
import Rhino


def ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def gh_type(name):
    return lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.' + name)


def component(name):
    import Grasshopper
    for proxy in Grasshopper.Instances.ComponentServer.ObjectProxies:
        if proxy.Desc.Name.replace(chr(0xB0), ' ').replace(' ', '') == name.replace(' ', '') and not proxy.Obsolete:
            obj = Grasshopper.Instances.ComponentServer.EmitObject(proxy.Guid)
            obj.CreateAttributes()
            return obj
    raise RuntimeError('no component ' + name)


def call(obj, method, *args):
    """An internal method of a plug-in object, by name and argument count."""
    return [m for m in obj.GetType().GetMethods(lt.ANY) if m.Name == method and len(m.GetParameters()) == len(args)][0].Invoke(obj, list(args))


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_DocumentIO, GH_RuntimeMessageLevel
    from Grasshopper.Kernel.Special import GH_Panel, GH_NumberSlider
    import make_gh_examples as mk
    canvas = Grasshopper.Instances.ActiveCanvas
    folder = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'protect')
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(os.path.join(folder, 'a'))
    os.makedirs(os.path.join(folder, 'b'))
    for sub in ('a', 'b'):
        with open(os.path.join(folder, sub, 'notes.txt'), 'w') as f:
            f.write('notes in ' + sub)
    definition = os.path.join(folder, 'protected.gh')

    # --- V Protect: setup -----------------------------------------------------------------------
    doc = GH_Document()
    protect = component('V Protect')
    protect.Attributes.Pivot = System.Drawing.PointF(300, 80)
    slider = mk._place(GH_NumberSlider(), 20, 230)
    slider.SetInitCode('0<3<10')
    for obj in (protect, slider):
        doc.AddObject(obj, False)
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    canvas.Document = doc
    doc.FilePath = definition  # SaveQuiet doesn't set it (File > Save As does)
    lt.check('saved first', GH_DocumentIO(doc).SaveQuiet(definition) and os.path.exists(definition), True)
    doc.Enabled = True
    lt.check('no inputs, no outputs', (protect.Params.Input.Count, protect.Params.Output.Count), (0, 0))
    protect.ExpireSolution(True)  # nothing set: the definition itself, next to it its archive
    yield 2
    default_zip = os.path.join(folder, 'protected.protect.zip')
    lt.check('nothing set: the definition kept, in (its name).protect.zip', (lt.member(protect, 'Files')[0] == definition, os.path.exists(default_zip)), (True, True))
    call(protect, 'SetForTests', System.Collections.Generic.List[System.String]([definition, os.path.join(folder, 'a', 'notes.txt'), os.path.join(folder, 'b', 'notes.txt')]), 'archive.zip', False)
    yield 2
    log = list(lt.member(protect, 'Log'))
    lt.note('log:\n  ' + '\n  '.join(log))
    lt.check('two more added (the same name in two folders: two)', sum(1 for l in log if l.startswith('added')), 3)
    zip_path = os.path.join(folder, 'archive.zip')
    lt.check('the archive set: next to the definition', os.path.exists(zip_path), True)
    remarks = list(protect.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
    lt.check('the balloon says what was stored', any(r.startswith('added: notes.txt') for r in remarks), True)

    # The Markdown fields ({{protected}}, {{modified}}) of the definition's Panels: its V Protect's state
    field_state = lambda d: protect.GetType().GetMethod('FieldState', lt.ANY).Invoke(None, [d]).Signature
    lt.check('fields in setup: not protected, not tracked', field_state(doc), 'not protected|not tracked')

    # --- protect -----------------------------------------------------------------------------------
    call(protect, 'Protect')
    yield 1
    call(protect, 'Look')
    lt.check('protected', lt.member(protect, 'IsProtected'), True)
    lt.check('on disk: the original (saved with the component protecting)', lt.member(protect, 'DiskState'), 'original')
    lt.check('open: unchanged', lt.member(protect, 'OpenModified'), False)
    lt.check('no revert needed', lt.member(protect, 'NeedsRevert'), False)
    lt.check('fields: protected, not modified', field_state(doc), 'protected|not modified')
    lt.check('a protect: link other than revert: said', 'protect:revert' in (protect.GetType().GetMethod('FollowLink', lt.ANY).Invoke(None, [doc, 'other']) or ''), True)
    slider.SetSliderValue(System.Decimal(7))
    slider.ExpireSolution(True)
    yield 2
    call(protect, 'Look')
    lt.check('the open definition changed', lt.member(protect, 'OpenModified'), True)
    lt.check('fields: modified', field_state(doc), 'protected|modified')
    with open(definition, 'ab') as f:
        f.write(b'tampered')
    call(protect, 'Look')
    lt.check('on disk: changed', lt.member(protect, 'DiskState'), 'changed')
    lt.check('revert offered', lt.member(protect, 'NeedsRevert'), True)
    call(protect, 'Revert', False)
    yield 3
    reopened = [d for d in Grasshopper.Instances.DocumentServer if d.FilePath and os.path.normcase(d.FilePath) == os.path.normcase(definition)]
    lt.check('the definition opened again', len(reopened), 1)
    lt.check('the old one is gone', any(d is doc for d in Grasshopper.Instances.DocumentServer), False)
    if reopened:
        again = reopened[0]
        p2 = [o for o in again.Objects if o.ComponentGuid == protect.ComponentGuid][0]
        call(p2, 'Look')
        lt.check('the file on disk: the original again', lt.member(p2, 'DiskState'), 'original')
        lt.check('the reopened definition: unchanged', lt.member(p2, 'OpenModified'), False)
        lt.check('still protected', lt.member(p2, 'IsProtected'), True)
        s2 = [o for o in again.Objects if o.GetType().Name == 'GH_NumberSlider'][0]
        lt.check('the slider as it was', System.Convert.ToDouble(s2.CurrentValue), 3.0)
        call(p2, 'Unprotect')
        lt.check('back to setup', lt.member(p2, 'IsProtected'), False)
        again.Enabled = False

    # --- V Text: the Text output ---------------------------------------------------------------------
    doc2 = GH_Document()
    vtext = component('V Text')
    vtext.Attributes.Pivot = System.Drawing.PointF(300, 80)
    note = mk.panel('# Title\n\nSome **bold** text.\n', 20, 40, width=200, height=80)
    doc2.AddObject(note, False)
    doc2.AddObject(vtext, False)
    vtext.Params.Input[0].AddSource(note)
    Grasshopper.Instances.DocumentServer.AddDocument(doc2)
    canvas.Document = doc2
    doc2.Enabled = True
    doc2.NewSolution(False)
    yield 2
    lt.check('V Text: Text, Decorators, Bounds, Anchor', [o.Name for o in vtext.Params.Output], ['Text', 'Decorators', 'Bounds', 'Anchor'])
    kinds = set(g.GetType().Name for g in vtext.Params.Output[0].VolatileData.AllData(True))
    decorators = set(g.GetType().Name for g in vtext.Params.Output[1].VolatileData.AllData(True))
    lt.note('V Text: Text %r, Decorators %r' % (kinds, decorators))
    lt.check('Text: text objects only', kinds, {'GH_TextEntity'})
    lt.check('Decorators: no text', 'GH_TextEntity' in decorators, False)
    lt.check('their previews off (the component draws the text)', (vtext.Params.Output[0].Hidden, vtext.Params.Output[1].Hidden), (True, True))

    # --- menu items: "V° ..." ----------------------------------------------------------------------------
    menu = System.Windows.Forms.ContextMenuStrip()
    note.AppendMenuItems(menu)
    texts = [i.Text for i in menu.Items if hasattr(i, 'Text') and i.Text]
    ours = [t for t in texts if 'Show as' in t or 'Edit the' in t]
    lt.note('Panel menu: %r' % ours)
    mark = lt.static_get('VNames', 'Mark')
    lt.check('the Panel\'s items start with "V' + mark + ' "', bool(ours) and all(t.startswith('V' + mark + ' ') for t in ours), True)
    lt.check('no "miniVivisection: " left', any(t.startswith('miniVivisection: ') for t in texts), False)

    # --- the answer's definition: tools refuse another one ------------------------------------------
    key_field = lt.plugin_type('GrasshopperBridge').GetField('AnswerFor', lt.ANY)
    key_field.SetValue(None, 'some other definition')
    answer = lt.static_call('GrasshopperBridge', 'Call', 'gh_get_selection', System.Text.Json.JsonDocument.Parse('{}').RootElement)
    lt.check('a tool refuses while the canvas shows another definition', 'switched Grasshopper to another definition' in answer, True)
    key_field.SetValue(None, None)
    answer = lt.static_call('GrasshopperBridge', 'Call', 'gh_get_selection', System.Text.Json.JsonDocument.Parse('{}').RootElement)
    lt.check('and works between answers', answer.startswith('Definition:'), True)

    # --- a Panel's Markdown in the Script Editor: its source ------------------------------------------
    lt.plugin_type('GrasshopperInputScripts').GetMethod('OpenPanel', lt.ANY).Invoke(None, [note, '.md'])
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 60)
    yield 6
    editor = lt.editors()[0]
    access = System.Activator.CreateInstance(lt.plugin_type('ScriptEditorAccess'), [editor])
    code = lt.member(access, 'Code')
    codes = editor.GetType().GetProperty('Codes', lt.ANY).GetValue(editor)
    editors = codes.GetType().GetProperty('Editors', lt.ANY).GetValue(codes)
    lt.check('the Markdown shows as its source, to edit', editors.GetType().GetMethod('IsEditing', lt.ANY).Invoke(editors, [code]), True)
    doc2.Enabled = False


lt.run('t_ghbatch3', test)
