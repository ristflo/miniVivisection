#! python 3
# Review 2026-10-15, C1 and C2: a copied Markdown text shares the original's id. Editing one
# copy must not take, replace or delete the other copy's parts -- also when the other was
# edited to be longer meanwhile -- and the edited copy gets an id of its own. Leftover parts
# that are locked or hidden are deleted all the same when a text shrinks.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

SHORT = '# Title\n\nA paragraph of text.\n\n---\n'
LONGER = SHORT + '\n- one\n- two\n- three\n'


def test():
    yield 2
    import make_rhino_examples as mk
    doc = Rhino.RhinoDoc.ActiveDoc
    builder = lt.plugin_type('MarkdownRhinoText')
    flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    find = lambda o: builder.GetMethod('Find', flags).Invoke(None, [doc, o])
    update = lambda found, md: list(builder.GetMethod('Update', flags).Invoke(None, [doc, found, md, found.Frame]))
    id_of = lambda o: o.Attributes.GetUserString('minivivisection.markdown.id')

    a_ids = mk.create(doc, SHORT, mk.frame())
    # The copy, 200 units away (as Copy or Ctrl+C/V: the same attributes, the same id)
    move = Rhino.Geometry.Transform.Translation(200, 0, 0)
    b_ids = [doc.Objects.Transform(i, move, False) for i in a_ids]
    yield 1
    lt.check('the copy shares the id', id_of(doc.Objects.FindId(b_ids[0])), id_of(doc.Objects.FindId(a_ids[0])))

    # A made longer: parts B doesn't have. A sees the id shared and gets one of its own.
    found = find(doc.Objects.FindId(a_ids[0]))
    lt.check('A found with its own parts only', sorted(str(o.Id) for o in found.Parts), sorted(str(i) for i in a_ids))
    lt.check('A knows its id is shared', found.Shared, True)
    a_ids = update(found, LONGER)
    yield 1
    lt.check('A longer than B', len(a_ids) > len(b_ids), True)
    lt.check('A has an id of its own now', id_of(doc.Objects.FindId(a_ids[0])) != id_of(doc.Objects.FindId(b_ids[0])), True)
    b_left = [i for i in b_ids if doc.Objects.FindId(i) is not None and not doc.Objects.FindId(i).IsDeleted]
    lt.check('B keeps every part', len(b_left), len(b_ids))

    # B edited: its family is its own parts only, none of A's
    found = find(doc.Objects.FindId(b_ids[0]))
    lt.check('B found with its own parts only', sorted(str(o.Id) for o in found.Parts), sorted(str(i) for i in b_ids))
    b_ids2 = update(found, SHORT.replace('paragraph', 'sentence'))
    yield 1
    a_left = [i for i in a_ids if doc.Objects.FindId(i) is not None and not doc.Objects.FindId(i).IsDeleted]
    lt.check('A keeps every part', len(a_left), len(a_ids))
    lt.check('B updated in place', sorted(str(i) for i in b_ids2), sorted(str(i) for i in b_ids))
    lt.check('A still finds all of its parts', len(list(find(doc.Objects.FindId(a_ids[0])).Parts)), len(a_ids))

    # A shrunk while all of its parts are locked: the leftovers go all the same (the kept
    # parts are replaced in place, locked as they are)
    for i in a_ids:
        doc.Objects.Lock(i, True)
    found = find(doc.Objects.FindId(a_ids[0]))
    lt.check('A found with its parts locked', len(list(found.Parts)), len(a_ids))
    a_ids3 = update(found, SHORT)
    yield 1
    lt.check('A shrunk: fewer parts', len(a_ids3) < len(a_ids), True)
    survivors = [i for i in a_ids if doc.Objects.FindId(i) is not None and not doc.Objects.FindId(i).IsDeleted]
    lt.check('the locked leftover parts are gone, the kept ones replaced in place', sorted(str(i) for i in survivors), sorted(str(i) for i in a_ids3))
    stale = [o for o in doc.Objects if id_of(o) == id_of(doc.Objects.FindId(a_ids3[0])) and str(o.Id) not in [str(i) for i in a_ids3]]
    lt.check('no part of A left behind', len(stale), 0)
    for i in a_ids3:
        doc.Objects.Unlock(i, True)
    doc.Objects.Delete(System.Collections.Generic.List[System.Guid](a_ids3 + b_ids2), True)


lt.run('t_markdowncopies', test)
