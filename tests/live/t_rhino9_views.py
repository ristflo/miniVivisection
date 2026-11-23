#! python 3
# Rhino 9: what the Script Editor shows. The Run and Debug buttons and the V°AI / V°Debug
# switches laid out in the dashboard (Eto: Loaded), not only there -- the dashboard's trays
# are TableLayouts built once, and a cell inserted later isn't shown -- and a script opened
# into the open editor (_-ScriptEditor _Open, as a link in the overview does) with its code
# view loaded: a script the plug-in ran into a WebView2 still starting left it empty
# (HoverPreview.CodeViewLoaded). Pictures of the editor in the notes.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino
import System

HERE = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
LESSONS = os.path.join(HERE, 'examples')


def shot(name):
    try:
        import shots
        window = lt.editors()[0]
        lt.note('picture: ' + shots.save(shots.editor(window, lt.window_handle(window)), 'rhino9_views_' + name))
    except Exception as ex:
        lt.note('no picture: %s' % ex)


def field(obj, name):
    t = obj.GetType()
    while t is not None:
        f = t.GetField(name, lt.ANY)
        if f is not None:
            return f.GetValue(obj)
        t = t.BaseType
    return None


def items(tray):
    return list(tray.GetType().GetProperty('Items', lt.ANY).GetValue(tray))


def command_name(c):
    prop = c.GetType().GetProperty('Command', lt.ANY) if c is not None else None
    command = prop.GetValue(c) if prop else None
    return command.GetType().Name.split('`')[0] if command is not None else None


def inside(c):
    """A cell's control, and those of a group in it (StackLayout)."""
    found = [c]
    try:
        found += [i.Control for i in c.Items]
    except Exception:
        pass
    return [x for x in found if x is not None]


def test():
    yield 2
    first = os.path.join(LESSONS, '01_watch_geometry.py')
    second = os.path.join(LESSONS, '10_visualizers.py')
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Open "%s"' % first, False)
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 120)
    yield 8
    window = lt.editors()[0]
    shot('1_first')
    dashboard = field(window, 'm_dashboard')
    left = list(field(dashboard, 'm_traysL'))[0]
    controls = [x for c in items(left) for x in inside(c)]
    run = [c for c in controls if command_name(c) == 'CodeRunCommand']
    debug = [c for c in controls if command_name(c) == 'CodeDebugCommand']
    slot = [c for c in controls if command_name(c) == 'CodeRunDebugContinueCommand']
    lt.check('Run and Debug buttons in the dashboard', (len(run), len(debug)), (1, 1))
    lt.check('Run and Debug laid out (shown)', [bool(c.Loaded and c.Visible) for c in run + debug], [True, True])
    lt.check('the run slot there, hidden while idle', [bool(c.Visible) for c in slot], [False])
    right = list(field(dashboard, 'm_traysR'))[0]
    toggles = [x for c in items(right) for x in inside(c) if x.GetType().Name == 'StateToggle']
    lt.check('V°AI and V°Debug switches in the top bar', len(toggles), 2)
    lt.check('the switches laid out (shown)', [bool(t.Loaded and t.Visible) for t in toggles], [True, True])
    browser = [x for c in items(right) for x in inside(c) if command_name(x) == 'ToggleBrowserCommand']
    lt.check("the editor's own panel buttons still there", len(browser), 1)

    model = window.DataContext
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Open "%s"' % second, False)
    codes = lt.member(model, 'Codes')

    def current_view():
        current = lt.member(codes, 'Current')
        if current is None or not str(current.Code.Uri).endswith('10_visualizers.py'):
            return None
        return current.Editor.GetWidget[Eto.Forms.Control]()

    def loaded():
        view = current_view()
        return view is not None and lt.static_call('HoverPreview', 'CodeViewLoaded', view)

    yield lt.wait_for(loaded, 30)
    view = current_view()
    lt.check('a script opened into the open editor: its code view loaded', bool(view is not None and lt.static_call('HoverPreview', 'CodeViewLoaded', view)), True)
    lt.check('and shown', bool(view is not None and view.Loaded), True)
    yield 3
    shot('2_second')

    # The V tabs in the console tray: laid out with a width, and painted -- they had their
    # place but stayed blank until the window was resized (not resized here). Painted: dark
    # (text) pixels where they are in the editor's picture.
    tabs = console_tabs(window)
    lt.note('console tabs: ' + ', '.join('%s %.0f' % (t, w) for t, w in tabs))
    ours = [w for t, w in tabs if t.startswith('V')]
    lt.check('the V tabs in the console tray have a width', bool(ours) and all(w > 10 for w in ours), True)
    dark = painted_pixels(window)
    lt.note('dark pixels where the V tabs are: %d (where the editor\'s own tabs are: %d)' % (dark, painted_pixels(window, rhinos=True)))
    lt.check('the V tabs painted (their titles in the picture)', dark > 100, True)

    # Stop while paused: the dashboard's Stop button, pressed as a click does (its command
    # through PushButton.ExecuteCommandAsync). In 10_visualizers.py (shown now), paused on its
    # first import (line 25); that breakpoint is taken away again at the end.
    text_editor = lt.member(codes, 'Current').Editor
    def on_25():
        return [bp for bp in text_editor.Code.DebugControls.Breakpoints if ':[25:' in str(bp.Reference)]
    for _ in range(5):
        if on_25():
            break
        text_editor.Goto(Rhino.Runtime.Code.Text.TextPosition(25, 1))
        yield 2
        text_editor.ToggleBreakpoint()
        yield 2
    lt.check('a breakpoint on line 25', len(on_25()), 1)
    debug = lt.member(lt.member(model, 'Commands'), 'CodeDebugCommand')
    m = sorted([m for m in debug.GetType().GetMethods(lt.ANY) if m.Name == 'Execute'], key=lambda m: len(m.GetParameters()))[0]
    Eto.Forms.Application.Instance.AsyncInvoke(lambda: m.Invoke(debug, [None] * len(m.GetParameters())))
    yield lt.wait_for(lambda: str(lt.member(codes, 'DebugControlsState')) == 'Paused', 30)
    lt.check('paused at the breakpoint', str(lt.member(codes, 'DebugControlsState')), 'Paused')
    stop = [x for c in items(left) for x in inside(c) if command_name(x) == 'CodeDebugStopCommand']
    lt.check('a Stop button', len(stop), 1)
    if stop:
        button = stop[0]
        lt.note('Stop: enabled %s, its command can run %s' % (button.Enabled, lt.member(button, 'Command').CanExecute))
        lt.check('Stop enabled while paused', bool(button.Enabled), True)
        shot('3_paused')
        lt.note('dashboard while paused: ' + ', '.join('%s x=%.0f w=%.0f' % (command_name(x) or x.GetType().Name, at_x(x, window), x.ControlObject.ActualWidth) for c in items(left) for x in inside(c) if x.Visible))
        # Pressed as a click does (PushButton.ExecuteCommandAsync); a click posted to the
        # window doesn't reach WPF's buttons on the hidden desktop.
        press = button.GetType().GetMethod('ExecuteCommandAsync', lt.ANY)
        Eto.Forms.Application.Instance.AsyncInvoke(lambda: press.Invoke(button, None))
        yield lt.wait_for(lambda: not bool(lt.member(codes, 'IsRunning')), 15)
        lt.check('Stop ended the run', bool(lt.member(codes, 'IsRunning')), False)
    if bool(lt.member(codes, 'IsRunning')):
        controls = lt.member(lt.member(codes, 'Current').Code, 'DebugControls')
        controls.Stop()
        yield 2
    for bp in on_25():
        text_editor.Code.DebugControls.Breakpoints.Remove(bp)



