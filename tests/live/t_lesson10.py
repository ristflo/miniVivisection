#! python 3
# Lesson 10 (visualizers), as its TRY THIS says: paused with the beams, Edit in Rhino on  beams
# makes a line per Beam (the visualizer draw_beam); the sloping one's low end moved up in Rhino
# goes back into its Beam through the inverse (move_beam).
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino

LESSON = os.path.join(lt.ROOT, r'examples\10_visualizers.py')


def paused_controls():
    hook = lt.hook()
    return list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))


def in_paused_script(code):
    return lt.static_call('PythonBridge', 'Exec', code, True).strip()


def test():
    yield 2
    # The lesson, paused at its BREAKPOINT line (an error right after it pauses the debug run there)
    with open(LESSON, encoding='utf-8') as f:
        text = f.read()
    marked = [line for line in text.split('\n') if '# <-- BREAKPOINT' in line][0]
    path = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'lesson10.py')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(text.replace(marked, marked + "\nraise RuntimeError('paused here, with the beams')"))
    Rhino.RhinoApp.RunScript('_-ScriptEditor _Run _Debug=_On "%s"' % path, False)
    yield lt.wait_for(lambda: len(paused_controls()) > 0, 60)
    controls = paused_controls()
    lt.check('paused with the beams', len(controls), 1)
    if not controls:
        return
    controls = controls[0]
    lt.check('B3 slopes: its end at 0', in_paused_script('print(beams[2].end)'), '[12, 12, 0]')

    hook = lt.hook()
    edits = lt.member(hook, 'Edits')
    edits.Start(controls, 'beams', 'auto')
    yield lt.wait_for(lambda: edits.Count >= 4, 15)
    lt.check('Edit in Rhino: a line per beam', edits.Count, 4)
    targets = lt.member(edits, '_targets')
    doc = Rhino.RhinoDoc.ActiveDoc
    third = [key for key in targets.Keys if lt.member(targets[key], 'Index') == 2]
    lt.check('the third beam\'s line', len(third), 1)
    if third:
        line = doc.Objects.FindId(third[0]).Geometry
        lt.note('B3 drawn %s -> %s, mode %s' % (line.PointAtStart, line.PointAtEnd, lt.member(targets[third[0]], 'Mode')))
        # Its low end dragged up to 3.5 (the Gumball: the object replaced)
        end = line.PointAtEnd
        moved = Rhino.Geometry.LineCurve(line.PointAtStart, Rhino.Geometry.Point3d(end.X, end.Y, 3.5))
        lt.check('the line moved in Rhino', doc.Objects.Replace(third[0], moved), True)
        yield 2
        lt.check('written back: B3\'s end at 3.5', in_paused_script('print(beams[2].end)'), '[12.0, 12.0, 3.5]')
        lt.check('the other beams as they were', in_paused_script('print(beams[0].start, beams[0].end)'), '[0, 0, 3.5] [12, 0, 3.5]')
    controls.Stop()
    yield 3


lt.run('t_lesson10', test)
