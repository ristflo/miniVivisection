#! python 3
# Stop in a Grasshopper script component's editor, pressed as a click does (the editor's own
# Stop button, its command through PushButton.ExecuteCommandAsync): the debugging ends and
# doesn't pause again -- not in the component's next runs (one per item), not in the solution
# that does what was held back while paused (an input changed meanwhile). With a breakpoint
# and with the pause at an uncaught error; Always debug, and Stop and record with the editor's
# Debug button. Rhino 8 and 9. Found: in Rhino 9 Stop and the steps were off in the component's
# editor when Grasshopper had started the debug run (Rhino9DebugButtons); in both, what was held
# back paused again at the breakpoint after Stop under Always debug (GrasshopperPauseGuard.PlainAfterStop).
import os
import sys
import time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def command_name(c):
    try:
        prop = c.GetType().GetProperty('Command', lt.ANY)
    except Exception:
        return None
    command = prop.GetValue(c) if prop is not None else None
    return command.GetType().Name.split('`')[0] if command is not None else None


def buttons(name_test):
    """(window, control) of every control in an open window whose command's name passes."""
    import Eto.Forms
    found = []
    for window in Eto.Forms.Application.Instance.Windows:
        try:
            children = list(window.Children)
        except Exception:
            continue
        for c in children:
            name = command_name(c)
            if name and name_test(name):
                found.append((window, c))
    return found


