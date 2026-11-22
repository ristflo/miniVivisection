#! python 3
# Rhino 9: the Run and Debug buttons next to the editor's run slot (DebugRunButton), Debug
# without a breakpoint, the editor kept open while debugging (KeepEditorOpen), and
# "-ScriptEditor _Run _Debug=_On" running the named script -- not the active tab -- in the
# debugger (ScriptRunCommand, Rhino 9's Rhino3DScriptEditor.OpenScriptAsync + Codes.RestoreAndDebug).
# Run: python tests/live/run_live.py --rhino 9 t_rhino9_rundebug
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino
import Eto.Forms


def marker(name):
    return os.path.join(lt.RESULT_DIR, name + '.ran')


def traced_script(name):
    # Writes whether Python's tracer was on (a debug run) into its marker.
    return lt.write_file(name + '.py', '#! python 3\nimport sys, time\nwith open(%r, "w") as f:\n    f.write(str(sys.gettrace() is not None))\ntime.sleep(1)\n' % marker(name))


def state_script(name):
    # A run that lets Rhino's message loop go (so the editor can minimize), then writes the
    # editor windows' states into its marker.
    return lt.write_file(name + '.py', '''#! python 3
import time
import Rhino
import Eto.Forms
for _ in range(25):
    time.sleep(0.1)
    Rhino.RhinoApp.Wait()
states = []
for w in Eto.Forms.Application.Instance.Windows:
    try:
        if w.DataContext is not None and w.GetType().FullName == 'RhinoCodeEditor.MainEditor':
            states.append(str(w.WindowState))
    except Exception:
        pass
with open(%r, "w") as f:
    f.write(",".join(states))
''' % marker(name))


def read(name):
    with open(marker(name), encoding='utf-8') as f:
        return f.read()


def command(model, name):
    return lt.member(lt.member(model, 'Commands'), name)


def execute(cmd):
    m = sorted([m for m in cmd.GetType().GetMethods(lt.ANY) if m.Name == 'Execute'], key=lambda m: len(m.GetParameters()))[0]
    args = [None] * len(m.GetParameters())
    Eto.Forms.Application.Instance.AsyncInvoke(lambda: m.Invoke(cmd, args))


def dashboard_commands(window):
    """Per left tray of the dashboard: (command type name, visible) of each item."""
    dashboard = lt.member(window, 'm_dashboard')
    trays = []
    for tray in lt.member(dashboard, 'm_traysL'):
        row = []
        for item in lt.member(tray, 'Items'):
            try:
                cmd = lt.member(item, 'Command')
            except Exception:
                cmd = None
            row.append((cmd.GetType().Name.split('`')[0] if cmd is not None else item.GetType().Name, bool(item.Visible)))
        trays.append(row)
    return trays


