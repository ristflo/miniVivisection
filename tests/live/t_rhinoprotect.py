#! python 3
# ProtectModel (RhinoProtect): a Rhino file's original kept -- in the file itself, and in an
# archive next to it --, its state ("original", "modified": the open model changed, the file on
# disk changed), Revert putting the original back and opening it again; Markdown fields
# {{protected}} and {{modified}} following the state; Back to setup.
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

FOLDER = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'protect-%d' % os.getpid())
MARKDOWN = '# State\n\nThis file is **{{protected}}** and {{modified}}.\n\n[Revert](protect:revert)\n'


def test():
    yield 2
    import make_rhino_examples as mk
    flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    protect = lt.plugin_type('RhinoProtect')
    call = lambda name, *args: protect.GetMethod(name, flags).Invoke(None, list(args))
    if not os.path.isdir(FOLDER):
        os.makedirs(FOLDER)

    def shown():
        texts = [o.Geometry.PlainText for o in Rhino.RhinoDoc.ActiveDoc.Objects if isinstance(o.Geometry, Rhino.Geometry.TextEntity)]
        return ' '.join(t for t in texts if 'This file is' in t)

    def circle():
        return next(o for o in Rhino.RhinoDoc.ActiveDoc.Objects if o.Attributes.Name == 'Circle')

    def fresh(path, in_itself):
        doc = Rhino.RhinoDoc.ActiveDoc
        doc.Modified = False
        Rhino.RhinoApp.RunScript('_-New _None', False)
        doc = Rhino.RhinoDoc.ActiveDoc
        mk.create(doc, MARKDOWN, mk.frame())
        attributes = doc.CreateDefaultAttributes()
        attributes.Name = 'Circle'
        doc.Objects.AddCircle(Rhino.Geometry.Circle(Rhino.Geometry.Point3d(40, -5, 0), 3), attributes)
        options = Rhino.FileIO.FileWriteOptions()
        options.UpdateDocumentPath = True
        doc.WriteFile(path, options)
        settings = call('Read', doc)
        settings.InItself = in_itself
        doc.Strings.SetString('minivivisection.protect', settings.ToString())
        return doc

    for in_itself in (True, False):
        how = 'in itself' if in_itself else 'archive'
        path = os.path.join(FOLDER, 'protected_%s.3dm' % ('itself' if in_itself else 'archive'))
        doc = fresh(path, in_itself)
        yield 2
        lt.check(how + ': fields before -- not protected, not tracked', shown(), 'This file is not protected and not tracked.')
        lt.check(how + ': protected', call('Protect', doc), None)
        lt.check(how + ': the original, the model unchanged', (call('Look', doc).Text, doc.Modified), ('original', False))
        lt.check(how + ': fields -- protected, not modified', shown(), 'This file is protected and not modified.')
        if in_itself:
            lt.check(how + ': the file on disk has its fingerprint', call('Fingerprint', path), doc.Strings.GetValue('minivivisection.protect.fingerprint'))
        else:
            lt.check(how + ': the archive next to it', os.path.exists(path.replace('.3dm', '.protect.zip')), True)

        # Changed in the model: modified, the fields follow (Rhino idle)
        start = circle().Geometry.GetBoundingBox(True).Center
        doc.Objects.Transform(circle().Id, Rhino.Geometry.Transform.Translation(10, 0, 0), True)
        yield 3
        lt.check(how + ': moved -- modified', call('Look', doc).Text, 'modified')
        lt.check(how + ': fields -- modified', shown(), 'This file is protected and modified.')
        lt.check(how + ': the fields\' update left the model modified (it is)', doc.Modified, True)
        # Saved: the file on disk isn't the original any more
        doc.WriteFile(path, Rhino.FileIO.FileWriteOptions())
        doc.Modified = False
        yield 2
        lt.check(how + ': saved -- the file on disk changed', (call('Look', doc).Disk, call('Look', doc).Text), ('changed', 'modified'))

        # Revert: the original written back and opened again
        lt.check(how + ': reverted', call('Revert', doc, False), None)
        yield 3
        doc = Rhino.RhinoDoc.ActiveDoc
        lt.check(how + ': the same file open again', os.path.basename(doc.Path or ''), os.path.basename(path))
        lt.check(how + ': the original again, unchanged', (call('Look', doc).Text, doc.Modified), ('original', False))
        lt.check(how + ': the circle where it was', circle().Geometry.GetBoundingBox(True).Center.DistanceTo(start) < 1e-6, True)
        lt.check(how + ': fields -- not modified', shown(), 'This file is protected and not modified.')
        lt.check(how + ': reverting the original: said', 'original already' in (call('Revert', doc, False) or ''), True)

        # Back to setup: not protected, not tracked
        call('Unprotect', doc)
        yield 2
        lt.check(how + ': back to setup -- fields', shown(), 'This file is not protected and not tracked.')

    lt.check('the link described', lt.plugin_type('MarkdownLinks').GetMethod('Describe', flags).Invoke(None, ['protect:revert']), 'Revert to the protected original')
    Rhino.RhinoDoc.ActiveDoc.Modified = False


lt.run('t_rhinoprotect', test)
