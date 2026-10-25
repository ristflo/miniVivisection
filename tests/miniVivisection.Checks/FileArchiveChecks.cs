// V°Protect's archive (miniVivisection.GH's FileArchive): files stored by hash, a list by full path;
// added, unchanged, replaced, the same name in two folders kept apart, checked, restored.
using System;
using System.IO;
using System.Linq;
using MiniVivisection.GH;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        private static void FileArchiveChecks()
        {
            var root = Path.Combine(Path.GetTempPath(), "minivivisection-checks", "archive-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var a = Path.Combine(root, "one", "notes.txt");
            var b = Path.Combine(root, "two", "notes.txt"); // the same name, another folder
            Directory.CreateDirectory(Path.GetDirectoryName(a));
            Directory.CreateDirectory(Path.GetDirectoryName(b));
            File.WriteAllText(a, "first");
            File.WriteAllText(b, "second");
            var archive = Path.Combine(root, "store", "archive.zip");
            try
            {
                var (log, hashes) = FileArchive.Store(archive, new[] { a, b, Path.Combine(root, "nothing.txt"), "" });
                Check("an archive is made", File.Exists(archive), true);
                Check("two added", log.Count(l => l.StartsWith("added")), 2);
                Check("a missing file is said", log.Any(l => l.StartsWith("missing")), true);
                Check("hashes for each (null for the missing and the empty)", string.Join(",", hashes.Select(h => h == null ? "-" : h.Length.ToString())), "32,32,-,-");
                Check("the same name in two folders: two entries", FileArchive.Read(archive).Count, 2);
                Check("the hash is the file's MD5", hashes[0], FileArchive.Md5Of(a));

                (log, _) = FileArchive.Store(archive, new[] { a, b });
                Check("stored again: unchanged", log.Count(l => l.StartsWith("unchanged")), 2);

                File.WriteAllText(a, "first, changed");
                Check("changed on disk: checked as changed", FileArchive.Check(archive, a).State, "changed");
                Check("the other one: original", FileArchive.Check(archive, b).State, "original");
                Check("a file not archived: null", FileArchive.Check(archive, archive + ".x").State, null);
                (log, _) = FileArchive.Store(archive, new[] { a });
                Check("changed: replaced", log.Count(l => l.StartsWith("replaced")), 1);
                Check("still two entries", FileArchive.Read(archive).Count, 2);
                Check("now original", FileArchive.Check(archive, a).State, "original");

                File.WriteAllText(b, "second, broken");
                Check("restore puts the archived copy back", FileArchive.Restore(archive, b) && File.ReadAllText(b) == "second", true);
                File.Delete(a);
                Check("a deleted file: missing", FileArchive.Check(archive, a).State, "missing");
                Check("and restored", FileArchive.Restore(archive, a) && File.ReadAllText(a) == "first, changed", true);
                Check("the archive itself isn't stored", FileArchive.Store(archive, new[] { archive }).Log.Any(l => l.StartsWith("skipped")), true);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (Exception)
                {
                    // the temp folder
                }
            }
        }
    }
}
