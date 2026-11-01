#! python 3
# Every Grasshopper example opened as a user opens it (File > Open: Grasshopper's own opening, the
# solution run, the canvas drawn): its V Protect says "original" -- not "modified" -- until the
# user changes something. When not: who set the definition modified (its stack), and which objects
# no longer write what the file has.
import os
import sys
import glob
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino

HERE = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FOLDER = os.path.join(HERE, 'examples', 'grasshopper')


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('miniVivisection.GH') is not None)


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    import Grasshopper
    from Grasshopper.Kernel import GH_DocumentIO
    from GH_IO.Serialization import GH_LooseChunk
    protect_type = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.ProtectComponent')
    flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
    md5 = System.Security.Cryptography.MD5.Create()

    def states(doc):
        """Each object's saved state (its chunk's MD5), by instance id."""
        result = {}
        for o in doc.Objects:
            chunk = GH_LooseChunk('object')
            o.Write(chunk)
            result[str(o.InstanceGuid)] = (o.NickName or o.Name, System.BitConverter.ToString(md5.ComputeHash(chunk.Serialize_Binary())))
        return result

    stacks = []

    def on_modified(sender, e):
        if sender.IsModified:
            stacks.append(System.Environment.StackTrace)

    def on_added(sender, e):
        e.Document.ModifiedChanged += on_modified

    server = Grasshopper.Instances.DocumentServer
    server.DocumentAdded += on_added
    paths = sorted(glob.glob(os.path.join(FOLDER, '*.gh')), key=lambda p: (len(os.path.basename(p)), p))
    try:
        for path in paths:
            name = os.path.basename(path)
            io = GH_DocumentIO()
            io.Open(path)
            in_file = states(io.Document)
            del stacks[:]
            opened = Grasshopper.Instances.DocumentEditor.ScriptAccess_OpenDocument(path)
            of_path = lambda: [d for d in server if (d.FilePath or '').lower() == path.lower()]
            yield lt.wait_for(lambda: len(of_path()) > 0, 30)
            yield 6
            if not of_path():
                lt.check('%s opened' % name, opened, True)
                continue
            doc = of_path()[0]
            protect = [o for o in doc.Objects if o.GetType() == protect_type]
            disk = protect_type.GetField('DiskState', flags).GetValue(protect[0]) if protect else None
            lt.note('%s: disk %s, modified %s, undo %s' % (name, disk, doc.IsModified, list(doc.UndoServer.UndoNames)))
            if doc.IsModified or disk != 'original':
                now = states(doc)
                for key, (nick, value) in now.items():
                    if key not in in_file:
                        lt.note('   added: %s' % nick)
                    elif in_file[key][1] != value:
                        lt.note('   changed: %s' % nick)
                for key in set(in_file) - set(now):
                    lt.note('   removed: %s' % in_file[key][0])
                for stack in stacks[:2]:
                    lines = [l.strip() for l in stack.split('\n') if 'Grasshopper' in l or 'MiniVivisection' in l or 'RhinoCode' in l]
                    lt.note('   set modified by:\n        ' + '\n        '.join(lines[:14]))
            lt.check('%s opens unmodified, its original' % name, (disk, doc.IsModified), ('original', False))
            doc.IsModified = False
            server.RemoveDocument(doc)
            yield 1
    finally:
        server.DocumentAdded -= on_added


lt.run('t_ghexamples_open', test)
