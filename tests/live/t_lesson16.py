#! python 3
# Lesson 16 (the watch face), as its TRY THIS says: Debug, Break now while it runs, see the
# bug in the paused values, redefine angle_of in the paused script (V Python), continue --
# the hand then runs clockwise from 12.
import math
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino
from Rhino.Runtime.Code.Execution.Debugging import DebugControlsState

LESSON = os.path.join(lt.ROOT, r'examples\16_long_running_watch.py')


def controls_in(state):
    hook = lt.hook()
    return list(hook.GetType().GetMethod('ControlsIn', lt.ANY).Invoke(hook, [state]))


def in_paused_script(code):
    # What V Python does: run code in the paused frame (here: the UI thread, paused).
    return lt.static_call('PythonBridge', 'Exec', code, True).strip()


def hand_angle(doc):
    layer = doc.Layers.FindName('Watch face')
    hands = [o for o in doc.Objects.FindByLayer(layer) if o.Attributes.ObjectColor.R == 255 and isinstance(o.Geometry, Rhino.Geometry.LineCurve)]
    end = hands[0].Geometry.PointAtEnd
    return math.atan2(end.Y, end.X)


def same_angle(a, b):
    return abs(math.atan2(math.sin(a - b), math.cos(a - b))) < 2 * math.pi / 60 * 1.5   # within a mark and a half


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_On "%s"' % LESSON, False)
    yield lt.wait_for(lambda: len(controls_in(DebugControlsState.Debugging)) > 0, 60)
    controls = controls_in(DebugControlsState.Debugging)[0]
    yield 2
    doc = Rhino.RhinoDoc.ActiveDoc
    layer = doc.Layers.FindName('Watch face')
    objects = list(doc.Objects.FindByLayer(layer)) if layer else []
    kinds = {}
    for o in objects:
        kinds[o.Geometry.GetType().Name] = kinds.get(o.Geometry.GetType().Name, 0) + 1
    lt.note('drawn: %s' % kinds)
    lt.check('the face: a circle, 60 markers and the hand, 12 numbers, the center', (len(objects), kinds.get('TextEntity')), (75, 12))

    lt.static_call('Threads', 'RequestBreak', controls, System.Int64(0))
    yield lt.wait_for(lambda: controls.State == DebugControlsState.Paused, 20)
    lt.check('Break now paused the running script', str(controls.State), 'Paused')
    tick = int(in_paused_script('print(tick % 60)'))
    lt.note('paused at mark %d' % tick)
    lt.check('the bug: the hand at the counter-clockwise angle from 3 o\'clock', same_angle(hand_angle(doc), tick * 2 * math.pi / 60), True)

    in_paused_script('def angle_of(tick):\n    return math.pi / 2 - tick * 2 * math.pi / 60\n')
    controls.Continue()
    yield 1.5
    lt.static_call('Threads', 'RequestBreak', controls, System.Int64(0))
    yield lt.wait_for(lambda: controls.State == DebugControlsState.Paused, 20)
    tick = int(in_paused_script('print(tick % 60)'))
    lt.note('paused again at mark %d' % tick)
    lt.check('fixed while it ran: clockwise from 12', same_angle(hand_angle(doc), math.pi / 2 - tick * 2 * math.pi / 60), True)
    controls.Stop()
    yield lt.wait_for(lambda: controls.State not in (DebugControlsState.Paused, DebugControlsState.Debugging), 20)
    lt.check('stopped', controls.State not in (DebugControlsState.Paused, DebugControlsState.Debugging), True)


lt.run('t_lesson16', test)
