#! python 3
# IronPython 2 (#! python2) scripts through IronPythonBridge: a script paused in a function
# (pause 1) and after the call (pause 2). Expressions in watches and the Watch tab, conditions
# and logpoints as real Python, the hover tooltip, Edit in Rhino written back into a global and
# into the function's local, the Python tab's input in the frame, raw data (lists of triples)
# drawn as points, values at the ends of the lines -- and the local write reaching the running
# function, seen at pause 2.
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

SCRIPT = '''#! python2
import Rhino.Geometry as rg
front = [[0, 0, 0], [5, 0, 4], [10, 0, 0]]
middle = [[0, 6, 0], [5, 6, 50], [10, 6, 0]]
pt = rg.Point3d(1, 2, 3)
result = None
moved = None


def profile(points):
    n = len(points)
    scale = 1.0
    anchor = rg.Point3d(points[1][0], points[1][1], 0)
    top = [p for p in points if p[2] > 10]   # <-- BREAKPOINT 1
    return [rg.Point3d(p[0], p[1], p[2] * scale) for p in points], anchor


sections, moved = profile(middle)
result = sections[1].Z
done = True   # <-- BREAKPOINT 2
'''
LOG = os.path.join(os.environ.get('TEMP', '.'), 'rhinovivisection', 'minivivisection', 'minivivisection.log')


def line_of(mark):
    return [i + 1 for i, l in enumerate(SCRIPT.split('\n')) if mark in l][0]


def paused_controls():
    hook = lt.hook()
    return list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))


def try_evaluate(controls, expression):
    """ScriptEditorHook.TryEvaluate(controls, expression, out value, out error) -> (found, value, error)."""
    t = lt.plugin_type('ScriptEditorHook')
    m = [m for m in t.GetMethods(lt.ANY) if m.Name == 'TryEvaluate' and len(m.GetParameters()) == 4
         and m.GetParameters()[0].ParameterType.Name == 'IDebugControls'][0]
    args = System.Array[System.Object]([controls, expression, None, None])
    found = m.Invoke(None, args)
    return found, args[2], args[3]


def decide(controls, frame, condition='', log=''):
    """BreakpointConditions.Decide with a spec: (pause or not, the condition's error)."""
    spec = System.Activator.CreateInstance(lt.plugin_type('BreakpointSpec'))
    spec.Condition = condition
    spec.LogMessage = log
    uri = frame.Reference.Uri
    line = frame.Reference.Position.LineNumber
    return lt.static_call('BreakpointConditions', 'Decide', spec, frame, uri, line, controls), spec.LastError


def log_lines(word):
    try:
        with open(LOG, encoding='utf-8', errors='replace') as f:
            return [l.rstrip() for l in f if word in l]
    except OSError:
        return []


def bridge_exec(frame, source):
    return lt.static_call('IronPythonBridge', 'Exec', frame, source)


def bridge_run(frame, source):
    result = lt.static_call('IronPythonBridge', 'Run', frame, source)
    return result.Item1, result.Item2


def point_of(value):
    return None if value is None else (value.X, value.Y, value.Z)


