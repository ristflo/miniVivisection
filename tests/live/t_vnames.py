#! python 3
# "Mark in the V names": V°Watch by default; another mark shows in V°Log at once; the V°Edit
# component takes the mark when it's made.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

DEG = chr(0x00B0)
ON_LINE = chr(0x2223)
THIN = chr(0x2009)


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('miniVivisection.GH') is not None)


def last_log():
    entries = list(lt.static_get('UserLog', 'Entries'))
    return entries[-1].Text if entries else ''


def test():
    yield 2
    mark = lambda: lt.static_call('MiniVivisectionSettings', 'GetText', 'NameMark')
    user_mark = mark()
    try:
        lt.static_call('MiniVivisectionSettings', 'Set', 'NameMark', DEG)
        lt.check('the default mark', lt.static_call('MiniVivisectionSettings', 'Find', 'NameMark').Default, DEG)
        lt.check('V' + DEG + 'Watch', lt.static_call('VNames', 'Of', 'Watch'), 'V' + DEG + 'Watch')
        lt.static_call('UserLog', 'Info', 'see V Log and V Settings', None)
        lt.check('V' + DEG + 'Log in V' + DEG + 'Log', last_log(), 'see V' + DEG + 'Log and V' + DEG + 'Settings')

        choices = {DEG: DEG, '|': '|', ON_LINE + ' (on the line)': ON_LINE, '| with thin spaces': THIN + '|' + THIN,
                   ON_LINE + ' with thin spaces': THIN + ON_LINE + THIN}
        seen = {}
        for choice, expected in choices.items():
            lt.static_call('MiniVivisectionSettings', 'Set', 'NameMark', choice)
            lt.static_call('UserLog', 'Info', 'open V Watch', None)
            seen[choice] = (lt.static_call('VNames', 'Of', 'Python') == 'V' + expected + 'Python', last_log() == 'open V' + expected + 'Watch')
        lt.check('every mark, in names and in V' + DEG + 'Log', all(a and b for a, b in seen.values()), True)
        lt.check('a setting\'s label follows', lt.static_call('MiniVivisectionSettings', 'Find', 'TabPython').Label,
                 'Tab (bottom): V' + THIN + ON_LINE + THIN + 'Python')

        # The V°Edit component: its name has the mark when it's made.
        lt.static_call('MiniVivisectionSettings', 'Set', 'NameMark', DEG)
        Rhino.RhinoApp.RunScript('_Grasshopper', False)
        yield lt.wait_for(grasshopper_ready, 120)
        import System
        import Grasshopper
        vedit_id = System.Guid('b7d2e8a4-3c1f-4e6b-a9d0-5f2c7e1b8d63')
        yield lt.wait_for(lambda: Grasshopper.Instances.ComponentServer.EmitObjectProxy(vedit_id) is not None, 60)
        made = Grasshopper.Instances.ComponentServer.EmitObjectProxy(vedit_id).CreateInstance()
        lt.check('V' + DEG + 'Edit: name and nickname', (made.Name, made.NickName), ('V' + DEG + 'Edit', 'V' + DEG + 'Edit'))
        lt.static_call('MiniVivisectionSettings', 'Set', 'NameMark', '|')
        made = Grasshopper.Instances.ComponentServer.EmitObjectProxy(vedit_id).CreateInstance()
        lt.check('with | chosen, a new one is V|Edit', made.NickName, 'V|Edit')
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'NameMark', user_mark)


lt.run('t_vnames', test)
