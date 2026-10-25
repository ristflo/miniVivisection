#! python 3
# The prompts window in Grasshopper (Plan_Grasshopper_Prompts.md): its toolbar button and tabs,
# every tool of the grasshopper server (miniVivisection.GH's GrasshopperTools) on a small
# definition, the undo step per answer (ClaudeUndo: change, add, remove, rewire -- one Ctrl+Z
# takes it all back, Redo brings it again), V Python's Grasshopper helpers, V PowerShell's
# folder, and what Claude may use (GrasshopperChat.Permissions).
import json
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

DOUBLE = '''#! python 3
a = x * 2
'''


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def gh_type(name):
    return lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.' + name)


def call(tool, **args):
    answer = gh_type('GrasshopperTools').GetMethod('Call').Invoke(None, [tool, json.dumps(args)])
    lt.progress('%s -> %s' % (tool, answer[:160].replace('\n', ' | ')))
    return answer


def stock(name):
    import Grasshopper
    for proxy in Grasshopper.Instances.ComponentServer.ObjectProxies:
        if proxy.Desc.Name == name and not proxy.Obsolete:
            obj = Grasshopper.Instances.ComponentServer.EmitObject(proxy.Guid)
            obj.CreateAttributes()
            return obj
    raise RuntimeError('no component ' + name)


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    yield 4  # the toolbar is looked for every 3 s
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess
    from Grasshopper.Kernel.Special import GH_NumberSlider, GH_Panel, GH_BooleanToggle
    from Grasshopper.Kernel.Parameters import Param_Number, Param_Curve
    import make_gh_examples as mk

    # --- the toolbar button and the window ------------------------------------------------
    editor = Grasshopper.Instances.DocumentEditor
    toolbar = editor.GetType().GetProperty('CanvasToolbar', lt.ANY).GetValue(editor)
    lt.check('the prompts button is on the toolbar', toolbar.Items.ContainsKey('miniVivisectionPromptsButton'), True)

    doc = GH_Document()
    slider = mk._place(GH_NumberSlider(), 20, 40)
    slider.SetInitCode('0<3<10')
    slider.NickName = 'Radius'
    circle = stock('Circle')
    circle.Attributes.Pivot = System.Drawing.PointF(240, 60)
    double = mk._place(mk.script('Double', 'Double', DOUBLE, [('x', GH_ParamAccess.item, 'double')], ['a']), 240, 220)
    numbers = Param_Number()
    numbers.CreateAttributes()
    numbers.NickName = 'Numbers'
    numbers.Attributes.Pivot = System.Drawing.PointF(20, 300)
    curves = Param_Curve()
    curves.CreateAttributes()
    curves.NickName = 'Curves'
    curves.Attributes.Pivot = System.Drawing.PointF(20, 380)
    panel = mk._place(GH_Panel(), 480, 40)
    panel.UserText = 'old text'
    toggle = mk._place(GH_BooleanToggle(), 20, 460)
    for obj in (slider, circle, double, numbers, curves, panel, toggle):
        doc.AddObject(obj, False)
    circle.Params.Input[1].AddSource(slider)
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    Grasshopper.Instances.ActiveCanvas.Document = doc
    doc.Enabled = True
    doc.NewSolution(False)
    yield 2

    lt.static_call('GrasshopperPrompts', 'Show', 'claude')
    yield 1
    window = lt.static_get('GrasshopperPrompts', 'Window')
    lt.check('the window is open', window is not None and window.Visible, True)
    titles = list(lt.static_get('GrasshopperPrompts', 'TabTitles'))
    lt.check('its tabs', [t.replace(chr(0xB0), ' ').replace(' ', '') for t in titles], ['VClaude', 'VPython', 'VPowerShell'])
    owner = window.GetType()  # owned by Grasshopper's window (Win32): its owner handle
    import ctypes
    hwnd = window.NativeHandle
    owner_handle = ctypes.windll.user32.GetWindow(ctypes.c_void_p(hwnd.ToInt64()), 4)  # GW_OWNER
    lt.check('owned by Grasshopper\'s window', owner_handle == editor.Handle.ToInt64(), True)

    # --- the tools: reading ------------------------------------------------------------------
    doc.SelectAll()
    slider.Attributes.Selected = True
    for o in doc.Objects:
        o.Attributes.Selected = o in (slider, circle)
    answer = call('gh_get_selection')
    lt.check('selection: both, with ids', str(slider.InstanceGuid) in answer and str(circle.InstanceGuid) in answer, True)
    lt.check('selection: the slider\'s range', 'slider: 3' in answer and 'max 10' in answer, True)
    lt.check('selection: the circle\'s wire from the slider', "<- Number Slider 'Radius' [" + str(slider.InstanceGuid) + ']' in answer, True)
    context = gh_type('GrasshopperTools').GetMethod('Context').Invoke(None, [True])
    lt.check('the context line names the selection', 'selected: ' in context and 'Radius' in context, True)
    answer = call('gh_get_data', id=str(circle.InstanceGuid), param='C')
    lt.check('data: the circle with its radius', 'radius 3' in answer, True)

    # --- setting values ------------------------------------------------------------------------
    answer = call('gh_set_value', id=str(slider.InstanceGuid), value=7, min=0, max=20)
    lt.check('slider set', (System.Convert.ToDouble(slider.CurrentValue), System.Convert.ToDouble(slider.Slider.Maximum)), (7.0, 20.0))
    yield 1
    lt.check('the circle followed', 'radius 7' in call('gh_get_data', id=str(circle.InstanceGuid), param='C'), True)
    call('gh_set_value', id=str(panel.InstanceGuid), value='# Note\n\nnew text', show_as='Markdown')
    show_as = gh_type('ScriptPanels').GetMethod('ShowAsOf', lt.ANY).Invoke(None, [panel])
    lt.check('panel text and Markdown', (panel.UserText, str(show_as)), ('# Note\n\nnew text', 'Markdown'))
    call('gh_set_value', id=str(toggle.InstanceGuid), value=True)
    lt.check('toggle set', toggle.Value, True)
    call('gh_set_value', id=str(numbers.InstanceGuid), values=[1, 2.5, 4])
    yield 1
    lt.check('stored numbers', [g.Value for g in numbers.VolatileData.AllData(True)], [1.0, 2.5, 4.0])
    line = Rhino.RhinoDoc.ActiveDoc.Objects.AddLine(Rhino.Geometry.Line(Rhino.Geometry.Point3d(0, 0, 0), Rhino.Geometry.Point3d(10, 0, 0)))
    call('gh_set_value', id=str(curves.InstanceGuid), rhino_objects=[str(line)])
    yield 1
    lt.check('a Rhino curve referenced', 'length 10' in call('gh_get_data', id=str(curves.InstanceGuid)), True)

    # --- scripts ---------------------------------------------------------------------------------
    answer = call('gh_set_script', id=str(double.InstanceGuid), code='#! python 3\nc = x + y\n',
                  inputs=[{'name': 'x'}, {'name': 'y', 'access': 'item'}], outputs=['c'])
    lt.check('script: inputs x, y', [p.NickName for p in double.Params.Input], ['x', 'y'])
    lt.check('script: output c (and out)', [p.NickName for p in double.Params.Output if p.NickName != 'out'], ['c'])
    call('gh_set_value', id=str(double.InstanceGuid), param='x', values=[40])
    call('gh_set_value', id=str(double.InstanceGuid), param='y', values=[2])
    yield 2
    lt.check('the script ran: 42', 'number 42' in call('gh_get_data', id=str(double.InstanceGuid), param='c') or
             'integer 42' in call('gh_get_data', id=str(double.InstanceGuid), param='c'), True)
    lt.check('gh_get_script has the code', 'c = x + y' in call('gh_get_script', id=str(double.InstanceGuid)), True)

    # --- editing, wires, notes, the canvas image -------------------------------------------------
    call('gh_edit_objects', ids=[str(circle.InstanceGuid)], nickname='Ring', enabled=False, move_by=[10, 0])
    lt.check('nickname, disabled, moved', (circle.NickName, circle.Locked, round(circle.Attributes.Pivot.X)), ('Ring', True, 250))
    call('gh_edit_objects', ids=[str(circle.InstanceGuid)], enabled=True, group='Circle part')
    groups = [o for o in doc.Objects if o.GetType().Name == 'GH_Group']
    lt.check('grouped', [g.NickName for g in groups], ['Circle part'])
    call('gh_disconnect', id=str(circle.InstanceGuid), param='R')
    lt.check('the wire is gone', circle.Params.Input[1].SourceCount, 0)
    answer = call('gh_add_note', text='# What this does\n\n- a circle\n- a sum', near=[str(double.InstanceGuid)])
    notes = [o for o in doc.Objects if o.GetType().Name == 'GH_Panel' and o.NickName == 'Note']
    lt.check('a note, right of the script', len(notes) == 1 and notes[0].Attributes.Pivot.X > double.Attributes.Bounds.Right, True)
    lt.check('the note is Markdown', str(gh_type('ScriptPanels').GetMethod('ShowAsOf', lt.ANY).Invoke(None, [notes[0]])), 'Markdown')
    answer = call('gh_capture_canvas')
    import re
    path = re.search(r'([A-Za-z]:\\[^ ]+?\.png)', answer).group(1)
    lt.check('a canvas image', os.path.exists(path) and os.path.getsize(path) > 2000, True)

    # --- one undo step per answer ------------------------------------------------------------------
    undo = gh_type('ClaudeUndo')
    before = undo.GetMethod('Begin').Invoke(None, None)
    call('gh_set_value', id=str(slider.InstanceGuid), value=12)
    call('gh_add_note', text='temporary', x=600, y=600)
    added = stock('Area')
    added.Attributes.Pivot = System.Drawing.PointF(500, 300)
    doc.AddObject(added, False)
    added.Params.Input[0].AddSource(circle.Params.Output[0])  # wired from an existing object
    doc.RemoveObject(toggle, False)                             # what RhinoAI's g1_delete_component does
    circle.Params.Input[1].AddSource(slider)                    # rewired
    toggle_id = toggle.InstanceGuid
    count = doc.ObjectCount
    summary = undo.GetMethod('Commit').Invoke(None, [before, 'V' + chr(0xB0) + 'Claude: test'])
    lt.check('the step says what changed', summary, '2 changed, 2 added, 1 removed')
    lt.check('one undo record, named', doc.UndoServer.UndoNames[0], 'V' + chr(0xB0) + 'Claude: test')
    doc.UndoServer.PerformUndo()
    yield 1
    lt.check('undo: the slider back at 7', System.Convert.ToDouble(slider.CurrentValue), 7.0)
    lt.check('undo: the added objects gone', doc.ObjectCount, count - 2 + 1)
    back = doc.FindObject(toggle_id, True)
    lt.check('undo: the removed toggle back', back is not None and back.Value, True)
    lt.check('undo: the rewiring undone', circle.Params.Input[1].SourceCount, 0)
    doc.UndoServer.PerformRedo()
    yield 1
    lt.check('redo: all of it again', (doc.ObjectCount, System.Convert.ToDouble(slider.CurrentValue), doc.FindObject(toggle_id, True) is None, circle.Params.Input[1].SourceCount),
             (count, 12.0, True, 1))
    added_back = [o for o in doc.Objects if o.Name == 'Area']
    lt.check('redo: the added component wired again', len(added_back) == 1 and added_back[0].Params.Input[0].SourceCount == 1, True)
    lt.check('UndoLast refuses a record that isn\'t Claude\'s', 'not Claude' in undo.GetMethod('UndoLast').Invoke(None, ['XYZ: ']), True)

    # --- V Python and V PowerShell in the window ------------------------------------------------
    python = lt.static_get('GrasshopperPrompts', 'Python')
    lt.static_call('GrasshopperPrompts', 'Show', 'python')
    python.GetType().GetMethod('Type', lt.ANY).Invoke(python, ['print(ghdoc() is not None, len(ghfind("Radius")))'])
    yield 2
    view = lt.member(python, '_view')
    shown = lt.member(view, 'TextNow')
    lt.check('V Python: ghdoc() and ghfind()', 'True 1' in shown, True)
    lt.static_call('GrasshopperPrompts', 'Show', 'powershell')
    yield 4
    shell = lt.static_get('GrasshopperPrompts', 'Shell')
    shell_view = lt.member(shell, '_view')
    shell_view.GetType().GetMethod('Type', lt.ANY).Invoke(shell_view, ['(Get-Location).Path'])
    yield 4
    documents = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments)
    lt.check('V PowerShell starts in Documents (an unsaved definition)', documents in lt.member(shell_view, 'TextNow'), True)

    # --- what Claude may use -----------------------------------------------------------------------
    chat = lt.static_get('GrasshopperPrompts', 'Claude')
    chat.GetType().GetMethod('McpConfig', lt.ANY).Invoke(chat, [False])
    has_rhino = lt.member(chat, '_hasRhinoServer')
    lt.note('RhinoAI router found: %r' % has_rhino)
    permissions = chat.GetType().GetMethod('Permissions', lt.ANY)
    result = permissions.Invoke(chat, [False, False])
    allowed, disallowed, system = list(result.Item1), list(result.Item2), result.Item3
    lt.check('our tools allowed', 'mcp__grasshopper' in allowed and 'Read' in allowed, True)
    if has_rhino:
        lt.check('RhinoAI: g1 placing allowed', 'mcp__rhino__g1_place_component' in allowed, True)
        lt.check('RhinoAI: g2 never', 'mcp__rhino__g2_place_component' in disallowed and not any('g2_' in a for a in allowed), True)
        lt.check('RhinoAI: run_python not without /allow', 'mcp__rhino__run_python' in disallowed, True)
        result = permissions.Invoke(chat, [True, True])
        allowed_ro, disallowed_ro = list(result.Item1), list(result.Item2)
        lt.check('read only: no g1 placing, no run_python', 'mcp__rhino__g1_place_component' in disallowed_ro and 'mcp__rhino__run_python' in disallowed_ro, True)
        lt.check('read only: the graph and the view still', 'mcp__rhino__g1_get_canvas_graph' in allowed_ro and 'mcp__rhino__get_viewport_image' in allowed_ro, True)
        lt.check('read only: told so', 'Read only' in result.Item3, True)
    tools = list(lt.static_call('GrasshopperBridge', 'Tools', True, True))
    lt.check('read-only tool list', sorted(t.Name for t in tools), ['gh_capture_canvas', 'gh_get_data', 'gh_get_script', 'gh_get_selection'])

    # the settings tab
    tabs = set(s.Tab for s in lt.static_get('MiniVivisectionSettings', 'All'))
    lt.check('the settings have three tabs', sorted(tabs), ['General', 'Grasshopper', 'Rhino'])
    view_type = lt.plugin_type('SettingsView')
    for immediate, narrow in ((True, True), (False, False)):
        settings_view = System.Activator.CreateInstance(view_type, [immediate, narrow])
        control = lt.member(settings_view, 'Control')
        pages = [p.Text for p in control.Pages] if control.GetType().Name == 'TabControl' else []
        lt.check('the settings view (%s) has three tabs' % ('V Settings' if narrow else 'Options'), pages, ['General', 'Rhino', 'Grasshopper'])
    lt.static_call('MiniVivisectionSettings', 'Set', 'GhPromptPowerShell', False)
    yield 1
    lt.check('a tab turned off goes', len(list(lt.static_get('GrasshopperPrompts', 'TabTitles'))), 2)
    lt.static_call('MiniVivisectionSettings', 'Set', 'GhPromptPowerShell', True)
    yield 1
    lt.check('and comes back', len(list(lt.static_get('GrasshopperPrompts', 'TabTitles'))), 3)
    doc.Enabled = False


lt.run('t_ghprompts', test)
