#! python 3
# The V names: V°Watch, in names and in V°Log; the V°Edit component is V°Edit when it's made --
# also with a mark an earlier version saved in the settings (the setting is gone).
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

DEG = chr(0x00B0)
PLUGIN = 'c198ec17-3078-46a0-8f9b-94a40e9ba298'


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('miniVivisection.GH') is not None)


def last_log():
    entries = list(lt.static_get('UserLog', 'Entries'))
    return entries[-1].Text if entries else ''


def test():
    yield 2
    import System
    settings = Rhino.PlugIns.PlugIn.GetPluginSettings(System.Guid(PLUGIN), False)
    earlier = settings.GetString('NameMark', '')
    try:
        settings.SetString('NameMark', '|')  # what an earlier version could have saved
        lt.check('no setting for the mark', any(s.Key == 'NameMark' for s in lt.static_get('MiniVivisectionSettings', 'All')), False)
        lt.check('V' + DEG + 'Watch', lt.static_call('VNames', 'Of', 'Watch'), 'V' + DEG + 'Watch')
        lt.static_call('UserLog', 'Info', 'see V Log and V Settings', None)
        lt.check('V' + DEG + 'Log in V' + DEG + 'Log', last_log(), 'see V' + DEG + 'Log and V' + DEG + 'Settings')
        lt.check('a setting\'s label', lt.static_call('MiniVivisectionSettings', 'Find', 'TabPython').Label, 'Tab (bottom): V' + DEG + 'Python')

        # The V°Edit component: its name has the ° when it's made
        Rhino.RhinoApp.RunScript('_Grasshopper', False)
        yield lt.wait_for(grasshopper_ready, 120)
        import Grasshopper
        vedit_id = System.Guid('b7d2e8a4-3c1f-4e6b-a9d0-5f2c7e1b8d63')
        yield lt.wait_for(lambda: Grasshopper.Instances.ComponentServer.EmitObjectProxy(vedit_id) is not None, 60)
        made = Grasshopper.Instances.ComponentServer.EmitObjectProxy(vedit_id).CreateInstance()
        lt.check('V' + DEG + 'Edit: name and nickname', (made.Name, made.NickName), ('V' + DEG + 'Edit', 'V' + DEG + 'Edit'))
    finally:
        if earlier:
            settings.SetString('NameMark', earlier)
        else:
            settings.DeleteItem('NameMark')


lt.run('t_vnames', test)
