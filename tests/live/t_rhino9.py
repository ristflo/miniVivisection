#! python 3
# Rhino 9: the Rhino 9 build loads, finds Rhino 9's Script Editor (its IEditor model) and
# follows its debugger: a pause at a breakpoint (t_rhino9_errorpause: at an uncaught error), Python evaluated in the paused frame (Python
# 3.13), a V°Watch value drawn, RhinoCode's variables through the Rhino 9 shims, the V° menu
# and the tabs (the console tray; t_rhino9_ui checks the rest of the editor UI). Run: python tests/live/run_live.py --rhino 9 t_rhino9
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino
import Eto.Forms

SCRIPT = '''#! python 3
import Rhino.Geometry as rg
pts = []
for k in range(3):
    pts.append(rg.Point3d(k, 2 * k, 0))
crv = rg.PolylineCurve(pts)
print('breakpoint here')
'''


def paused_controls():
    hook = lt.hook()
    return list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))


def call(obj, name, *args):
    methods = [m for m in obj.GetType().GetMethods(lt.ANY) if m.Name == name and len(m.GetParameters()) == len(args)]
    return methods[0].Invoke(obj, list(args))


def test():
    yield 2
    lt.check('Rhino 9', Rhino.RhinoApp.Version.Major, 9)
    lt.check('plug-in loaded', lt.assembly('miniVivisection') is not None, True)
    if lt.assembly('miniVivisection') is None:
        return
    lt.check('the Rhino 9 build', lt.static_get('MiniVivisectionPlugin', 'BuiltForRhino'), 9)
    path = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'rhino9_pause.py')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(SCRIPT)
    # Rhino 9's ScriptEditor command has no Debug option: the script opened in the editor,
    # then the editor's own Debug command (its model's Commands.CodeDebugCommand).
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Open "%s"' % path, False)
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 120)
    yield 5
    model = lt.editors()[0].DataContext
    # A breakpoint on line 7 (print), through the editor's text editor (ITextEditor).
    text_editor = lt.member(lt.member(model, 'Codes'), 'Current').Editor
    text_editor.Goto(Rhino.Runtime.Code.Text.TextPosition(7, 1))
    yield 1
    text_editor.ToggleBreakpoint()
    yield 2
    debug = lt.member(lt.member(model, 'Commands'), 'CodeDebugCommand')
    execute = [m for m in debug.GetType().GetMethods(lt.ANY) if m.Name == 'Execute']
    lt.note('CodeDebugCommand: %s, Execute(%s)' % (debug.GetType().FullName, ', '.join(str(len(m.GetParameters())) for m in execute)))
    m = sorted(execute, key=lambda m: len(m.GetParameters()))[0]
    args = [None] * len(m.GetParameters())
    # Asynchronously: the command runs the script on the UI thread, so its pause would come
    # inside this step, where the test can't look.
    Eto.Forms.Application.Instance.AsyncInvoke(lambda: m.Invoke(debug, args))
    codes = lt.member(model, 'Codes')
    lt.static_call('MiniVivisectionSettings', 'Set', 'LogLevel', 'Debug')
    last = None
    for tick in range(300):
        yield 0.2
        second = tick
        now = (str(codes.DebugControlsState), str(codes.IsRunning), str(Rhino.RhinoApp.CommandPrompt))
        if now == last:
            continue
        last = now
        hooked = lt.member(lt.hook(), '_hookedList')
        states = []
        for ref in hooked:
            ok, target = ref.TryGetTarget()
            states.append(str(target.State) if ok else 'gone')
        lt.note('%5.1f s: editor debugger %s, running %s, prompt %r; followed: %s' % (second * 0.2, now[0], now[1], now[2], ', '.join(states)))
        if paused_controls():
            break
    yield lt.wait_for(lambda: len(paused_controls()) > 0, 10)
    hook = lt.hook()
    lt.check('Script Editor windows found', len(lt.editors()) > 0, True)
    lt.check('editors followed', lt.member(hook, 'EditorCount') > 0, True)
    controls = paused_controls()
    lt.check('paused (at the breakpoint)', len(controls), 1)
    if not controls:
        return
    c = controls[0]
    editor = call(hook, 'EditorOf', c)
    lt.check('the paused debugger\'s editor known', editor is not None, True)

    text = lt.static_call('PythonBridge', 'Exec', 'print(pts[-1])', True).strip()
    lt.check('Python in the paused frame (pts[-1])', text, '2,4,0')
    version = lt.static_call('PythonBridge', 'Exec', 'import sys; print(sys.version_info[:2])', True).strip()
    lt.note('paused script\'s Python: ' + version)

    value = lt.static_call('PythonBridge', 'Eval', 'pts')
    variable = lt.static_call('PythonBridge', 'VariableFor', 'pts', value)
    lt.check('RhinoCode\'s variable for pts (Rhino 9 tracer)', variable is not None, True)
    if variable is not None:
        lt.check('  it can be expanded', lt.member(variable, 'CanExpand'), True)
        members = list(call(variable, 'Expand'))
        lt.note('  pts members: ' + ', '.join(str(lt.member(lt.member(m, 'Id'), 'Identifier')) for m in members[:5]))
        lt.check('  three items', len([m for m in members if str(lt.member(lt.member(m, 'Id'), 'Identifier')).startswith('[')]), 3)

    frame = c.Results.CurrentThread.CurrentFrame
    results = list(lt.static_call('ExecFrameCompat', 'Evaluate', frame))
    names = [str(lt.member(lt.member(r.Result, 'Id'), 'Identifier')) for r in results if r.Result is not None]
    lt.note('frame variables: ' + ', '.join(names[:12]))
    lt.check('the frame\'s variables (GetSlots) include pts and crv', 'pts' in names and 'crv' in names, True)

    lt.check('pts added to V' + chr(0xB0) + 'Watch', call(hook, 'AddRhinoWatch', c, 'crv', None), True)
    yield 2
    watch = call(hook, 'WatchListFor', editor)
    statuses = {str(w.Expression): str(w.Status) for w in lt.member(watch, 'Items')}
    lt.note('V' + chr(0xB0) + 'Watch: ' + repr(statuses))
    lt.check('crv drawn', statuses.get('crv', '').startswith('drawn'), True)

    titles = [str(w.Title) for w in Eto.Forms.Application.Instance.Windows]
    lt.check('the tabs: in the console tray (else the companion window is up)',
             bool(lt.static_call('Rhino9Ui', 'InConsole', editor)) or any(t.startswith('miniVivisection --') for t in titles), True)
    menu = editor.Menu
    lt.check('the V' + chr(0xB0) + ' menu in the editor', menu is not None and any(str(i.Text) == 'V' + chr(0xB0) for i in menu.Items), True)
    lt.note('UserLog: ' + ' | '.join(str(e.Text) for e in list(lt.static_get('UserLog', 'Entries'))[-8:]))
    c.Stop()
    yield 3
    lt.check('stopped', len(paused_controls()), 0)


lt.run('t_rhino9', test)