def test():
    yield 2
    lt.check('Rhino 9', Rhino.RhinoApp.Version.Major, 9)
    if lt.assembly('miniVivisection') is None:
        lt.check('plug-in loaded', False, True)
        return
    names = ('x_script', 'y_script', 'debug_state', 'run_state')
    for name in names:
        if os.path.exists(marker(name)):
            os.remove(marker(name))
    x, y = traced_script('x_script'), traced_script('y_script')
    debug_state, run_state = state_script('debug_state'), state_script('run_state')
    old_buttons = lt.static_call('MiniVivisectionSettings', 'GetBool', 'DebugRunButton')
    old_keep = lt.static_call('MiniVivisectionSettings', 'GetBool', 'KeepEditorWhileDebugging')
    lt.static_call('MiniVivisectionSettings', 'Set', 'DebugRunButton', True)
    lt.static_call('MiniVivisectionSettings', 'Set', 'KeepEditorWhileDebugging', True)
    try:
        Rhino.RhinoApp.RunScript('_-ScriptEditor _Open "%s"' % x, False)
        yield lt.wait_for(lambda: len(lt.editors()) > 0, 120)
        yield 5
        window = lt.editors()[0]
        model = window.DataContext
        codes = lt.member(model, 'Codes')
        for name in ('DebugRunButton', 'KeepEditorOpen', 'ScriptRunCommand'):
            lt.check(name + ' in place', lt.static_get(name, 'HookProblem'), None)

        # --- the buttons ---------------------------------------------------------------------
        trays = dashboard_commands(window)
        lt.note('dashboard: ' + repr(trays))
        # Only a shown tray is bound to the editor's commands (the project tray isn't): the
        # checks on the bound ones; the other gets its buttons at the same place.
        bound = [row for row in trays if any(n == 'CodeRunDebugContinueCommand' for n, _ in row)]
        lt.check('a tray with the run slot', len(bound) > 0, True)
        for row in bound:
            names_only = [n for n, _ in row]
            i = names_only.index('CodeRunDebugContinueCommand')
            lt.check('Run and Debug right after the run slot', names_only[i + 1:i + 3], ['CodeRunCommand', 'CodeDebugCommand'])
            lt.check('the run slot hidden while idle', row[i][1], False)
        for row in trays:
            if row not in bound:
                lt.note('an unbound tray: %d items, the bound one %d' % (len(row), len(bound[0]) if bound else -1))
        debug = command(model, 'CodeDebugCommand')
        lt.check('Debug enabled without a breakpoint', bool(debug.CanExecute), True)
        lt.static_call('MiniVivisectionSettings', 'Set', 'DebugRunButton', False)
        lt.static_call('DebugRunButton', 'ApplyAll')
        yield 1
        row = dashboard_commands(window)[0]
        lt.check('setting off: the slot shows, the buttons don\'t', [v for n, v in row if n in ('CodeRunDebugContinueCommand', 'CodeRunCommand', 'CodeDebugCommand')], [True, False, False])
        lt.check('setting off: Debug needs a breakpoint again', bool(debug.CanExecute), False)
        lt.static_call('MiniVivisectionSettings', 'Set', 'DebugRunButton', True)
        lt.static_call('DebugRunButton', 'ApplyAll')
        yield 1
        lt.check('setting on again: Debug enabled', bool(debug.CanExecute), True)

        # --- the editor open while debugging -------------------------------------------------
        options = lt.member(lt.member(model, 'Options'), 'GeneralOptionsAspect')
        minimize = bool(lt.member(options, 'MinimizeOnExecute'))
        lt.note('the editor\'s "Minimize on execute": %s' % minimize)
        Rhino.RhinoApp.RunScript('_-ScriptEditor _Open "%s"' % debug_state, False)
        yield 3
        execute(debug)
        yield lt.wait_for(lambda: os.path.exists(marker('debug_state')), 30)
        lt.check('debugging: the editor stays open', read('debug_state') if os.path.exists(marker('debug_state')) else None, 'Normal')
        yield lt.wait_for(lambda: not codes.IsRunning, 20)
        Rhino.RhinoApp.RunScript('_-ScriptEditor _Open "%s"' % run_state, False)
        yield 3
        execute(command(model, 'CodeRunCommand'))
        yield lt.wait_for(lambda: os.path.exists(marker('run_state')), 30)
        state = read('run_state') if os.path.exists(marker('run_state')) else None
        lt.check('a plain Run minimizes as the editor\'s option says', state, 'Minimized' if minimize else 'Normal')
        yield lt.wait_for(lambda: not codes.IsRunning, 20)
        window.WindowState = Eto.Forms.WindowState.Normal
        yield 2

        # --- ScriptEditor Run, Debug ---------------------------------------------------------
        Rhino.RhinoApp.RunScript('_-ScriptEditor _Open "%s"' % x, False)
        yield 3
        if os.path.exists(marker('x_script')):
            os.remove(marker('x_script'))
        lt.note('X is the active tab; now ScriptEditor Run Debug Y')
        Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_On "%s"' % y, False)
        yield lt.wait_for(lambda: os.path.exists(marker('y_script')), 60)
        lt.check('Y ran in the debugger (no breakpoint)', read('y_script') if os.path.exists(marker('y_script')) else None, 'True')
        lt.check('X didn\'t run', os.path.exists(marker('x_script')), False)
        yield lt.wait_for(lambda: not codes.IsRunning, 20)
        os.remove(marker('y_script')) if os.path.exists(marker('y_script')) else None
        Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_Off "%s"' % y, False)
        yield lt.wait_for(lambda: os.path.exists(marker('y_script')), 30)
        lt.check('Debug=Off: Y ran plainly', read('y_script') if os.path.exists(marker('y_script')) else None, 'False')
        lt.note('UserLog: ' + ' | '.join(str(e.Text) for e in list(lt.static_get('UserLog', 'Entries'))[-8:]))
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'DebugRunButton', old_buttons)
        lt.static_call('MiniVivisectionSettings', 'Set', 'KeepEditorWhileDebugging', old_keep)


lt.run('t_rhino9_rundebug', test)