def press(control):
    """A click's own path: PushButton.ExecuteCommandAsync, else the command."""
    import Eto.Forms
    method = None
    t = control.GetType()
    while t is not None and method is None:
        method = t.GetMethod('ExecuteCommandAsync', lt.ANY)
        t = t.BaseType
    if method is not None:
        Eto.Forms.Application.Instance.AsyncInvoke(lambda: method.Invoke(control, None))
        return 'ExecuteCommandAsync'
    import System.Threading
    command = lt.member(control, 'Command')
    execute = sorted([m for m in command.GetType().GetMethods() if m.Name == 'Execute'], key=lambda m: len(m.GetParameters()))[0]
    args = [System.Threading.CancellationToken() if p.ParameterType.Name == 'CancellationToken' else None for p in execute.GetParameters()]
    Eto.Forms.Application.Instance.AsyncInvoke(lambda: execute.Invoke(command, args))
    return 'Command.Execute'


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess
    from Grasshopper.Kernel.Parameters import Param_Integer
    from Grasshopper.Kernel.Types import GH_Integer
    from Rhino.Runtime.Code.Execution.Debugging import CodeReferenceBreakpoint
    import make_gh_examples as mk
    hook = lt.hook()
    paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
    runs = lt.plugin_type('GrasshopperRuns')
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')

    doc = GH_Document()
    x = mk._place(mk.persistent(Param_Integer(), 'x', {(0,): [GH_Integer(i) for i in range(5)]}), 20, 60)
    each = mk._place(mk.script('Each', 'Each', '#! python 3\ny = x * 2\na = 10 // (y - 4)\n', [('x', GH_ParamAccess.item, 'int')], ['a']), 300, 60)
    for obj in (x, each):
        doc.AddObject(obj, False)
    each.Params.Input[0].AddSource(x)
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    Grasshopper.Instances.ActiveCanvas.Document = doc
    doc.Enabled = True

    def recompute():
        # Scheduled: a solution started inside a test step would pause inside it (the runner
        # doesn't step again while a step runs).
        each.ExpireSolution(False)
        doc.ScheduleSolution(50)

    def watch(seconds, label):
        """Pauses seen within seconds after Stop (each one stopped again, so nothing hangs)."""
        seen = []
        end = time.time() + seconds
        while time.time() < end:
            now = paused()
            if now:
                frame = now[0].Results.CurrentThread.CurrentFrame
                seen.append(frame.Reference.Position.LineNumber)
                lt.note('%s: paused again at line %d' % (label, seen[-1]))
                now[0].Stop()
                if len(seen) >= 6:
                    break
                yield 1
            else:
                yield 0.25
        watch.seen = seen

    def stop_by_button(label):
        # Rhino 9: Stop and the steps were off in the component's editor when Grasshopper had
        # started the debug run (the editor's RunKind not Debug): Rhino9DebugButtons.
        for window, c in buttons(lambda n: 'DebugStop' in n):
            try:
                codes = lt.member(window.DataContext, 'Codes')
                lt.note('%s: the editor: RunKind %s, DebugControlsState %s, IsRunning %s; Stop: CanExecute %s' % (
                    label, lt.member(codes, 'RunKind'), lt.member(codes, 'DebugControlsState'), lt.member(codes, 'IsRunning'),
                    lt.member(lt.member(c, 'Command'), 'CanExecute')))
            except Exception as ex:
                lt.note('%s: the editor state: %s' % (label, ex))
        stops = [b for b in buttons(lambda n: 'DebugStop' in n) if b[1].Enabled and b[1].Visible]
        lt.note('%s: enabled Stop buttons: %s' % (label, ['%s in %s' % (command_name(c), w.Title) for w, c in stops]))
        lt.check('%s: an enabled Stop button in the component\'s editor' % label, len(stops) >= 1, True)
        if not stops:
            for c in paused():
                c.Stop()
            return
        lt.note('%s: pressed by %s' % (label, press(stops[0][1])))
        yield lt.wait_for(lambda: len(paused()) == 0, 10)
        lt.check('%s: the pressed Stop ended the pause' % label, len(paused()), 0)
        yield 1
        lt.check('%s: Stop off again' % label, bool(stops[0][1].Enabled), False)

    try:
        doc.NewSolution(False)
        yield 3
        code = runs.GetMethod('CodeOf', lt.ANY).Invoke(None, [each])
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
        runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [each])
        yield lt.wait_for(lambda: runs.GetMethod('HasVisibleEditor', lt.ANY).Invoke(None, [each]), 60)
        yield 3
        code = runs.GetMethod('CodeOf', lt.ANY).Invoke(None, [each])
        lt.check('the component has a debugger', code is not None and code.HasDebugControls, True)

        # 1. A breakpoint, run per item: paused in the first run, Stop -- no more pauses.
        breakpoint = CodeReferenceBreakpoint(code, 2)
        code.DebugControls.Breakpoints.Add(breakpoint)
        recompute()
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        lt.check('1 breakpoint: paused', len(paused()), 1)
        if paused():
            yield 1
            yield from stop_by_button('1 breakpoint')
            yield from watch(8, '1 breakpoint')
            lt.check('1 breakpoint: Stop ended it, no pause in the next runs', watch.seen, [])
            lt.check('1 breakpoint: the solution is over', str(doc.SolutionState) != 'Process', True)

        # 2. The same, an input changed while paused (held back, done after the pause).
        yield 2
        recompute()
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        lt.check('2 held back: paused', len(paused()), 1)
        if paused():
            x.ExpireSolution(True)  # as a slider moved while paused: held back
            yield 1
            yield from stop_by_button('2 held back')
            yield from watch(8, '2 held back')
            lt.check('2 held back: Stop ended it, no pause in what was held back', watch.seen, [])
        code.DebugControls.Breakpoints.Remove(breakpoint)

        # 3. The pause at an uncaught error (x = 2: 10 // 0), Stop -- no more pauses.
        yield 2
        recompute()
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        lt.check('3 error: paused at the error', len(paused()), 1)
        if paused():
            yield 1
            yield from stop_by_button('3 error')
            yield from watch(8, '3 error')
            lt.check('3 error: Stop ended it, no pause in the next runs', watch.seen, [])

        # 4. Stop and record, the editor's own Debug button, a breakpoint.
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Stop and record')
        yield 3
        code.DebugControls.Breakpoints.Add(breakpoint)
        yield 1
        # Rhino 8: Debug (CodeRunDebugCommand); Rhino 9: Run / Debug / Continue in one button, Debug with a breakpoint.
        debugs = [b for b in buttons(lambda n: n in ('CodeRunDebugCommand', 'GHRunDebugContinueCommand')) if b[1].Enabled and b[1].Visible]
        lt.note('4: Debug buttons: %s' % [command_name(c) for w, c in debugs])
        lt.check('4 record: the component editor\'s Debug button', len(debugs) >= 1, True)
        if debugs:
            press(debugs[0][1])
            yield lt.wait_for(lambda: len(paused()) > 0, 30)
            lt.check('4 record: paused', len(paused()), 1)
            if paused():
                yield 1
                yield from stop_by_button('4 record')
                yield from watch(8, '4 record')
                lt.check('4 record: Stop ended it, no pause in the next runs', watch.seen, [])
        code.DebugControls.Breakpoints.Remove(breakpoint)
    finally:
        for c in paused():
            c.Stop()
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)
    doc.Enabled = False


lt.run('t_ghstop', test)
