#! python 3
# Hover in a call: on Point3d in "pts.append(rg.Point3d(...))" the tooltip offers "Watch
# rg.Point3d(…)" -- the whole call, a constructor; on append it doesn't (a method: watching it
# would append at every pause). The link's watch is the point itself.
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

SCRIPT = '''#! python 3
import math
import Rhino.Geometry as rg
pts = []
ri, height = 2.0, 5.0
for k in range(3):
    th = k * 0.5
    t = k / 2.0
    pts.append(rg.Point3d(ri * math.cos(th), ri * math.sin(th), height * t))
raise RuntimeError('paused here, after the loop')
'''
LINE = 9
CALL = 'rg.Point3d(ri * math.cos(th), ri * math.sin(th), height * t)'


def paused_controls():
    hook = lt.hook()
    return list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))


def test():
    yield 2
    path = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'call_watch.py')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(SCRIPT)
    old = lt.static_call('MiniVivisectionSettings', 'GetBool', 'TooltipActions')
    lt.static_call('MiniVivisectionSettings', 'Set', 'TooltipActions', True)
    try:
        Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_On "%s"' % path, False)
        yield lt.wait_for(lambda: len(paused_controls()) > 0, 60)
        controls = paused_controls()
        lt.check('paused (at the error after the loop)', len(controls), 1)
        if not controls:
            return
        build = lt.plugin_type('HoverValues').GetMethod('BuildTip', lt.ANY)
        line_text = SCRIPT.split('\n')[LINE - 1]
        call_at = lambda name: lt.static_call('HoverText', 'CallAt', line_text, line_text.index(name) + 1)

        on_point = build.Invoke(None, [controls[0], 'rg.Point3d', LINE, None, True, call_at('Point3d')]) or ''
        lt.note('tooltip on Point3d:\n' + on_point)
        import urllib.parse
        lt.check('on Point3d: "Watch rg.Point3d(…)"', 'Watch rg.Point3d' in on_point.replace('\\', ''), True)  # the link text is Markdown-escaped
        lt.check('its link takes the whole call', 'e=' + urllib.parse.quote(CALL, safe="()*-._~!'") in on_point or urllib.parse.quote(CALL) in on_point, True)

        on_append = build.Invoke(None, [controls[0], 'pts.append', LINE, None, True, call_at('append')]) or ''
        lt.note('tooltip on append:\n' + on_append)
        lt.check('on append: no call link (a method)', 'Watch pts.append' in on_append, False)

        hook = lt.hook()
        lt.check('the link: the call added to V' + chr(0xB0) + 'Watch', hook.GetType().GetMethod('AddRhinoWatch', lt.ANY).Invoke(hook, [controls[0], CALL, None]), True)
        value = lt.static_call('PythonBridge', 'Exec', 'print(%s)' % CALL, True).strip()
        lt.check('its value: the last point (k = 2)', value, str(lt.static_call('PythonBridge', 'Exec', 'print(pts[-1])', True).strip()))
        controls[0].Stop()
        yield 3
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'TooltipActions', old)


lt.run('t_callwatch', test)