def at_x(control, window):
    import clr
    clr.AddReference('WindowsBase')
    clr.AddReference('PresentationCore')
    from System.Windows import Point
    return control.ControlObject.TranslatePoint(Point(0, 0), window.ControlObject).X


def painted_pixels(window, rhinos=False):
    """Dark pixels (text) in the editor's picture where the plug-in's console tabs are."""
    sys.path.insert(0, os.path.join(HERE, 'tools'))
    import shots
    import clr
    clr.AddReference('WindowsBase')
    clr.AddReference('PresentationCore')
    from System.Windows import Point
    hwnd = lt.window_handle(window)
    picture = shots.editor(window, hwnd)
    wpf = window.ControlObject
    tray = lt.static_call('Rhino9Trays', 'Find', window, 'EditorConsoles')
    stack = lt.static_call('Rhino9Trays', 'StackOf', lt.static_call('Rhino9Trays', 'TabsOf', tray))
    ours = [i.Control for i in stack.Items if i.Control is not None and i.Control.Visible and str(lt.member(i.Control, 'Title')).startswith('V') != rhinos]
    if not ours:
        return 0
    first, last = ours[0].ControlObject, ours[-1].ControlObject
    a = first.TranslatePoint(Point(0, 0), wpf)
    b = last.TranslatePoint(Point(last.ActualWidth, last.ActualHeight), wpf)
    wx, wy, ww, wh = shots.rect_of(hwnd)
    cx, cy = shots.client_origin(hwnd)
    client = ctypes_client_width(hwnd)
    scale = client / max(1.0, float(wpf.ActualWidth))
    x0, y0 = int(a.X * scale) + cx - wx, int(a.Y * scale) + cy - wy
    x1, y1 = int(b.X * scale) + cx - wx, int(b.Y * scale) + cy - wy
    dark = 0
    for x in range(max(0, x0), min(picture.Width, x1)):
        for y in range(max(0, y0), min(picture.Height, y1)):
            c = picture.GetPixel(x, y)
            if c.R + c.G + c.B < 300:
                dark += 1
    return dark


def ctypes_client_width(hwnd):
    import ctypes
    import ctypes.wintypes as wt
    r = wt.RECT()
    ctypes.windll.user32.GetClientRect(ctypes.c_void_p(hwnd), ctypes.byref(r))
    return r.right - r.left


def console_tabs(window):
    """(title, drawn width) of each tab in the console tray's strip."""
    found = []
    tray = lt.static_call('Rhino9Trays', 'Find', window, 'EditorConsoles')
    tabs = lt.static_call('Rhino9Trays', 'TabsOf', tray) if tray is not None else None
    stack = lt.static_call('Rhino9Trays', 'StackOf', tabs) if tabs is not None else None
    for item in (stack.Items if stack is not None else []):
        c = item.Control
        if c is None or not c.Visible:
            continue
        try:
            title = str(lt.member(c, 'Title') or '')
        except Exception:
            title = c.GetType().Name
        try:
            width = float(c.ControlObject.ActualWidth)
        except Exception:
            width = -1.0
        found.append((title, width))
    return found


import Eto.Forms
lt.run('t_rhino9_views', test)
