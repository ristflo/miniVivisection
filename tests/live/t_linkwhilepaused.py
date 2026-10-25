#! python 3
# Review 2026-10-15, B1: a link that opens a file (a model replacing Rhino's, a definition)
# isn't followed while a script is paused -- said instead; a link to an object needs no file
# and still works. The pause is stood in for (AnyPaused says yes).
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

LESSONS = os.path.join(lt.ROOT, r'examples')


def test():
    yield 2
    import clr
    links = lt.plugin_type('MarkdownLinks')
    static = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    follow = lambda url, folder: links.GetMethod('Follow', static).Invoke(None, [url, folder])
    guard = lt.plugin_type('GrasshopperPauseGuard')
    any_paused = guard.GetProperty('AnyPaused', lt.ANY)
    real = any_paused.GetValue(None)
    always = System.Delegate.CreateDelegate(clr.GetClrType(System.Func[bool]), clr.GetClrType(System.Environment).GetProperty('Is64BitProcess').GetGetMethod())
    any_paused.SetValue(None, always)
    try:
        folder = os.path.join(LESSONS, 'grasshopper')
        said = follow('rhino:../00_overview.3dm#Logo', folder) or ''
        lt.check('a model link waits while a script is paused', 'not while a script is paused' in said, True)
        lt.check('and says what it would open', '00_overview.3dm' in said, True)
        said = follow('gh:gh11_markdown_notes.gh#Ring', folder) or ''
        lt.check('a definition link waits too', 'not while a script is paused' in said, True)
        lt.check('the model stayed', Rhino.RhinoDoc.ActiveDoc.Path or '', '')
        said = follow('rhino:Nothing', folder) or ''
        lt.check('a link to an object (no file) is tried as before', 'No Rhino object' in said, True)
    finally:
        any_paused.SetValue(None, real)
    said = follow('rhino:x.gh#A', None) or ''
    lt.check('rhino: with a definition: refused (that is a gh: link)', 'gh: link' in said, True)
    said = follow('gh:a.3dm#B', None) or ''
    lt.check('gh: with a model: refused (that is a rhino: link)', 'rhino: link' in said, True)


lt.run('t_linkwhilepaused', test)
