#! python 3
# Review 2026-10-27, M5: a protected definition, modified: V Protect's Revert (the button's), its
# protect:revert link from the component and from a Panel refuse while a script is paused or a
# component computes -- said, the definition left open as it is; afterwards Revert reverts. The
# pause and the computing are stood in for (AnyPaused says yes; FileLinksWait says computing), as
# in t_linkwhilepaused.
import os
import shutil
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

FOLDER = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'protectpaused-%d' % os.getpid())


def ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('miniVivisection.GH') is not None)


def component(name):
    import Grasshopper
    for proxy in Grasshopper.Instances.ComponentServer.ObjectProxies:
        if proxy.Desc.Name.replace(chr(0xB0), ' ').replace(' ', '') == name.replace(' ', '') and not proxy.Obsolete:
            obj = Grasshopper.Instances.ComponentServer.EmitObject(proxy.Guid)
            obj.CreateAttributes()
            return obj
    raise RuntimeError('no component ' + name)


def call(obj, method, *args):
    return [m for m in obj.GetType().GetMethods(lt.ANY) if m.Name == method and len(m.GetParameters()) == len(args)][0].Invoke(obj, list(args))


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(ready, 120)
    import clr
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_DocumentIO
    from Grasshopper.Kernel.Special import GH_NumberSlider
    import make_gh_examples as mk
    shutil.rmtree(FOLDER, ignore_errors=True)
    os.makedirs(FOLDER)
    definition = os.path.join(FOLDER, 'paused.gh')

    # A protected definition, its slider moved: modified
    doc = GH_Document()
    protect = component('V Protect')
    protect.Attributes.Pivot = System.Drawing.PointF(300, 80)
    slider = mk._place(GH_NumberSlider(), 20, 230)
    slider.SetInitCode('0<3<10')
    note = mk.panel('[Revert](protect:revert)', 20, 20, width=200, height=60)
    for obj in (protect, slider, note):
        doc.AddObject(obj, False)
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    Grasshopper.Instances.ActiveCanvas.Document = doc
    doc.FilePath = definition
    GH_DocumentIO(doc).SaveQuiet(definition)
    doc.Enabled = True
    protect.ExpireSolution(True)
    yield 2
    call(protect, 'Protect')
    yield 1
    slider.SetSliderValue(System.Decimal(7))
    slider.ExpireSolution(True)
    yield 2
    call(protect, 'Look')
    lt.check('modified: revert offered', lt.member(protect, 'NeedsRevert'), True)
    open_now = lambda: Grasshopper.Instances.DocumentServer.Contains(doc)

    gh = lt.assembly('miniVivisection.GH')
    static = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    follow_link = protect.GetType().GetMethod('FollowLink', lt.ANY)
    panel_follow = gh.GetType('MiniVivisection.GH.ScriptPanels').GetMethod('Follow', static)

    def refused(why, expected):
        said = call(protect, 'Revert', False) or ''
        lt.check(why + ': the Revert button refuses, said', expected in said, True)
        said = follow_link.Invoke(None, [doc, 'revert']) or ''
        lt.check(why + ': protect:revert refuses, said', expected in said, True)
        said = panel_follow.Invoke(None, ['protect:revert', FOLDER, note]) or ''
        lt.check(why + ': a Panel\'s protect:revert refuses, said', expected in said, True)

    # A script paused (stood in for)
    guard = lt.plugin_type('GrasshopperPauseGuard')
    any_paused = guard.GetProperty('AnyPaused', lt.ANY)
    real = any_paused.GetValue(None)
    always = System.Delegate.CreateDelegate(clr.GetClrType(System.Func[bool]), clr.GetClrType(System.Environment).GetProperty('Is64BitProcess').GetGetMethod())
    any_paused.SetValue(None, always)
    try:
        refused('paused', 'not while a script is paused')
        yield 2
        lt.check('paused: the definition still open, still modified', (open_now(), lt.member(protect, 'NeedsRevert')), (True, True))
    finally:
        any_paused.SetValue(None, real)

    # A component computing (stood in for: what the guard asks says so)
    links = lt.plugin_type('MarkdownLinks')
    wait = links.GetField('FileLinksWait', static)
    real_wait = wait.GetValue(None)
    wait.SetValue(None, System.Func[System.String](lambda: 'a Grasshopper script component computes'))
    try:
        refused('computing', 'not while a Grasshopper script component computes')
        yield 2
        lt.check('computing: the definition still open', open_now(), True)
    finally:
        wait.SetValue(None, real_wait)

    # Neither: Revert reverts
    lt.check('afterwards: Revert reverts', call(protect, 'Revert', False), None)
    yield 3
    reopened = [d for d in Grasshopper.Instances.DocumentServer if d.FilePath and os.path.normcase(d.FilePath) == os.path.normcase(definition)]
    lt.check('the definition opened again, the old one gone', (len(reopened), open_now()), (1, False))
    for d in reopened:
        d.Enabled = False


lt.run('t_protect_paused', test)
