#! python 3
# The help and examples: the miniVivisection toolbar (RhinoToolbar) opened from its copy, shown and
# hidden by its setting; miniVivisectionExamples opening your copy of the overview; the lessons
# folder made; V°Examples, V°Manual, V°Settings on Grasshopper's tab (hidden by one setting), V°Examples
# opening gh0_overview.gh; miniVivisectionSettings opening Rhino's Options at the miniVivisection page.
# (The manual and the lessons folder open other programs -- not here: their files are looked at.)
import os
import threading
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino


def floating():
    """How many floating containers (Rhino 8's toolbar windows) this Rhino shows."""
    import ctypes
    import ctypes.wintypes as wt
    user32 = ctypes.windll.user32
    pid = os.getpid()
    found = []
    proc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

    def each(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            title = ctypes.create_unicode_buffer(256)
            user32.GetWindowTextW(hwnd, title, 256)
            if title.value.startswith('TabPanelDockBarFloatingForm'):
                found.append(hwnd)
        return True
    user32.EnumWindows(proc(each), 0)
    return len(found)


CLOSED = []


def close_options():
    """Rhino's Options dialog closed (WM_CLOSE: as Cancel) -- from a timer thread, while it is modal."""
    import ctypes
    import ctypes.wintypes as wt
    user32 = ctypes.windll.user32
    pid = os.getpid()
    proc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

    def each(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            title = ctypes.create_unicode_buffer(256)
            user32.GetWindowTextW(hwnd, title, 256)
            if 'Options' in title.value:
                user32.PostMessageW(hwnd, 0x0010, 0, 0)
                CLOSED.append(True)
        return True
    user32.EnumWindows(proc(each), 0)


def gh_ready():
    asm = lt.assembly('Grasshopper')
    return asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None and lt.assembly('miniVivisection.GH') is not None


def test():
    yield 3
    flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    toolbar = lt.plugin_type('RhinoToolbar')
    file = toolbar.GetProperty('File', flags).GetValue(None)
    here = os.path.dirname(lt.assembly('miniVivisection').Location)
    lt.check('the toolbar file open: the plug-in one', (file is not None, file is not None and os.path.normcase(file.Path) == os.path.normcase(os.path.join(here, 'toolbar', 'miniVivisection.rui'))), (True, True))
    lt.check('one toolbar in it', file.ToolbarCount if file else 0, 1)
    # Rhino may hold it in two containers (one restored from its window layout, one made by
    # -ShowToolbar): shown in one, hidden in all
    apply = lambda: toolbar.GetMethod('Apply', flags).Invoke(None, None)
    apply()
    yield 1
    lt.check('shown (the default): one floating toolbar', floating(), 1)
    lt.static_call('MiniVivisectionSettings', 'Set', 'HelpToolbar', False)
    yield 1
    lt.check('the setting off: hidden', floating(), 0)
    lt.static_call('MiniVivisectionSettings', 'Set', 'HelpToolbar', True)
    yield 1
    lt.check('the setting on: shown again, in the same container', floating(), 1)

    # The lessons folder (your copies) and what the buttons open
    folder = lt.static_get('MiniVivisectionSettings', 'LessonsFolder')
    lt.check('the lessons folder: the copies', (os.path.exists(os.path.join(folder, '00_overview.3dm')), os.path.exists(os.path.join(folder, 'grasshopper', 'gh0_overview.gh'))), (True, True))
    lt.check('the manual next to the plug-in: PDF and Markdown', (os.path.exists(os.path.join(here, 'miniVivisection_Manual.pdf')), os.path.exists(os.path.join(here, 'miniVivisection_Manual.md'))), (True, True))
    lt.check('the commands', all(Rhino.Commands.Command.IsCommand(n) for n in ('miniVivisectionManual', 'miniVivisectionExamples', 'miniVivisectionGrasshopperExamples', 'miniVivisectionLessons', 'miniVivisectionSettings')), True)

    # The manual as Markdown: in a Script Editor (started for it), as V Help opens it
    Rhino.RhinoApp.RunScript('_-miniVivisectionManual _Markdown', False)
    access = lt.plugin_type('ScriptEditorAccess')
    manual = os.path.normcase(os.path.join(here, 'miniVivisection_Manual.md'))

    def manual_open():
        import Eto.Forms
        return any(window.GetType().FullName == 'RhinoCodeEditor.Editor.RCE' and
                   os.path.normcase(System.Activator.CreateInstance(access, [window]).ScriptPath or '') == manual
                   for window in Eto.Forms.Application.Instance.Windows)
    yield lt.wait_for(manual_open, 40)
    lt.check('-miniVivisectionManual Markdown: the manual in the Script Editor', manual_open(), True)

    # Examples: the overview opened (after the command)
    Rhino.RhinoDoc.ActiveDoc.Modified = False
    Rhino.RhinoApp.RunScript('_miniVivisectionExamples', False)
    yield 3
    path = Rhino.RhinoDoc.ActiveDoc.Path or ''
    lt.check('miniVivisectionExamples: your copy of the overview open', (os.path.basename(path), os.path.normcase(os.path.dirname(path)) == os.path.normcase(folder)), ('00_overview.3dm', True))

    # Grasshopper's examples from Rhino: Grasshopper started, its overview opened
    Rhino.RhinoApp.RunScript('_miniVivisectionGrasshopperExamples', False)
    yield lt.wait_for(gh_ready, 120)
    import Grasshopper

    def gh_overview_open():
        canvas = Grasshopper.Instances.ActiveCanvas
        return canvas is not None and canvas.Document is not None and os.path.basename(canvas.Document.FilePath or '') == 'gh0_overview.gh'
    yield lt.wait_for(gh_overview_open, 60)
    lt.check('miniVivisectionGrasshopperExamples: Grasshopper started, its overview open', gh_overview_open(), True)

    # Grasshopper: V Examples, V Manual and V Settings on the tab (Help), one setting for them
    help_names = sorted(p.Desc.Name[-len(n):] for p in Grasshopper.Instances.ComponentServer.ObjectProxies if p.Desc.Category == 'miniVivisection' and p.Desc.SubCategory == 'Help'
                        for n in ('Examples', 'Manual', 'Settings') if p.Desc.Name.endswith(n))
    lt.check('V Examples, V Manual, V Settings on the miniVivisection tab', help_names, ['Examples', 'Manual', 'Settings'])
    gha = lt.assembly('miniVivisection.GH')
    examples = gha.GetType('MiniVivisection.GH.ExamplesComponent')
    components = [System.Activator.CreateInstance(gha.GetType('MiniVivisection.GH.' + n)) for n in ('ExamplesComponent', 'ManualComponent', 'SettingsComponent')]
    lt.check('shown (the default)', [str(c.Exposure) for c in components], ['primary'] * 3)
    lt.check('their icons', [c.Icon_24x24 is not None for c in components], [True] * 3)
    lt.static_call('MiniVivisectionSettings', 'Set', 'GhHelpComponents', False)
    lt.check('the setting off: all hidden', [str(c.Exposure) for c in components], ['hidden'] * 3)
    lt.static_call('MiniVivisectionSettings', 'Set', 'GhHelpComponents', True)
    lt.check('the overview opened', examples.GetMethod('OpenOverview', flags).Invoke(None, None), None)
    yield 2
    canvas = Grasshopper.Instances.ActiveCanvas
    lt.check('on the canvas: gh0_overview.gh', os.path.basename(canvas.Document.FilePath or ''), 'gh0_overview.gh')
    lt.check('opened again: shown, not opened twice', examples.GetMethod('OpenOverview', flags).Invoke(None, None), None)
    lt.check('one overview open', len([d for d in Grasshopper.Instances.DocumentServer if (d.FilePath or '').endswith('gh0_overview.gh')]), 1)

    # Settings (the toolbar's gear, V Settings): Rhino's Options, at the miniVivisection page --
    # the dialog is modal: closed from another thread after a few seconds
    page = lt.plugin_type('MiniVivisectionOptionsPage')
    page.GetProperty('WasActive', flags).SetValue(None, False)
    closer = threading.Timer(6, close_options)
    closer.start()
    Rhino.RhinoApp.RunScript('_miniVivisectionSettings', False)
    closer.cancel()
    yield 1
    lt.check('miniVivisectionSettings: Options opened at the miniVivisection page', (page.GetProperty('WasActive', flags).GetValue(None), CLOSED), (True, [True]))
    Rhino.RhinoDoc.ActiveDoc.Modified = False


lt.run('t_helptoolbar', test)
