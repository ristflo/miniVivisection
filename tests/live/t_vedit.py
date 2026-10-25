#! python 3
# Batch 8 (review 2026-10-10: H7, M8, M10, M11, L27): V Edit, the Grasshopper component.
# Three lines feed a V Edit; its edit objects show in Rhino. An edit in Rhino marks the
# definition modified (undo record); the edit objects don't mark the model modified; a
# hidden edit object is still kept out of a saved .3dm, with no duplicate afterwards; a saved
# chunk with an unreadable edit still reads.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino
from Rhino.Geometry import Line, LineCurve, Point3d, Transform, Vector3d

OWNER = 'minivivisection.gh'


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None


def edit_objects(doc):
    return list(doc.Objects.FindByUserString(OWNER, '*', True) or [])


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import clr
    clr.AddReference('Grasshopper')
    clr.AddReference('GH_IO')
    import Grasshopper
    from Grasshopper.Kernel import GH_Document
    from Grasshopper.Kernel.Parameters import Param_Curve
    from Grasshopper.Kernel.Types import GH_Curve
    from Grasshopper.Kernel.Data import GH_Path
    from GH_IO.Serialization import GH_LooseChunk
    import System
    vedit_id = System.Guid('b7d2e8a4-3c1f-4e6b-a9d0-5f2c7e1b8d63')  # by its id: its name has the chosen mark (V°Edit)
    yield lt.wait_for(lambda: Grasshopper.Instances.ComponentServer.EmitObjectProxy(vedit_id) is not None, 60)
    proxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(vedit_id)
    lt.check('V Edit is loaded in Grasshopper', proxy is not None, True)
    vedit = proxy.CreateInstance()
    curves = Param_Curve()
    for i in range(3):
        curves.PersistentData.Append(GH_Curve(LineCurve(Line(Point3d(i * 10, 0, 0), Point3d(i * 10, 10, 0)))), GH_Path(0))
    doc = GH_Document()
    doc.AddObject(curves, False)
    doc.AddObject(vedit, False)
    vedit.Params.Input[0].AddSource(curves)
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    Grasshopper.Instances.ActiveCanvas.Document = doc
    rhino = Rhino.RhinoDoc.ActiveDoc
    rhino.Modified = False
    doc.Enabled = True
    doc.NewSolution(False)
    yield lt.wait_for(lambda: len(edit_objects(rhino)) == 1, 20)
    lt.check('item 0 shows in Rhino as an edit object', len(edit_objects(rhino)), 1)
    lt.check('the edit objects leave the model unmodified', rhino.Modified, False)

    doc.IsModified = False
    obj = edit_objects(rhino)[0]
    rhino.Objects.Transform(obj.Id, Transform.Translation(Vector3d(0, 0, 5)), True)  # the user's edit
    yield lt.wait_for(lambda: lt.member(vedit, '_edits').Count == 1, 10)
    lt.check('the edit is taken', lt.member(vedit, '_edits').Count, 1)
    lt.check('the definition counts as changed (save prompt)', doc.IsModified, True)
    lt.check('Grasshopper can undo it', doc.UndoServer.UndoCount > 0, True)
    yield 2

    # Hidden, then the model saved: the edit object must not be in the file.
    obj = edit_objects(rhino)[0]
    rhino.Objects.Hide(obj.Id, True)
    path = os.path.join(lt.RESULT_DIR, 'vedit_saved.3dm')
    if os.path.exists(path):
        os.remove(path)
    Rhino.RhinoApp.RunScript('_-SaveAs "%s" _Enter' % path, False)
    yield lt.wait_for(lambda: os.path.exists(path), 20)
    yield 2
    saved = Rhino.FileIO.File3dm.Read(path)
    in_file = [o for o in saved.Objects if o.Attributes.GetUserString(OWNER)] if saved else None
    lt.check('a hidden edit object isn\'t saved into the .3dm', in_file, [])
    yield lt.wait_for(lambda: len(edit_objects(rhino)) >= 1, 10)
    lt.check('after the save: one edit object, no duplicate', len(edit_objects(rhino)), 1)

    # A saved chunk whose edit can't be read: the component still reads.
    chunk = GH_LooseChunk('vedit')
    vedit.Write(chunk)
    chunk.RemoveItem('MV.EditGeometry', 0)
    other = proxy.CreateInstance()
    lt.check('reads with an unreadable edit', other.Read(chunk), True)
    lt.check('that edit left out', lt.member(other, '_edits').Count, 0)
    doc.Enabled = False


lt.run('t_vedit', test)
