#! python 3
# Review 2026-10-27, H6: a .gh from someone else, with a V Protect in setup that names files outside
# its folder (..\outside\secret.txt) and an archive on another computer (a UNC path), stores nothing
# when it is opened. Store now (the user's word) stores only the files next to the definition --
# the one outside is said, not read -- and refuses the archive on the share. Kept in the definition
# itself, the same. Rhino's ProtectModel (Store) follows the same rules.
import os
import shutil
import sys
import tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

ROOT = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'foreign-%d' % os.getpid())
DEFS = os.path.join(ROOT, 'defs')
OUTSIDE = os.path.join(ROOT, 'outside')
SHARE = os.path.join(ROOT, 'share')  # what the UNC path below reaches: this machine's C$ share


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


def unc(path):
    """The local path as a UNC path to this machine's administrative share (\\\\localhost\\C$\\...)."""
    drive, rest = os.path.splitdrive(path)
    return '\\\\localhost\\' + drive.rstrip(':') + '$' + rest


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_DocumentIO, GH_RuntimeMessageLevel
    shutil.rmtree(ROOT, ignore_errors=True)
    os.makedirs(DEFS)
    os.makedirs(OUTSIDE)
    secret = os.path.join(OUTSIDE, 'secret.txt')
    with open(secret, 'w') as f:
        f.write('not to be copied')
    with open(os.path.join(DEFS, 'beside.txt'), 'w') as f:
        f.write('next to the definition')
    share_archive = unc(os.path.join(SHARE, 'stolen.zip'))
    lt.note('the UNC archive: %s (the share reachable: %s)' % (share_archive, os.path.isdir(unc(ROOT))))
    archive_type = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.FileArchive')
    read_zip = [m for m in archive_type.GetMethods(lt.ANY) if m.Name == 'Read' and m.GetParameters()[0].ParameterType.Name == 'Byte[]'][0]
    read_archive = [m for m in archive_type.GetMethods(lt.ANY) if m.Name == 'Read' and m.GetParameters()[0].ParameterType.Name == 'String'][0]
    names_in = lambda entries: sorted(os.path.basename(e.Path) for e in entries)

    def foreign(name, files, archive, in_itself):
        """A .gh as someone else saved it: a V Protect in setup naming these files and archive."""
        doc = GH_Document()
        protect = component('V Protect')
        protect.Attributes.Pivot = System.Drawing.PointF(300, 80)
        doc.AddObject(protect, False)
        field = lambda n: protect.GetType().GetField(n, lt.ANY)
        for f in files:
            field('_files').GetValue(protect).Add(f)
        field('_archiveSetting').SetValue(protect, archive)
        field('_inItself').SetValue(protect, in_itself)
        path = os.path.join(DEFS, name)
        doc.FilePath = path
        GH_DocumentIO(doc).SaveQuiet(path)
        return path

    def opened(path):
        Grasshopper.Instances.DocumentEditor.ScriptAccess_OpenDocument(path)
        doc = next(d for d in Grasshopper.Instances.DocumentServer if d.FilePath and os.path.normcase(d.FilePath) == os.path.normcase(path))
        return doc, next(o for o in doc.Objects if o.GetType().Name == 'ProtectComponent')

    # --- an archive next to it (the default), a file outside its folder --------------------------------
    path_a = foreign('a.gh', ['..\\outside\\secret.txt', 'beside.txt'], None, False)
    doc_a, protect_a = opened(path_a)
    yield 3
    zip_a = os.path.join(DEFS, 'a.protect.zip')
    lt.check('opened: nothing stored (no archive made)', os.path.exists(zip_a), False)
    remarks = list(protect_a.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
    lt.check('opened: the balloon says Store now stores', any('Store now' in r for r in remarks), True)
    call(protect_a, 'StoreNow')
    yield 2
    lt.check('Store now: the archive next to it', os.path.exists(zip_a), True)
    lt.check('Store now: only the file next to the definition', names_in(read_archive.Invoke(None, [zip_a])), ['beside.txt'])
    log = list(lt.member(protect_a, 'Log'))
    lt.note('log:\n  ' + '\n  '.join(log))
    lt.check('the file outside: not stored, said', any(l.startswith('not stored') and 'secret.txt' in l for l in log), True)
    warnings = list(protect_a.RuntimeMessages(GH_RuntimeMessageLevel.Warning))
    lt.check('and a warning in the balloon', any('secret.txt' in w for w in warnings), True)

    # --- an archive on another computer (UNC) -----------------------------------------------------------
    path_b = foreign('b.gh', ['beside.txt'], share_archive, False)
    doc_b, protect_b = opened(path_b)
    yield 3
    lt.check('opened: nothing written to the share', os.path.exists(SHARE), False)
    call(protect_b, 'StoreNow')
    yield 2
    lt.check('Store now: the archive on the share refused', os.path.exists(SHARE), False)
    warnings = list(protect_b.RuntimeMessages(GH_RuntimeMessageLevel.Warning))
    lt.check('and said', any('on another computer' in w for w in warnings), True)

    # --- kept in the definition itself -----------------------------------------------------------------
    path_c = foreign('c.gh', ['..\\outside\\secret.txt', 'beside.txt'], None, True)
    doc_c, protect_c = opened(path_c)
    yield 3
    lt.check('in itself, opened: nothing kept', lt.member(protect_c, '_keptZip'), None)
    call(protect_c, 'StoreNow')
    yield 2
    kept = lt.member(protect_c, '_keptZip')
    lt.check('in itself, Store now: only the file next to it', names_in(read_zip.Invoke(None, [kept])) if kept is not None else None, ['beside.txt'])
    for d in (doc_a, doc_b, doc_c):
        d.Enabled = False
        d.IsModified = False
        Grasshopper.Instances.DocumentServer.RemoveDocument(d)

    # --- Rhino: ProtectModel's Store, the same rules -----------------------------------------------------
    flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    protect = lt.plugin_type('RhinoProtect')
    rcall = lambda name, *args: protect.GetMethod(name, flags).Invoke(None, list(args))
    doc = Rhino.RhinoDoc.ActiveDoc
    doc.Modified = False
    Rhino.RhinoApp.RunScript('_-New _None', False)
    doc = Rhino.RhinoDoc.ActiveDoc
    model = os.path.join(DEFS, 'model.3dm')
    options = Rhino.FileIO.FileWriteOptions()
    options.UpdateDocumentPath = True
    doc.WriteFile(model, options)
    doc.Strings.SetString('minivivisection.protect', 'protected=0\ninitself=0\nfile=..\\outside\\secret.txt\nfile=beside.txt')
    log = list(rcall('Store', doc))
    lt.note('Rhino log:\n  ' + '\n  '.join(log))
    model_zip = os.path.join(DEFS, 'model.protect.zip')
    lt.check('Rhino: only the file next to the model stored', names_in(read_archive.Invoke(None, [model_zip])) if os.path.exists(model_zip) else None, ['beside.txt'])
    lt.check('Rhino: the file outside said', any(l.startswith('not stored') and 'secret.txt' in l for l in log), True)
    doc.Strings.SetString('minivivisection.protect', 'protected=0\ninitself=0\narchive=%s\nfile=beside.txt' % share_archive)
    log = list(rcall('Store', doc))
    lt.check('Rhino: the archive on the share refused', (os.path.exists(SHARE), any('on another computer' in l for l in log)), (False, True))
    doc.Modified = False


lt.run('t_protect_foreign_gh', test)
