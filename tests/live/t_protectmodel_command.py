#! python 3
# Review 2026-10-27, H5: ProtectModel > Revert run as a scripted *command* (`_-ProtectModel _Revert`),
# not called directly: the command returns, the revert follows once it has ended, and the model and
# the file on disk are the original again -- for the original in the file itself and in an archive,
# with the file on disk changed and with only the open model changed.
import os
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

FOLDER = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'protectcommand-%d' % os.getpid())
MARKDOWN = '# State\n\nThis file is **{{protected}}** and {{modified}}.\n'


def test():
    yield 2
    import make_rhino_examples as mk
    flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    protect = lt.plugin_type('RhinoProtect')
    call = lambda name, *args: protect.GetMethod(name, flags).Invoke(None, list(args))
    archive_type = lt.assembly('miniVivisection').GetType('MiniVivisection.GH.FileArchive')
    check_archive = [m for m in archive_type.GetMethods(flags) if m.Name == 'Check' and m.GetParameters()[0].ParameterType.Name == 'String'][0]
    if not os.path.isdir(FOLDER):
        os.makedirs(FOLDER)

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

    def on_disk_original(path, in_itself, doc):
        if in_itself:
            return call('Fingerprint', path) == doc.Strings.GetValue('minivivisection.protect.fingerprint')
        return check_archive.Invoke(None, [path.replace('.3dm', '.protect.zip'), path, None]).Item1 == 'original'

    for in_itself in (True, False):
        for saved in (True, False):
            how = ('in itself' if in_itself else 'archive') + (', saved' if saved else ', not saved')
            lt.progress(how)
            path = os.path.join(FOLDER, 'command_%s_%s.3dm' % ('itself' if in_itself else 'archive', 'saved' if saved else 'open'))
            doc = fresh(path, in_itself)
            yield 1
            lt.check(how + ': protected', call('Protect', doc), None)
            start = circle().Geometry.GetBoundingBox(True).Center
            doc.Objects.Transform(circle().Id, Rhino.Geometry.Transform.Translation(10, 0, 0), True)
            if saved:
                doc.WriteFile(path, Rhino.FileIO.FileWriteOptions())
                doc.Modified = False
            yield 2
            lt.check(how + ': modified', call('Look', doc).Text, 'modified')
            serial = doc.RuntimeSerialNumber

            # The command, scripted: it returns; the revert follows once it has ended
            returned = Rhino.RhinoApp.RunScript('_-ProtectModel _Revert', False)
            lt.check(how + ': the scripted command returned', returned, True)
            yield 3
            doc = Rhino.RhinoDoc.ActiveDoc
            lt.note('%s: serial %d -> %d' % (how, serial, doc.RuntimeSerialNumber))
            lt.check(how + ': the file opened again (a new model)', (os.path.basename(doc.Path or ''), doc.RuntimeSerialNumber != serial), (os.path.basename(path), True))
            lt.check(how + ': the original again, unchanged', (call('Look', doc).Text, doc.Modified), ('original', False))
            lt.check(how + ': the circle where it was', circle().Geometry.GetBoundingBox(True).Center.DistanceTo(start) < 1e-6, True)
            lt.check(how + ': the file on disk is the original', on_disk_original(path, in_itself, doc), True)

    Rhino.RhinoDoc.ActiveDoc.Modified = False


lt.run('t_protectmodel_command', test)
