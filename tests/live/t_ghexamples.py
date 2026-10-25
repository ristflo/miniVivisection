#! python 3
# The Grasshopper lessons (examples/grasshopper/gh*.gh): made by tools/make_gh_examples.py
# here in Rhino, then each opened and checked as its panel says.
import math
import os
import sys
import threading
import time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def editor_shows(path):
    """Whether a Script Editor shows the file at path (its active script)."""
    for window in lt.editors():
        code = System.Activator.CreateInstance(lt.plugin_type('ScriptEditorAccess'), [window]).Code
        if path and code is not None and code.Uri is not None and code.Uri.IsFile and code.Uri.LocalPath.lower() == path.lower():
            return True
    return False


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    # From Stop and record, the default (this Rhino's own setting back at the end): the lessons
    # that want Always debug switch it on; GH 3's stop is Stop and record's (Always debug would
    # pause it: t_ghhidden).
    user_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Stop and record')
    import Grasshopper
    from Grasshopper.Kernel import GH_DocumentIO, GH_RuntimeMessageLevel
    import make_gh_examples
    written = make_gh_examples.make()
    lt.check('twelve lessons written', [os.path.basename(p) for p in written],
             ['gh1_which_branch_failed.gh', 'gh2_the_one_wrong_item.gh', 'gh3_stop_a_hang.gh', 'gh4_v_edit.gh', 'gh5_runtime_error.gh', 'gh6_threads_in_a_component.gh', 'gh7_parallel_run.gh',
              'gh8_long_running.gh', 'gh9_script_from_a_panel.gh', 'gh10_script_from_a_file.gh', 'gh11_markdown_notes.gh',
              'gh12_markdown_parser.gh'])

    def open_doc(path):
        io = GH_DocumentIO()
        lt.check('opens: ' + os.path.basename(path), io.Open(path), True)
        doc = io.Document
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        doc.Enabled = True
        doc.NewSolution(False)
        return doc

    def find(doc, nickname):
        return [o for o in doc.Objects if o.NickName == nickname][0]

    # Every lesson ends with links to the lesson before and after (where there is one), and a
    # click on the next one opens it -- or shows it, when it is open already
    for n, path in enumerate(written, 1):
        io = GH_DocumentIO()
        io.Open(path)
        text = [o for o in io.Document.Objects if o.NickName == 'Lesson'][0].UserText
        tail = text.rstrip().split('\n')[-1]
        want = (['gh%d_' % (n - 1)] if n > 1 else []) + ['gh0_overview'] + (['gh%d_' % (n + 1)] if n < len(written) else [])
        lt.check('GH %d ends with the links %s' % (n, want), [w for w in want if '(' + w in tail] == want and tail.count('](') == len(want), True)
    overview = make_gh_examples.gh0(os.path.dirname(written[0]))
    io = GH_DocumentIO()
    io.Open(overview)
    text = [o for o in io.Document.Objects if o.NickName == 'Lesson'][0].UserText
    lt.check('the overview links every lesson', all('(' + os.path.basename(p) + ')' in text for p in written) and '(../01_watch_geometry.py)' in text, True)
    lt.check('the overview has its V Protect', any(o.ComponentGuid == make_gh_examples.PROTECT for o in io.Document.Objects), True)
    # Every definition: what is right of its Panel starts GAP right of it -- inputs' wires clear of the text
    for path in [overview] + written:
        io = GH_DocumentIO()
        io.Open(path)
        for o in io.Document.Objects:
            o.Attributes.ExpireLayout()
            o.Attributes.PerformLayout()
        lesson = [o for o in io.Document.Objects if o.NickName == 'Lesson'][0].Attributes.Bounds
        beside = [o for o in io.Document.Objects if o.Attributes.Bounds.Left > lesson.Left and o.Attributes.Bounds.Top < lesson.Bottom]
        nearest = min(beside, key=lambda o: o.Attributes.Bounds.Left)
        lt.check('%s: the gap right of the Panel (%s nearest)' % (os.path.basename(path), nearest.NickName), int(round(nearest.Attributes.Bounds.Left - lesson.Right)), make_gh_examples.GAP)
        protect = [o.Attributes.Bounds for o in io.Document.Objects if o.ComponentGuid == make_gh_examples.PROTECT]
        above = lesson if protect[0].Left < lesson.Right else nearest.Attributes.Bounds  # gh0: under its slider
        lt.check('%s: V Protect under the %s, 30 below, left edges lined up' % (os.path.basename(path), 'Panel' if above is lesson else nearest.NickName),
                 (int(round(protect[0].Left - above.Left)), int(round(protect[0].Top - above.Bottom))), (0, 30))
    folder = os.path.dirname(written[0])
    first = open_doc(written[0])
    error = lt.static_call('MarkdownLinks', 'Follow', 'gh2_the_one_wrong_item.gh', folder)
    yield 2
    shown = Grasshopper.Instances.ActiveCanvas.Document
    lt.check('the "next" link opens GH 2', (error, os.path.basename(shown.FilePath or '')), (None, 'gh2_the_one_wrong_item.gh'))
    count = Grasshopper.Instances.DocumentServer.DocumentCount
    Grasshopper.Instances.ActiveCanvas.Document = first
    lt.static_call('MarkdownLinks', 'Follow', 'gh2_the_one_wrong_item.gh', folder)
    yield 1
    lt.check('again: the open one shown, not opened twice', (Grasshopper.Instances.DocumentServer.DocumentCount, os.path.basename(Grasshopper.Instances.ActiveCanvas.Document.FilePath)),
             (count, 'gh2_the_one_wrong_item.gh'))
    for d in list(Grasshopper.Instances.DocumentServer):
        d.Enabled = False
        Grasshopper.Instances.DocumentServer.RemoveDocument(d)

    # gh1: three failing runs, all in branch {0;2}
    doc = open_doc(written[0])
    yield 2
    divide = find(doc, 'Divide')
    state = lt.plugin_type('GrasshopperRuns').GetMethod('StateOf', lt.ANY).Invoke(None, [divide])
    failures = list(lt.member(state, 'Failures'))
    lt.note('gh1 errors: ' + '; '.join(list(divide.RuntimeMessages(GH_RuntimeMessageLevel.Error))[:3]))
    lt.check('gh1: 3 runs fail', len(failures), 3)
    lt.check('gh1: all in branch {0;2}', sorted(lt.member(lt.member(f, 'Run'), 'Item') for f in failures), ['{0;2}[0]', '{0;2}[1]', '{0;2}[2]'])
    remarks = list(divide.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
    lt.check('gh1: the remark names them', any('3 runs failed' in r for r in remarks), True)
    doc.Enabled = False

    # gh2: one post points down -- item {0}[6]
    doc = open_doc(written[1])
    yield 2
    posts = find(doc, 'Posts')
    lt.note('gh2 errors: ' + '; '.join(list(posts.RuntimeMessages(GH_RuntimeMessageLevel.Error))[:3]) + ' | inputs ' + ', '.join(p.NickName + ':' + str(p.Access) for p in posts.Params.Input))
    lines = [g.Value for g in posts.Params.Output[0].VolatileData.AllData(True)]
    down = [i for i, line in enumerate(lines) if line.PointAtEnd.Z < line.PointAtStart.Z]
    lt.check('gh2: 10 posts, one pointing down: item 6', (len(lines), down), (10, [6]))
    doc.Enabled = False

    # gh4: V Edit shows profile [1] in Rhino; the loft is made -- opened as a user does
    Rhino.RhinoApp.RunScript('_-Grasshopper _Document _Open "%s" _Enter' % written[3], False)
    yield lt.wait_for(lambda: Grasshopper.Instances.ActiveCanvas.Document is not None and 'gh4' in (Grasshopper.Instances.ActiveCanvas.Document.FilePath or ''), 30)
    doc = Grasshopper.Instances.ActiveCanvas.Document
    yield lt.wait_for(lambda: len(Rhino.RhinoDoc.ActiveDoc.Objects.FindByUserString('minivivisection.gh', '*', True) or []) == 1, 20)
    lt.check('gh4: one edit object (profile [1])', len(Rhino.RhinoDoc.ActiveDoc.Objects.FindByUserString('minivivisection.gh', '*', True) or []), 1)
    loft = find(doc, 'Loft')
    lt.check('gh4: the loft is made', loft.Params.Output[0].VolatileDataCount, 1)
    vedit = [o for o in doc.Objects if str(o.ComponentGuid) == 'b7d2e8a4-3c1f-4e6b-a9d0-5f2c7e1b8d63'][0]  # V Edit
    lt.note('gh4 V Edit says: %s | %s | shown %d' % (vedit.Message, '; '.join(list(vedit.RuntimeMessages(GH_RuntimeMessageLevel.Remark)) + list(vedit.RuntimeMessages(GH_RuntimeMessageLevel.Warning))), lt.member(vedit, '_shown').Count))
    doc.Enabled = False

    # gh5: a runtime error; with Always debug and the editor open, the debugger pauses at it
    doc = open_doc(written[4])
    yield 2
    spacing = find(doc, 'Spacing')
    errors = list(spacing.RuntimeMessages(GH_RuntimeMessageLevel.Error))
    lt.check('gh5: the error, as Grasshopper shows it', any('division by zero' in e for e in errors), True)
    state = lt.plugin_type('GrasshopperRuns').GetMethod('StateOf', lt.ANY).Invoke(None, [spacing])
    failures = list(lt.member(state, 'Failures'))
    lt.check('gh5: the failing run and its line', [(lt.member(lt.member(f, 'Run'), 'Run'), lt.member(f, 'Line')) for f in failures], [(1, 6)])
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    try:
        runs = lt.plugin_type('GrasshopperRuns')
        runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [spacing])
        yield lt.wait_for(lambda: runs.GetMethod('HasVisibleEditor', lt.ANY).Invoke(None, [spacing]), 60)
        yield 3
        spacing.ExpireSolution(False)
        doc.ScheduleSolution(50)  # Grasshopper's own timer: the pause doesn't sit inside this test's step
        hook = lt.hook()
        paused = lambda: len(list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))) > 0
        yield lt.wait_for(paused, 30)
        controls = list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
        lt.check('gh5: the debugger paused', len(controls), 1)
        if controls:
            frame = controls[0].Results.CurrentThread.CurrentFrame
            lt.check('gh5: at the error -- line 6, in ratio()', (frame.Reference.Position.LineNumber, str(frame.Event)), (6, 'Exception'))
            controls[0].Stop()
            yield 3
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)
    doc.Enabled = False

    # gh6: threads in a component -- lengths in finishing order; a breakpoint after the join
    # pauses, one inside a worker is let run on (the freeze guard)
    doc = open_doc(written[5])
    yield 3
    measure = find(doc, 'Measure')
    lengths = [g.Value for g in measure.Params.Output[1].VolatileData.AllData(True)]
    lt.check("gh6: lengths in finishing order, not the curves' order",[round(v, 1) for v in lengths], [1.5, 3.0, 6.0, 9.0, 12.0, 15.0])
    tags = [g.Value.Text for g in measure.Params.Output[0].VolatileData.AllData(True)]
    lt.check('gh6: so the tags are wrong', tags, ['1.5', '3.0', '6.0', '9.0', '12.0', '15.0'])
    old_mode = lt.static_call('MiniVivisectionSettings', 'GetText', 'GrasshopperScripts')
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    try:
        runs = lt.plugin_type('GrasshopperRuns')
        runs.GetMethod('OpenEditor', lt.ANY).Invoke(None, [measure])
        yield lt.wait_for(lambda: runs.GetMethod('HasVisibleEditor', lt.ANY).Invoke(None, [measure]), 60)
        yield 3
        code = runs.GetMethod('CodeOf', lt.ANY).Invoke(None, [measure])
        from Rhino.Runtime.Code.Execution.Debugging import CodeReferenceBreakpoint
        hook = lt.hook()
        paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
        after_join = CodeReferenceBreakpoint(code, 19)
        code.DebugControls.Breakpoints.Add(after_join)
        measure.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('gh6: the breakpoint after the join pauses', len(controls), 1)
        if controls:
            line = controls[0].Results.CurrentThread.CurrentFrame.Reference.Position.LineNumber
            seen = lt.static_call('PythonBridge', 'Exec', 'print([round(v, 1) for v in lengths])', True).strip()
            lt.check('gh6: at line 19, lengths sorted', (line, seen), (19, '[1.5, 3.0, 6.0, 9.0, 12.0, 15.0]'))
            controls[0].Continue()
            lt.note('gh6: continued')
            yield lt.wait_for(lambda: len(paused()) == 0, 20)
            lt.note('gh6: no pause left')
        code.DebugControls.Breakpoints.Remove(after_join)
        in_worker = CodeReferenceBreakpoint(code, 10)
        code.DebugControls.Breakpoints.Add(in_worker)
        lt.note('gh6: breakpoint in the worker set')
        measure.ExpireSolution(False)
        doc.ScheduleSolution(50)
        lt.note('gh6: solution scheduled')
        yield 6
        lt.note('gh6: 6 s later')
        lt.check("gh6: a breakpoint in a worker doesn't pause (Rhino would freeze)",len(paused()), 0)
        lt.check('gh6: the solution finished', measure.Params.Output[1].VolatileDataCount, 6)
        code.DebugControls.Breakpoints.Remove(in_worker)
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', old_mode)
    doc.Enabled = False
    lt.note('after gh6: the generic patches were put back %d time(s)' % lt.static_get('GrasshopperRuns', 'Repairs'))

    # gh3: Run off -- no hang; Run on -- hangs; Break now (the hotkey's path) stops it
    doc = open_doc(written[2])
    yield 2
    halve = find(doc, 'Halve')
    lt.check('gh3: Run off -- solves at once', halve.RuntimeMessageLevel.ToString() != 'Error', True)
    def stop():
        # As a user would: once Rhino hangs in Halve, Ctrl+Alt+B (its thread calls Stop()).
        import time
        for _ in range(60):
            running = lt.static_get('GrasshopperRuns', 's_running')
            if running is not None and running.NickName == 'Halve':
                time.sleep(1)
                lt.progress('gh3: Halve hangs; Stop() returned %r' % lt.static_call('GrasshopperRuns', 'Stop'))
                return
            time.sleep(0.5)
        lt.progress('gh3: Halve never ran')

    stopper = threading.Thread(target=stop, daemon=True)
    stopper.start()
    toggle = find(doc, 'Run')
    toggle.Value = True
    toggle.ExpireSolution(True)  # hangs here until the stop
    yield 3
    remarks = list(halve.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
    lt.note('gh3 remark: ' + (remarks[0] if remarks else '(none)'))
    lt.check('gh3: stopped in run 3, {0}[2]', any('stopped by' in r and 'run 3' in r and '{0}[2]' in r for r in remarks), True)
    toggle.Value = False
    doc.Enabled = False

    hook = lt.hook()
    paused = lambda: list(hook.GetType().GetMethod('PausedControls', lt.ANY).Invoke(hook, None))
    in_paused_script = lambda code: lt.static_call('PythonBridge', 'Exec', code, True).strip()  # as V Python

    # gh8: Run off -- the face, no hands. Always debug, Run on: Ctrl+Alt+B pauses Clock (its
    # editor hidden till then), angle_of redefined in the paused frame, Continue -- the hands
    # after the pause point the right way.
    doc = open_doc(written[7])
    yield 2
    clock, run = find(doc, 'Clock'), find(doc, 'Run')
    lt.check('gh8: Run off -- the face (61 curves), no hands', (clock.Params.Output[0].VolatileDataCount, clock.Params.Output[1].VolatileDataCount), (61, 0))
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Always debug')
    try:
        yield lt.wait_for(lambda: lt.static_get('GrasshopperHiddenEditors', 'Count') >= 1, 60)
        yield 5  # the editor loads

        def press():
            time.sleep(3)
            lt.progress('gh8: Ctrl+Alt+B')
            lt.static_call('BreakHotkey', 'Break')

        threading.Thread(target=press, daemon=True).start()
        run.Value = True
        run.ExpireSolution(False)
        doc.ScheduleSolution(50)
        yield lt.wait_for(lambda: len(paused()) > 0, 30)
        controls = paused()
        lt.check('gh8: Ctrl+Alt+B paused Clock', len(controls), 1)
        if controls:
            tick = in_paused_script('print(tick)')
            lt.note('gh8: paused at mark %s' % tick)
            in_paused_script('def angle_of(tick):\n    return math.pi / 2 - tick * 2 * math.pi / 60\n')
            controls[0].Continue()
            yield lt.wait_for(lambda: len(paused()) == 0 and clock.Params.Output[1].VolatileDataCount == 60, 60)
            ends = [g.Value.PointAtEnd for g in clock.Params.Output[1].VolatileData.AllData(True)]
            lt.check('gh8: 60 hands', len(ends), 60)
            if len(ends) == 60:
                lt.check('gh8: the bug -- the first hand at 3 o\'clock', (round(ends[0].X), round(ends[0].Y)), (40, 0))
                lt.check('gh8: fixed while paused -- the last hand just before 12', (ends[59].X < 0, ends[59].Y > 39), (True, True))
        run.Value = False
        run.ExpireSolution(True)
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', 'Stop and record')
    doc.Enabled = False

    # gh9: the Panel's script runs (the bug: turned in radians); opened in the Script Editor as
    # a working file, the fix saved goes back into the Panel and Twist recomputes
    doc = open_doc(written[8])
    yield 2
    twist, source = find(doc, 'Twist'), find(doc, 'Twist script')
    squares = lambda: [g.Value for g in twist.Params.Output[0].VolatileData.AllData(True)]

    def turn():  # from level 0 to level 1, in degrees
        a, b = [s.PointAtStart for s in squares()[:2]]
        return round(math.degrees(math.atan2(b.Y, b.X) - math.atan2(a.Y, a.X)) % 360, 1)

    lt.check('gh9: 12 squares from the Panel\'s script', len(squares()), 12)
    lt.check('gh9: the bug -- each level turned 5 radians, not 5 degrees', turn(), 286.5)
    inputs = lt.plugin_type('GrasshopperInputScripts')
    inputs.GetMethod('Open', lt.ANY).Invoke(None, [twist, None, None])
    folder = lt.static_get('GrasshopperInputScripts', 'Folder')
    copies = [os.path.join(folder, f) for f in os.listdir(folder) if f.startswith('Twist_script-')]
    working = max(copies, key=os.path.getmtime) if copies else ''
    yield lt.wait_for(lambda: editor_shows(working), 60)
    lt.check('gh9: the Panel\'s script open in the Script Editor', editor_shows(working), True)
    fixed = make_gh_examples.GH9_SCRIPT.replace('import Rhino.Geometry as rg', 'import math\nimport Rhino.Geometry as rg') \
        .replace('square.Rotate(i * angle,', 'square.Rotate(math.radians(i * angle),')
    with open(working, 'w', encoding='utf-8') as f:
        f.write(fixed)  # as the editor's Save
    yield 3
    lt.check('gh9: saved -- the fix is in the Panel', 'math.radians' in source.UserText, True)
    lt.check('gh9: and Twist recomputed -- 5 degrees per level', turn() if len(squares()) >= 2 else None, 5.0)
    doc.Enabled = False

    # gh10: the script is a file the Panel names without a folder (Input Is Path): found next to
    # the definition; Run on hangs, Ctrl+Alt+B (Stop and record) stops it, the file itself opens
    doc = open_doc(written[9])
    yield 2
    spiral, run = find(doc, 'Spiral'), find(doc, 'Run')
    script_file = os.path.join(os.path.dirname(written[9]), make_gh_examples.GH10_FILE)
    lt.check('gh10: its script file next to it', os.path.exists(script_file), True)
    lt.check('gh10: Run off -- the file read, no error', list(spiral.RuntimeMessages(GH_RuntimeMessageLevel.Error)), [])

    def stop_spiral():
        for _ in range(60):
            running = lt.static_get('GrasshopperRuns', 's_running')
            if running is not None and running.NickName == 'Spiral':
                time.sleep(2)
                lt.progress('gh10: Spiral hangs; Ctrl+Alt+B')
                lt.static_call('BreakHotkey', 'Break')
                return
            time.sleep(0.25)
        lt.progress('gh10: Spiral never ran')

    threading.Thread(target=stop_spiral, daemon=True).start()
    run.Value = True
    run.ExpireSolution(True)  # hangs here until the stop
    yield 4
    yield lt.wait_for(lambda: editor_shows(script_file), 30)
    lt.check('gh10: stopped -- gh10_spiral.py itself open in the Script Editor', editor_shows(script_file), True)
    remarks = list(spiral.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
    lt.note('gh10 remark: ' + (remarks[0] if remarks else '(none)'))
    run.Value = False
    doc.Enabled = False

    # gh11: the lesson and the notes in Markdown (saved so); Ring's script from a Panel: the bug,
    # then the fix in the Panel
    doc = open_doc(written[10])
    yield 2
    static = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    script_panels = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.ScriptPanels')
    show_as = lambda panel: str(script_panels.GetMethod('ShowAsOf', static).Invoke(None, [panel]))
    lt.check('gh11: the lesson and the notes shown as Markdown', (show_as(find(doc, 'Lesson')), show_as(find(doc, 'Notes'))), ('Markdown', 'Markdown'))
    ring = find(doc, 'Ring')
    area = lambda: [round(g.Value, 2) for g in ring.Params.Output[0].VolatileData.AllData(True)]
    lt.check('gh11: the bug -- 50.27, not 201.06', area(), [50.27])
    viewport = Grasshopper.GUI.Canvas.GH_Viewport()
    viewport.Width, viewport.Height = 1500, 950
    viewport.Zoom = 1.0
    viewport.MidPoint = System.Drawing.PointF(740, 370)
    picture = os.path.join(os.environ.get('TEMP', '.'), 'minivivisection-live', 'gh11.png')
    Grasshopper.Instances.ActiveCanvas.GenerateHiResImageTile(viewport, System.Drawing.Color.White).Save(picture)
    lt.note('image: ' + picture)
    script_panel = find(doc, 'Ring script')
    script_panel.UserText = script_panel.UserText.replace('(outer - inner) ** 2', '(outer ** 2 - inner ** 2)')
    script_panel.ExpireSolution(True)
    yield 2
    lt.check('gh11: fixed in the Panel -- 201.06', area(), [201.06])
    # Its links: Ring on the canvas, its named view, and Rhino's example (opened in Rhino)
    lesson_links = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.MarkdownBlocks').GetMethod('Parse', static)
    urls = [r.Url for b in lesson_links.Invoke(None, [find(doc, 'Lesson').UserText]) for r in b.Runs if r.Url]
    lt.check('gh11: the lesson\'s links', ('gh:Ring' in urls, 'ghview:Overview' in urls, 'rhino:../00_overview.3dm#Logo' in urls), (True, True, True))
    lt.check('gh11: its named view', [v.Name for v in doc.Properties.ViewList], ['Overview'])
    follow = lambda url: script_panels.GetMethod('Follow', static).Invoke(None, [url, os.path.dirname(written[10]), None])
    lt.check('gh11: gh:Ring followed', follow('gh:Ring'), None)
    yield 1
    middle = Grasshopper.Instances.ActiveCanvas.Viewport.MidPoint
    ring_box = ring.Attributes.Bounds
    lt.check('gh11: the canvas on Ring', abs(middle.X - (ring_box.X + ring_box.Width / 2)) < 5, True)
    lt.check('gh11: <gh:Ring script> (spaces) followed', follow('gh:Ring script'), None)
    Rhino.RhinoDoc.ActiveDoc.Modified = False  # no question about saving the test's model
    lt.check('gh11: rhino:../00_overview.3dm#Logo followed', follow('rhino:../00_overview.3dm#Logo'), None)
    # The red mark lasts 2.5 s, from the follow on: looked at right away (the file opened, its fingerprint taken, may take longer)
    marking = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.MarkdownLinks').GetProperty('Marking', static).GetValue(None)
    lt.check('gh11: its logo marked', marking, True)
    yield 1
    lt.check('gh11: Rhino opened the example', os.path.basename(Rhino.RhinoDoc.ActiveDoc.Path or ''), '00_overview.3dm')
    lt.check('gh11: rhinoview:...#Overview: the example already open, its view restored', follow('rhinoview:../00_overview.3dm#MarkdownText'), None)
    doc.Enabled = False

    # gh12: the checklist through Rhino's Markdig -- the bugs (a task without its space, a table
    # without its rule), then both fixed
    doc = open_doc(written[11])
    yield 2
    lt.check('gh12: the lesson and the checklist shown as Markdown', (show_as(find(doc, 'Lesson')), show_as(find(doc, 'Checklist'))), ('Markdown', 'Markdown'))
    markdig = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.MarkdownMarkdig').GetMethod('Parse', static)

    def parsed(text):
        blocks = list(markdig.Invoke(None, [text]))
        kinds = [str(lt.member(b, 'Kind')) for b in blocks]
        boxes = [lt.member(b, 'Marker') for b in blocks if str(lt.member(b, 'Kind')) == 'ListItem']
        return kinds, boxes

    checklist = find(doc, 'Checklist')
    kinds, boxes = parsed(checklist.UserText)
    lt.note('gh12 before: %s | %s' % (' '.join(kinds), ' '.join(boxes)))
    lt.check('gh12: the bugs -- four boxes (the last task is text), no table', (len(boxes), 'Table' in kinds), (4, False))
    fixed = checklist.UserText.replace('-[ ] Tag', '- [ ] Tag').replace('| Test | Result |', '| Test | Result |\n|---|---|')
    kinds, boxes = parsed(fixed)
    lt.note('gh12 after: %s | %s' % (' '.join(kinds), ' '.join(boxes)))
    lt.check('gh12: fixed -- five boxes and a table', (len(boxes), 'Table' in kinds), (5, True))
    viewport = Grasshopper.GUI.Canvas.GH_Viewport()
    viewport.Width, viewport.Height = 1000, 620
    viewport.Zoom = 1.0
    viewport.MidPoint = System.Drawing.PointF(490, 300)
    picture = os.path.join(os.environ.get('TEMP', '.'), 'minivivisection-live', 'gh12.png')
    Grasshopper.Instances.ActiveCanvas.GenerateHiResImageTile(viewport, System.Drawing.Color.White).Save(picture)
    lt.note('image: ' + picture)
    doc.Enabled = False

    # Every lesson: its Lesson Panel is Markdown and fits its Panel without scrolling (drawn
    # once for its layout), no two objects overlap, and every gh: link names an object of it
    view_of = lambda panel: script_panels.GetMethod('MarkdownViewOf', static).Invoke(None, [panel])
    parse = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.MarkdownBlocks').GetMethod('Parse', static)
    for path in written:
        name = os.path.basename(path)
        io = GH_DocumentIO()
        io.Open(path)
        doc = io.Document
        Grasshopper.Instances.DocumentServer.AddDocument(doc)
        Grasshopper.Instances.ActiveCanvas.Document = doc
        yield 1
        lesson = find(doc, 'Lesson')
        lt.check(name + ': the lesson Panel is Markdown', show_as(lesson), 'Markdown')
        bounds = lesson.Attributes.Bounds
        viewport = Grasshopper.GUI.Canvas.GH_Viewport()
        viewport.Width, viewport.Height = int(bounds.Width + 100), int(bounds.Height + 100)
        viewport.Zoom = 1.0
        viewport.MidPoint = System.Drawing.PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)
        Grasshopper.Instances.ActiveCanvas.GenerateHiResImageTile(viewport, System.Drawing.Color.White)  # drawn: its layout
        view = view_of(lesson)
        content = lesson.Attributes.ContentBounds
        lt.check(name + ': the lesson fits its Panel (no scrolling)', view is not None and view.Height + 8 <= content.Height, True,)
        if view is not None:
            lt.note('%s: text %d of %d' % (name, view.Height, content.Height))
        objects = [o for o in doc.Objects if o.GetType().Name != 'GH_Group']
        overlaps = []
        for i, a in enumerate(objects):
            for b in objects[i + 1:]:
                if a.Attributes.Bounds.IntersectsWith(b.Attributes.Bounds):
                    overlaps.append((a.NickName, b.NickName))
        lt.check(name + ': no two objects overlap', overlaps, [])
        urls = [r.Url for b in parse.Invoke(None, [lesson.UserText]) for r in b.Runs if r.Url and r.Url.startswith('gh:') and '#' not in r.Url]
        missing = [u for u in urls if not any(o.NickName == u[3:] for o in doc.Objects)]
        lt.check(name + ': every gh: link names an object', missing, [])
        doc.Enabled = False
        Grasshopper.Instances.DocumentServer.RemoveDocument(doc)
    lt.static_call('MiniVivisectionSettings', 'Set', 'GrasshopperScripts', user_mode)

lt.run('t_ghexamples', test)
