#! python 3
# The Grasshopper tab: "miniVivisection", and "V°" (its short name, with the chosen mark) when the
# tabs don't fit -- Grasshopper shortens them from the right, then shows symbols (V).
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('miniVivisection.GH') is not None)


def test():
    yield 2
    short = lt.static_call('VNames', 'Of', '').strip()  # V and the chosen mark: V° by default
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import System
    import Grasshopper
    server = Grasshopper.Instances.ComponentServer
    lt.check('the short name and the symbol', (server.GetCategoryShortName('miniVivisection'), server.GetCategorySymbolName('miniVivisection')), (short, 'V'))

    editor = Grasshopper.Instances.DocumentEditor
    flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
    ribbon = editor.GetType().GetProperty('Ribbon', flags).GetValue(editor)
    yield lt.wait_for(lambda: any(t.NameFull == 'miniVivisection' for t in ribbon.Tabs), 60)
    tabs = list(ribbon.Tabs)
    tab = [t for t in tabs if t.NameFull == 'miniVivisection'][0]
    lt.check('the ribbon\'s tab', (tab.NameFull, tab.NameShort, tab.NameSymbol), ('miniVivisection', short, 'V'))
    lt.note('tabs, left to right: ' + ', '.join(t.NameFull for t in tabs))

    # Narrower and narrower: the tab's display, and the ribbon's width
    seen = []
    old = editor.Width
    try:
        for width in range(4000, 199, -50):
            editor.Width = width
            ribbon.LayoutRibbon()
            seen.append((ribbon.ClientRectangle.Width, str(tab.DisplayStyle)))
    finally:
        editor.Width = old
        ribbon.LayoutRibbon()
    order = []
    for _, style in seen:
        if not order or order[-1] != style:
            order.append(style)
    lt.note('display while narrowing: ' + ' -> '.join('%s from %d px' % (s, next(w for w, x in seen if x == s)) for s in order))
    lt.check('wide enough: miniVivisection', seen[0][1], 'FullName')
    lt.check('narrower: V' + short[1:] + ', then the symbol', [s for s in order if s in ('FullName', 'ShortName', 'Symbol')], ['FullName', 'ShortName', 'Symbol'])


lt.run('t_ghtab', test)
