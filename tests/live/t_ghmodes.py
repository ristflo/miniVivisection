#! python 3
# "Grasshopper script components": Normal, Stop and record (the default), Always debug.
# The toolbar button goes round the three; Normal leaves a running component to Ctrl+Alt+B
# alone; a "Normal" saved before there were three becomes Stop and record, once.
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
    mode = lambda: lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    user_mode = mode()
    store = lt.plugin_type('MiniVivisectionPlugin').GetProperty('Instance', lt.ANY).GetValue(None).Settings
    try:
        # The default.
        setting = lt.static_call('MiniVivisectionSettings', 'Find', 'GrasshopperScripts')
        lt.check('the default: Stop and record', lt.member(setting, 'Default'), 'Stop and record')

        # The migration: a "Normal" of before, once.
        version = store.GetInteger('SettingsVersion', 0)
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Normal')
        store.SetInteger('SettingsVersion', 2)
        lt.static_call('MiniVivisectionSettings', 'MigrateRenamed')
        lt.check('a saved Normal of before becomes Stop and record', (mode(), store.GetInteger('SettingsVersion', 0)), ('Stop and record', 3))
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Normal')
        lt.static_call('MiniVivisectionSettings', 'MigrateRenamed')
        lt.check('Normal chosen now stays Normal', mode(), 'Normal')
        store.SetInteger('SettingsVersion', max(version, 3))

        Rhino.RhinoApp.RunScript('_Grasshopper', False)
        yield lt.wait_for(grasshopper_ready, 120)
        yield 4  # the toolbar button comes within 3 s

        # The toolbar button: Stop and record -> Always debug -> Normal -> Stop and record.
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Stop and record')
        seen = []
        for _ in range(3):
            lt.static_call('GrasshopperToolbar', 'Toggle')
            seen.append(mode())
        lt.check('the button goes round the three', seen, ['Always debug', 'Normal', 'Stop and record'])
        button = lt.static_get('GrasshopperToolbar', 's_button')
        lt.check('its tooltip says the mode and the next', ('Stop and record' in button.ToolTipText, 'Click: Always debug' in button.ToolTipText), (True, True))
        lt.check('its icon: Run in color (Stop and record)', lt.static_get('GrasshopperToolbar', 's_shows').startswith('record'), True)
        icons = []
        for _ in range(3):
            lt.static_call('GrasshopperToolbar', 'Toggle')
            icons.append(lt.static_get('GrasshopperToolbar', 's_shows').split(' editor')[0].split(' drawn')[0])
        lt.check('icons: Debug in color, Run gray, Run in color', icons, ['debug', 'run gray', 'record'])
        # The three icons as the toolbar shows them, for a look: icon_<mode>.png
        import os
        for mode_name, file in (('Normal', 'normal'), ('Stop and record', 'record'), ('Always debug', 'debug')):
            lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', mode_name)
            lt.static_call('GrasshopperToolbar', 'Apply')
            lt.static_get('GrasshopperToolbar', 's_button').Image.Save(os.path.join(lt.RESULT_DIR, 'icon_%s.png' % file))
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Stop and record')

        # Normal: Break now with a component running (as the hotkey finds it) leaves it alone.
        import Grasshopper
        from Grasshopper.Kernel import GH_DocumentIO
        io = GH_DocumentIO()
        io.Open(GH3)
        doc = io.Document
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        halve = [o for o in doc.Objects if o.NickName == 'Halve'][0]
        running = lt.plugin_type('GrasshopperRuns').GetField('s_running', lt.ANY)
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Normal')
        running.SetValue(None, halve)
        try:
            lt.check('Normal: Ctrl+Alt+B leaves a running component alone', lt.static_call('GrasshopperRuns', 'Stop'), False)
        finally:
            running.SetValue(None, None)
        lt.check('nothing running: nothing to stop', lt.static_call('GrasshopperRuns', 'Stop'), None)
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', user_mode)


lt.run('t_ghmodes', test)