def test():
    yield 2
    path = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'ipy_probe.py')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(SCRIPT)

    # The editor with the script and two breakpoints, then its debug run
    Rhino.RhinoApp.RunScript('_ScriptEditor', False)
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 60)
    editor = lt.editors()[0]
    state = lt.member(editor, 'ActiveState')
    code = Rhino.Runtime.Code.RhinoCode.CreateCode(System.Uri(path))
    add = [m for m in state.GetType().GetMethods(lt.ANY) if m.Name == 'AddCode' and len(m.GetParameters()) == 2
           and m.GetParameters()[0].ParameterType.FullName == 'Rhino.Runtime.Code.Code'][0]
    add.Invoke(state, [code, System.Activator.CreateInstance(add.GetParameters()[1].ParameterType, True)])
    yield 3
    active = lt.member(lt.member(state, 'ActiveCode'), 'Code')
    lt.check('the editor took it as IronPython 2', active.GetType().Name, 'McNeelPython2Code')
    from Rhino.Runtime.Code.Execution.Debugging import CodeReferenceBreakpoint
    active.DebugControls.Breakpoints.Clear()  # the editor keeps a file's breakpoints across sessions: an earlier run's
    active.DebugControls.Breakpoints.Add(CodeReferenceBreakpoint(active, line_of('BREAKPOINT 1')))
    active.DebugControls.Breakpoints.Add(CodeReferenceBreakpoint(active, line_of('BREAKPOINT 2')))
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_On "%s"' % path, False)
    yield lt.wait_for(lambda: len(paused_controls()) > 0, 90)
    controls = paused_controls()
    lt.check('paused at breakpoint 1', len(controls), 1)
    if not controls:
        return
    controls = controls[0]
    frame = controls.Results.CurrentThread.CurrentFrame
    lt.check('in profile(), line of BREAKPOINT 1', frame.Reference.Position.LineNumber, line_of('BREAKPOINT 1'))
    lt.check('the kind: IronPython', str(lt.static_call('ScriptKinds', 'Of', frame.Reference.Uri, None)), 'IronPython')
    lt.check('an IronPython pause (the bridge reaches its frame)', lt.static_call('ScriptEditorHook', 'IsIronPythonPause', controls), True)
    lt.check('not a Python 3 pause', lt.static_call('ScriptEditorHook', 'IsPythonPause', controls), False)

    # Plain names and expressions, evaluated in the paused frame by IronPython itself
    lt.check('plain local n = 3', try_evaluate(controls, 'n')[:2], (True, 3))
    lt.check('plain global pt: a Point3d', (try_evaluate(controls, 'pt')[0], type(try_evaluate(controls, 'pt')[1]).__name__), (True, 'Point3d'))
    lt.check('expression points[1][2] = 50', try_evaluate(controls, 'points[1][2]')[:2], (True, 50))
    lt.check('expression len(points) * n = 9', try_evaluate(controls, 'len(points) * n')[:2], (True, 9))
    lt.check('expression with a comprehension', list(try_evaluate(controls, '[p[2] for p in points]')[1]), [0, 50, 0])
    found, value, error = try_evaluate(controls, 'nope + 1')
    lt.check('an unknown name: not found, Python\'s error', (found, error), (False, "NameError: name 'nope' is not defined"))

    # The editor's Watch tab: an expression's row
    hook = lt.hook()
    watch_var = hook.GetType().GetMethod('TryEvaluateWatch', lt.ANY)
    from Rhino.Runtime.Code.Execution.Debugging import DebugExpression
    args = System.Array[System.Object]([frame, DebugExpression('points[1]'), None])
    lt.check('Watch tab: points[1] has a row', watch_var.Invoke(hook, args), True)
    row = args[2]
    lt.check('its value, as RhinoCode shows a list', row.GetValueRepr() if row is not None else None, '3 Item(s)')
    lt.check('its items', [v.GetValueRepr() for v in row.Expand()] if row is not None else None, ['5', '6', '50'])
    lt.check('its type: RhinoCode\'s own IronPython variable', row.GetType().Name if row is not None else None, 'PyExecVariable')
    args = System.Array[System.Object]([frame, DebugExpression('nope'), None])
    watch_var.Invoke(hook, args)
    lt.check('Watch tab: an error row', args[2].GetValueRepr() if args[2] is not None else None, "NameError: name 'nope' is not defined")

    # Conditions and logpoints: real Python, in the frame
    lt.check('condition n == 3 holds', decide(controls, frame, 'n == 3'), (True, None))
    lt.check('condition n == 4 does not', decide(controls, frame, 'n == 4'), (False, None))
    lt.check('condition with len() and indexing', decide(controls, frame, 'len(points) == 3 and points[1][2] > 10'), (True, None))
    lt.check('condition with a generator', decide(controls, frame, 'any(p[2] > 10 for p in points)'), (True, None))
    lt.check('a failing condition pauses, with Python\'s error', decide(controls, frame, 'nope > 1'), (True, "NameError: name 'nope' is not defined"))
    lt.check('logpoint: never pauses', decide(controls, frame, '', 'n={n} count={len(points)} first={points[0]} bad={nope}')[0], False)
    yield 1
    logged = [l for l in log_lines('Logpoint, line') if 'count=' in l]
    lt.check('logpoint filled in from the frame, an error in <>', logged[-1].endswith("n=3 count=3 first=[0, 6, 0] bad=<NameError: name 'nope' is not defined>") if logged else logged, True)

    # The hover tooltip: expressions too, with Edit in Rhino for geometry
    build = lt.plugin_type('HoverValues').GetMethod('BuildTip', lt.ANY)
    tip = build.Invoke(None, [controls, 'points[1][2]', line_of('BREAKPOINT 1'), None, False, None]) or ''
    lt.check('tooltip on points[1][2]: its value, as Python', 'points[1][2] = 50' in tip and '```python' in tip, True)
    tip = build.Invoke(None, [controls, 'anchor', line_of('BREAKPOINT 1'), None, False, None]) or ''
    lt.check('tooltip on anchor offers Edit in Rhino', 'Edit in Rhino' in tip, True)

    # V Watch: geometry is drawn; raw data isn't (the pipeline is Python 3's)
    watch_list = hook.GetType().GetMethod('WatchListFor', lt.ANY).Invoke(hook, [editor])
    items = lt.member(watch_list, 'Items')
    while items.Count > 0:
        items.RemoveAt(0)  # earlier tests' watches (kept per editor)
    lt.check('watch pt added', hook.GetType().GetMethod('AddRhinoWatch', lt.ANY).Invoke(hook, [controls, 'pt', None]), True)
    lt.check('watch anchor added', hook.GetType().GetMethod('AddRhinoWatch', lt.ANY).Invoke(hook, [controls, 'anchor', None]), True)
    hook.GetType().GetMethod('AddRhinoWatch', lt.ANY).Invoke(hook, [controls, 'points', None])
    yield 2
    statuses = dict((w.Expression, w.Status) for w in lt.member(watch_list, 'Items'))
    lt.note('watch statuses: %r' % statuses)
    lt.check('pt is drawn', 'drawn' in statuses.get('pt', ''), True)
    lt.check('anchor (a local) is drawn', 'drawn' in statuses.get('anchor', ''), True)
    lt.check('points (raw triples) are drawn as points', statuses.get('points', '').startswith('drawn as points'), True)
    lt.check('raw data isn\'t edited, with the reason', lt.static_call('EditSession', 'ModeOf', controls, try_evaluate(controls, 'points')[1], 'auto').startswith('readonly:'), True)

    # Values at the ends of the lines: the frame's variables, Python's text for its own values
    inline = lt.static_call('IronPythonBridge', 'InlineValues', frame)
    texts = dict((item.Item1, item.Item2) for item in inline.Item4)
    lt.check('inline values: the function\'s lines', (inline.Item2, inline.Item3), (line_of('def profile'), line_of('BREAKPOINT 1')))
    lt.check('inline values: n and points with Python\'s text, anchor as .NET', (texts.get('n'), texts.get('points'), texts.get('anchor')), ('3', '[[0, 6, 0], [5, 6, 50], [10, 6, 0]]', None))
    lt.check('inline values shown in the code view', any('values at the line ends shown' in l for l in log_lines('values at the line ends')), True)

    # Edit in Rhino: a global and a local, each moved in Rhino, written back through the bridge
    edits = lt.member(hook, 'Edits')
    for name in ('pt', 'anchor'):
        edits.Start(controls, name, 'auto')
    yield lt.wait_for(lambda: edits.Count >= 2, 15)
    lt.check('Edit in Rhino: pt and anchor are objects', edits.Count, 2)
    targets = lt.member(edits, '_targets')
    doc = Rhino.RhinoDoc.ActiveDoc
    for name, z in (('pt', 8.0), ('anchor', 7.0)):
        ids = [key for key in targets.Keys if lt.member(targets[key], 'Expression') == name]
        lt.check('%s has one object' % name, len(ids), 1)
        if ids:
            old = doc.Objects.FindId(ids[0]).Geometry.Location
            lt.check('%s moved in Rhino' % name, doc.Objects.Replace(ids[0], Rhino.Geometry.Point(Rhino.Geometry.Point3d(old.X, old.Y, z))), True)
    yield 2
    lt.check('written back: pt (a global) at z = 8', point_of(try_evaluate(controls, 'pt')[1]), (1.0, 2.0, 8.0))
    lt.check('written back: anchor (the function\'s local) at z = 7', point_of(try_evaluate(controls, 'anchor')[1]), (5.0, 6.0, 7.0))
    for name in ('pt', 'anchor'):
        edits.Stop(controls, name)

    # The Python tab's input, in the frame, as IronPython 2 code
    lt.check('exec: print (Python 2 statement)', bridge_exec(frame, 'print middle[1]'), '[5, 6, 50]\n')
    lt.check('exec: a cell with a final expression', bridge_exec(frame, 'k = n * 2\nk + 1'), '7\n')
    lt.check('exec: an error, reported', bridge_exec(frame, '1/0').strip().split('\n')[-1].startswith('ZeroDivisionError: '), True)
    lt.check('prompt: an expression is printed', bridge_run(frame, 'n * 2'), (False, '6\n'))
    lt.check('prompt: an unfinished block wants more', bridge_run(frame, 'for i in range(2):'), (True, ''))
    lt.check('prompt: the block, run', bridge_run(frame, 'for i in range(2):\n    print i\n'), (False, '0\n1\n'))
    lt.check('exec: changing a local and a global item', bridge_exec(frame, 'scale = 2.0\nmiddle[1][2] = 5'), '')
    lt.check('the local changed, as the frame reads it', try_evaluate(controls, 'scale')[1], 2.0)

    # Continue: the function returns with the changed local (scale 2.0 -> Z 5 * 2) and the moved anchor
    controls.Continue()
    yield lt.wait_for(lambda: len(paused_controls()) > 0 and paused_controls()[0].Results.CurrentThread.CurrentFrame.Reference.Position.LineNumber == line_of('BREAKPOINT 2'), 60)
    controls = paused_controls()[0]
    frame = controls.Results.CurrentThread.CurrentFrame
    lt.check('paused at breakpoint 2 (module level)', frame.Reference.Position.LineNumber, line_of('BREAKPOINT 2'))
    lt.check('still an IronPython pause', lt.static_call('ScriptEditorHook', 'IsIronPythonPause', controls), True)
    lt.check('the writes reached the running function: result = 5 * 2.0', try_evaluate(controls, 'result')[1], 10.0)
    lt.check('the moved anchor came back from the function', point_of(try_evaluate(controls, 'moved')[1]), (5.0, 6.0, 7.0))
    lt.check('a module-level expression', try_evaluate(controls, 'len(sections) + middle[1][2]')[1], 8)
    lt.check('exec at module level', bridge_exec(frame, 'result = 1\nresult'), '1\n')
    controls.Stop()
    yield 3
    active.DebugControls.Breakpoints.Clear()


lt.run('t_ironpython', test)
