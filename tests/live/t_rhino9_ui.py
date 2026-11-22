#! python 3
# Rhino 9: what the plug-in adds to Rhino 9's Script Editor -- the V tabs in the console tray,
# the pages in the left panel, folder drop's Explorer, the lessons in the Examples tab, and,
# while paused at a breakpoint: values in the code's tooltips (the wrapped hover provider),
# the breakpoint condition marker, the plug-in's entries in the code view's menu (Watch in
# Rhino run from there) and the health line. Run: python tests/live/run_live.py --rhino 9 t_rhino9_ui
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino
import System
import Eto.Forms

SCRIPT = '''#! python 3
import Rhino.Geometry as rg
pts = []
for k in range(3):
    pts.append(rg.Point3d(k, 2 * k, 0))
crv = rg.PolylineCurve(pts)
print('breakpoint here')
'''
DEG = chr(0xB0)


def paused_controls():
    hook = lt.hook()
    return list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))


def call(obj, name, *args):
    methods = [m for m in obj.GetType().GetMethods(lt.ANY) if m.Name == name and len(m.GetParameters()) == len(args)]
    return methods[0].Invoke(obj, list(args))


def as_int(value):
    return int(System.Convert.ToInt32(value))


def local_path(uri):
    return os.path.normcase(os.path.normpath(str(uri.LocalPath))) if uri is not None else ''


def script(ed, body):
    """HoverPreview.Script: JS in the code view (a function body), its answer as text."""
    return lt.static_call('HoverPreview', 'Script', ed, body)


def test():
    yield 2
    lt.check('Rhino 9', Rhino.RhinoApp.Version.Major, 9)
    if lt.assembly('miniVivisection') is None:
        lt.check('plug-in loaded', False, True)
        return
    path = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'rhino9_ui.py')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(SCRIPT)
    lt.progress('opening the script')
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Open "%s"' % path, False)
    lt.progress('opened')
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 120)
    yield 6  # the scans: tabs, pages, lessons
    editor = lt.editors()[0]
    model = editor.DataContext

    lt.progress('the V tabs in the console tray')
    # --- the V tabs in the console tray (Rhino9Ui, Rhino9Trays) ---
    lt.check('the V tabs are in the console tray', lt.static_call('Rhino9Ui', 'InConsole', editor), True)
    tray = lt.static_call('Rhino9Trays', 'Find', editor, 'EditorConsoles')
    if tray is not None:
        titles = [str(lt.member(t, 'Title')) for t in lt.member(tray, 'Tabs')]
        lt.note('console tabs: ' + ', '.join(titles))
        lt.check('  V' + DEG + 'Watch and V' + DEG + 'Log among them', 'V' + DEG + 'Watch' in titles and 'V' + DEG + 'Log' in titles, True)
        lt.static_call('ConsoleTabs', 'Show', editor, 'Python')
        yield 1
        lt.check('  V' + DEG + 'Python selected (its ConsoleTray value)', as_int(lt.member(model, 'CurrentConsole')), -0x5600 - 2)
        container = lt.static_call('Rhino9Trays', 'ContainerOf', tray)
        content = container.Content
        inner = getattr(content, 'Content', None)
        lt.check('  the console shows V' + DEG + 'Python', type(inner).__name__ if inner is not None else str(content.GetType().Name), 'PythonTerminal')
        lt.static_call('Rhino9Trays', 'SetTray', model, 'CurrentConsole', 0)  # the editor's Terminal
        yield 1
        lt.check('  the editor\'s Terminal back', str(container.Content.GetType().Name) != 'Panel' or container.Content is not content, True)
    titles = [str(w.Title) for w in Eto.Forms.Application.Instance.Windows]
    lt.check('no companion window needed', any(t.startswith('miniVivisection --') for t in titles), False)

    lt.progress('the left panel pages')
    # --- the left panel pages (BrowserPages, Rhino 9) ---
    shown = lt.static_call('BrowserPages', 'Show9', editor, 'V Watch')
    lt.check('left panel: page V' + DEG + 'Watch shown', shown, True)
    if shown:
        yield 1
        lt.check('  its BrowserTray value', as_int(lt.member(model, 'CurrentBrowser')), -(0x5700 + 1))
        lt.check('  the left panel is open', bool(lt.member(model, 'ShowBrowser')), True)
    lt.static_call('Rhino9Trays', 'SetTray', model, 'CurrentBrowser', 1)  # Explorer

    lt.progress('folder drop')
    # --- folder drop: the editor's Explorer (FolderDrop.Explore, Rhino 9) ---
    folder = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'dropped')
    os.makedirs(folder, exist_ok=True)
    lt.static_call('FolderDrop', 'Explore', editor, folder)
    explorer = lt.member(model, 'Explorer')
    yield lt.wait_for(lambda: lt.member(explorer, 'HasLibrary') and local_path(lt.member(lt.member(explorer, 'Library'), 'Base')).rstrip('\\') == os.path.normcase(folder), 10)
    base = lt.member(lt.member(explorer, 'Library'), 'Base') if lt.member(explorer, 'HasLibrary') else None
    lt.check('folder drop: Explorer shows the folder', local_path(base).rstrip('\\'), os.path.normcase(folder))
    lt.check('  the Explorer page in front', as_int(lt.member(model, 'CurrentBrowser')), 1)

    lt.progress('the lessons')
    # --- the lessons in the Examples tab (EditorExamples, Rhino 9) ---
    task = lt.static_call('EditorExamples', 'ShowLessons', editor)
    yield lt.wait_for(lambda: task.IsCompleted, 20)
    lt.check('lessons: shown in the Examples tab', task.IsCompleted and str(task.Status) == 'RanToCompletion' and bool(task.Result), True)
    examples = lt.member(model, 'Examples')
    base = lt.member(lt.member(examples, 'Library'), 'Base') if lt.member(examples, 'HasLibrary') else None
    lt.note('Examples tab: ' + local_path(base))
    lt.check('  the lessons folder', 'lessons' in local_path(base), True)
    lt.check('  the Examples page in front', as_int(lt.member(model, 'CurrentBrowser')), 4)
    lt.check('  a lesson opens as its file', str(lt.member(examples, 'OpenSelectedCommand').GetType().Name), 'OpenLessonCommand')
    lt.static_call('Rhino9Trays', 'SetTray', model, 'CurrentBrowser', 1)

    lt.progress('a pause at a breakpoint')
    # --- a pause at a breakpoint on line 7 (as t_rhino9) ---
    codes = lt.member(model, 'Codes')
    text_editor = lt.member(codes, 'Current').Editor
    text_editor.Goto(Rhino.Runtime.Code.Text.TextPosition(7, 1))
    yield 1
    text_editor.ToggleBreakpoint()
    yield 2
    debug = lt.member(lt.member(model, 'Commands'), 'CodeDebugCommand')
    m = sorted([m for m in debug.GetType().GetMethods(lt.ANY) if m.Name == 'Execute'], key=lambda m: len(m.GetParameters()))[0]
    args = [None] * len(m.GetParameters())
    Eto.Forms.Application.Instance.AsyncInvoke(lambda: m.Invoke(debug, args))
    yield lt.wait_for(lambda: len(paused_controls()) > 0, 60)
    controls = paused_controls()
    lt.check('paused at the breakpoint', len(controls), 1)
    if not controls:
        return
    c = controls[0]
    hook = lt.hook()
    yield 2

    lt.progress('tooltip values')
    # --- tooltip values (HoverValues, Rhino 9) ---
    access = lt.plugin_type('ScriptEditorAccess').GetConstructors(lt.ANY)[0].Invoke([editor])
    ed = lt.member(access, 'Monaco')
    provider = lt.static_call('HoverValues', 'CurrentHoverProvider', ed.Handler) if ed is not None else None
    lt.check('the code view has a hover provider', provider is not None, True)
    if provider is not None:
        lt.check('  it is the plug-in\'s', lt.static_call('HoverValues', 'IsWrapped', provider), True)
        text_task = ed.GetTextAsync()
        yield lt.wait_for(lambda: text_task.IsCompleted, 10)
        text = str(text_task.Result)
        tip = provider.Invoke(text.index('crv = ') + 1)  # inside "crv", line 6
        yield lt.wait_for(lambda: tip.IsCompleted, 10)
        parts = [str(p) for p in tip.Result] if str(tip.Status) == 'RanToCompletion' and tip.Result is not None else []
        lt.note('tooltip: ' + repr(parts[:2])[:300])
        lt.check('  the tooltip shows crv\'s value first', len(parts) > 0 and 'crv' in parts[0], True)

    lt.progress('the breakpoint condition marker')
    # --- the breakpoint condition marker (BreakpointMarkers) ---
    uri = text_editor.Code.Uri
    lt.static_call('BreakpointSpecs', 'SetOneShot', uri, 7, 'k == 2')
    yield 2  # the markers' timer
    if ed is not None:
        lines = script(ed, "return window.__swvBp ? JSON.stringify(window.__swvBp.lines()) : 'none';")
        after = script(ed, "var a = document.querySelector('.swv-bp-after'); return a ? a.innerText : '';")
        yield lt.wait_for(lambda: lines.IsCompleted and after.IsCompleted, 5)
        lt.note('marker lines %s, text %r' % (lines.Result if lines.IsCompleted else '?', after.Result if after.IsCompleted else '?'))
        lt.check('the marker is on line 7', '7' in str(lines.Result), True)
        lt.check('  it shows the condition', 'k == 2' in str(after.Result).replace(chr(0xA0), ' '), True)

    lt.progress("the code view's menu")
    # --- the code view's menu (Rhino9CodeMenu) ---
    bar = lt.member(model, 'MenuBar')
    text_editor.Goto(Rhino.Runtime.Code.Text.TextPosition(6, 2))  # the cursor on "crv"
    yield 1
    group = call(bar, 'GetTextContextMenu')
    items = {str(lt.member(i, 'Id')): i for i in lt.member(group, 'Menus')}
    lt.note('text menu: ' + ', '.join(items.keys()))
    for id in ('minivivisection.breakpoint.condition', 'minivivisection.breakpoint.hitcount', 'minivivisection.breakpoint.logmessage',
               'minivivisection.watch', 'minivivisection.bake', 'minivivisection.edit'):
        lt.check('menu: ' + id, id in items, True)
    watch = items.get('minivivisection.watch')
    if watch is not None:
        done = lt.member(watch, 'Command').Execute(System.Threading.CancellationToken())
        yield lt.wait_for(lambda: done.IsCompleted, 5)
        yield 2
        expressions = [str(w.Expression) for w in lt.member(call(hook, 'WatchListFor', editor), 'Items')]
        lt.check('menu: Watch in Rhino added crv', 'crv' in expressions, True)
    lt.static_call('BreakpointSpecs', 'EndOneShot', uri, 7)

    health = [str(e.Text) for e in lt.static_get('UserLog', 'Entries') if 'Editor hooks' in str(e.Text)]
    lt.note('health: ' + (health[-1] if health else '(none)'))
    lt.check('health line: tooltips, menus, tabs, pages, drop, lessons in place', bool(health) and not any(
        word in health[-1] for word in ('tooltip values (', "code view's menu (", 'console tray (', 'left panel pages (', 'folder drop (', 'Examples tab (')), True)
    c.Stop()
    yield 3
    lt.check('stopped', len(paused_controls()), 0)


lt.run('t_rhino9_ui', test)
