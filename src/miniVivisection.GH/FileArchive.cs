using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiniVivisection.GH
{
    /// <summary>
    /// V°Protect's archive: a zip file holding copies of files, each under its MD5 hash (so a
    /// file's copy is stored once, however many paths have it), and <c>files.json</c>, the list
    /// that translates: for each file its full path, name, hash, size and time. A file is known by
    /// its full path -- files of the same name in different folders are different files; a file
    /// stored again with another hash replaces its old copy (removed when no other path uses it).
    /// No Grasshopper here: the checks test it on their own.
    /// </summary>
    public static class FileArchive
    {
        public const string ListName = "files.json";

        public sealed class Entry
        {
            public string Path { get; set; }
            public string Name { get; set; }
            public string Md5 { get; set; }
            public long Size { get; set; }
            public DateTime Modified { get; set; }
            public DateTime Stored { get; set; }
        }

        /// <summary>A file's MD5, lower-case hex.</summary>
        public static string Md5Of(string path)
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(path))
                return Hex(md5.ComputeHash(stream));
        }

        public static string Md5Of(byte[] bytes)
        {
            using (var md5 = MD5.Create())
                return Hex(md5.ComputeHash(bytes));
        }

        private static string Hex(byte[] hash) => string.Concat(hash.Select(b => b.ToString("x2")));

        /// <summary>A path as the archive keeps it: full, Windows separators.</summary>
        public static string Key(string path) => System.IO.Path.GetFullPath(path.Trim().Trim('"'));

        // --- what may be stored, and where -------------------------------------------------------------

        // The files and archives the user picked in this session (a dialog, or typed): full paths
        private static readonly HashSet<string> s_picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A file or an archive the user picked (in a dialog, or typed in the command): it may be
        /// stored, or used, wherever it is -- in this session.</summary>
        public static void Pick(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            lock (s_picked)
                s_picked.Add(Key(path));
        }

        /// <summary>Whether the user picked <paramref name="path"/> in this session.</summary>
        public static bool Picked(string path)
        {
            try
            {
                lock (s_picked)
                    return s_picked.Contains(Key(path));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Whether <paramref name="path"/> is beside the file <paramref name="file"/> (a definition, a
        /// model): in its folder, or a folder in it.</summary>
        public static bool Beside(string path, string file)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(file))
                return false;
            var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file));
            return !string.IsNullOrEmpty(folder) && Key(path).StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether <paramref name="path"/> is on another computer: a UNC path (\\host\share), or a
        /// drive mapped to one. Only the path is looked at -- the computer isn't contacted.</summary>
        public static bool OnNetwork(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            path = path.Trim().Trim('"');
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
                return true;
            try
            {
                var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Why <paramref name="path"/>, a file to store, isn't stored now -- or null: stored are files
        /// the user picked in this session, and files beside <paramref name="owner"/> (the definition, the
        /// model). A file a definition or model names elsewhere (it may come from anyone) isn't read.</summary>
        public static string StoreRefusal(string path, string owner)
        {
            if (Picked(path) || Beside(path, owner) || (owner != null && string.Equals(Key(path), Key(owner), StringComparison.OrdinalIgnoreCase)))
                return null;
            return "not stored: " + Key(path) + " -- not next to the file, and not added in this session (add it again)";
        }

        /// <summary>Why the archive <paramref name="archive"/> isn't used now -- or null: one on another computer
        /// only when the user chose it in this session, or it is beside <paramref name="owner"/> (where that
        /// was opened from). Not even looked for: a path on a share is contacted just to look.</summary>
        public static string ArchiveRefusal(string archive, string owner)
        {
            if (archive == null || !OnNetwork(archive) || Picked(archive) || Beside(archive, owner))
                return null;
            return "The archive " + archive + " is on another computer: choose it again to use it.";
        }

        // --- a file's MD5, known by its size and time ------------------------------------------------------

        // Each file's MD5 as worked out last, with its size and last write time then ("": unreadable)
        private static readonly Dictionary<string, (long Size, DateTime Time, string Md5)> s_md5s =
            new Dictionary<string, (long, DateTime, string)>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> s_hashing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A file's MD5 -- worked out again only when its size or last write time changed. With
        /// <paramref name="later"/>, never on this thread: null while it is worked out on a worker, which
        /// calls <paramref name="later"/> when done; "" when it can't be read.</summary>
        private static string KnownMd5(string key, Action later)
        {
            var info = new FileInfo(key);
            var (size, time) = (info.Length, info.LastWriteTimeUtc);
            lock (s_md5s)
            {
                if (s_md5s.TryGetValue(key, out var known) && known.Size == size && known.Time == time)
                    return known.Md5;
                if (later != null && !s_hashing.Add(key))
                    return null; // a worker has it
            }
            if (later == null)
            {
                var md5 = Md5Of(key);
                lock (s_md5s)
                    s_md5s[key] = (size, time, md5);
                return md5;
            }
            System.Threading.Tasks.Task.Run(() =>
            {
                string md5;
                try
                {
                    md5 = Md5Of(key);
                }
                catch (Exception)
                {
                    md5 = "";
                }
                lock (s_md5s)
                {
                    s_hashing.Remove(key);
                    s_md5s[key] = (size, time, md5);
                }
                try
                {
                    later();
                }
                catch (Exception)
                {
                    // the caller's
                }
            });
            return null;
        }

        // A file against its archived entry: "original", "changed", "missing"; "checking" while its MD5 is
        // worked out (later), "unreadable"
        private static string StateOf(Entry entry, string key, Action later)
        {
            if (!File.Exists(key))
                return "missing";
            var md5 = KnownMd5(key, later);
            return md5 == null ? "checking" : md5.Length == 0 ? "unreadable" : md5 == entry.Md5 ? "original" : "changed";
        }

        // Each archive's list as read last, with its size and last write time then
        private static readonly Dictionary<string, (long Size, DateTime Time, List<Entry> Entries)> s_lists =
            new Dictionary<string, (long, DateTime, List<Entry>)>(StringComparer.OrdinalIgnoreCase);

        private static List<Entry> KnownList(string archive)
        {
            var info = new FileInfo(archive);
            if (!info.Exists)
                return new List<Entry>();
            lock (s_lists)
            {
                if (s_lists.TryGetValue(info.FullName, out var known) && known.Size == info.Length && known.Time == info.LastWriteTimeUtc)
                    return known.Entries;
            }
            var entries = Read(archive);
            lock (s_lists)
                s_lists[info.FullName] = (info.Length, info.LastWriteTimeUtc, entries);
            return entries;
        }

        /// <summary>The archive's list; empty when there's no archive (or no list in it).</summary>
        public static List<Entry> Read(string archive)
        {
            if (!File.Exists(archive))
                return new List<Entry>();
            using (var zip = ZipFile.OpenRead(archive))
                return ReadList(zip);
        }

        private static List<Entry> ReadList(ZipArchive zip)
        {
            var list = zip.GetEntry(ListName);
            if (list == null)
                return new List<Entry>();
            using (var reader = new StreamReader(list.Open(), Encoding.UTF8))
                return JsonSerializer.Deserialize<List<Entry>>(reader.ReadToEnd()) ?? new List<Entry>();
        }

        private static void WriteList(ZipArchive zip, List<Entry> entries)
        {
            zip.GetEntry(ListName)?.Delete();
            using (var writer = new StreamWriter(zip.CreateEntry(ListName).Open(), new UTF8Encoding(false)))
                writer.Write(JsonSerializer.Serialize(entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>Stores <paramref name="paths"/> in <paramref name="archive"/> (made when it isn't
        /// there): new files added, changed ones replaced, unchanged ones left. One log line per file,
        /// and each file's hash (null for one that couldn't be read).</summary>
        public static (List<string> Log, List<string> Hashes) Store(string archive, IList<string> paths)
        {
            var log = new List<string>();
            var hashes = new List<string>();
            var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(archive));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            var existed = File.Exists(archive);
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update)) // made when it isn't there
                StoreInto(zip, paths, existed, archive, System.IO.Path.GetFullPath(archive), log, hashes);
            return (log, hashes);
        }

        /// <summary>Store, the archive kept in memory (V°Protect's originals in the definition itself): the
        /// zip's bytes before (null: none yet) and after.</summary>
        public static (byte[] Zip, List<string> Log, List<string> Hashes) Store(byte[] zipBytes, IList<string> paths)
        {
            var log = new List<string>();
            var hashes = new List<string>();
            using (var memory = new MemoryStream())
            {
                var existed = zipBytes != null && zipBytes.Length > 0;
                if (existed)
                {
                    memory.Write(zipBytes, 0, zipBytes.Length);
                    memory.Position = 0;
                }
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
                    StoreInto(zip, paths, existed, "in the definition", null, log, hashes);
                return (memory.ToArray(), log, hashes);
            }
        }

        private static void StoreInto(ZipArchive zip, IList<string> paths, bool existed, string archive, string skip, List<string> log, List<string> hashes)
        {
            {
                var entries = existed ? ReadList(zip) : new List<Entry>();
                var changed = !existed;
                foreach (var raw in paths)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        hashes.Add(null);
                        continue;
                    }
                    string key;
                    try
                    {
                        key = Key(raw);
                    }
                    catch (Exception ex)
                    {
                        log.Add("not a path: " + raw + " (" + ex.Message + ")");
                        hashes.Add(null);
                        continue;
                    }
                    if (!File.Exists(key))
                    {
                        log.Add("missing: " + key);
                        hashes.Add(null);
                        continue;
                    }
                    if (skip != null && string.Equals(key, skip, StringComparison.OrdinalIgnoreCase))
                    {
                        log.Add("skipped: " + key + " is the archive itself");
                        hashes.Add(null);
                        continue;
                    }
                    byte[] bytes;
                    try
                    {
                        bytes = File.ReadAllBytes(key);
                    }
                    catch (Exception ex)
                    {
                        log.Add("can't read: " + key + " (" + ex.Message + ")");
                        hashes.Add(null);
                        continue;
                    }
                    var md5 = Md5Of(bytes);
                    hashes.Add(md5);
                    var entry = entries.FirstOrDefault(e => string.Equals(e.Path, key, StringComparison.OrdinalIgnoreCase));
                    if (entry != null && entry.Md5 == md5 && zip.GetEntry(md5) != null)
                    {
                        log.Add("unchanged: " + key + " (" + md5 + ")");
                        continue;
                    }
                    if (zip.GetEntry(md5) == null)
                    {
                        using (var stream = zip.CreateEntry(md5, CompressionLevel.Optimal).Open())
                            stream.Write(bytes, 0, bytes.Length);
                    }
                    var info = new FileInfo(key);
                    if (entry == null)
                    {
                        entries.Add(new Entry { Path = key, Name = info.Name, Md5 = md5, Size = bytes.Length, Modified = info.LastWriteTimeUtc, Stored = DateTime.UtcNow });
                        log.Add("added: " + key + " (" + md5 + ", " + bytes.Length + " bytes)");
                    }
                    else
                    {
                        var old = entry.Md5;
                        entry.Md5 = md5;
                        entry.Size = bytes.Length;
                        entry.Modified = info.LastWriteTimeUtc;
                        entry.Stored = DateTime.UtcNow;
                        log.Add("replaced: " + key + " (was " + old + ", now " + md5 + ")");
                        if (!entries.Any(e => e.Md5 == old))
                            zip.GetEntry(old)?.Delete();
                    }
                    changed = true;
                }
                if (changed)
                    WriteList(zip, entries);
                log.Insert(0, string.Format("archive {0}: {1} file(s) in it{2}", archive, entries.Count, existed ? "" : " (new)"));
            }
        }

        private static ZipArchive OpenBytes(byte[] zipBytes) => new ZipArchive(new MemoryStream(zipBytes ?? new byte[0], false), ZipArchiveMode.Read);

        /// <summary>Check, against an archive in memory. <paramref name="later"/>: as for the archive on disk.</summary>
        public static (string State, Entry Entry) Check(byte[] zipBytes, string path, Action later = null)
        {
            if (zipBytes == null || zipBytes.Length == 0)
                return (null, null);
            var key = Key(path);
            Entry entry;
            using (var zip = OpenBytes(zipBytes))
                entry = ReadList(zip).FirstOrDefault(e => string.Equals(e.Path, key, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return (null, null);
            var state = StateOf(entry, key, later);
            if (later == null && state == "unreadable")
                throw new IOException("can't read " + key);
            return (state, entry);
        }

        /// <summary>The files an archive in memory has.</summary>
        public static List<Entry> Read(byte[] zipBytes)
        {
            if (zipBytes == null || zipBytes.Length == 0)
                return new List<Entry>();
            using (var zip = OpenBytes(zipBytes))
                return ReadList(zip);
        }

        /// <summary>A file on disk against the archive: "original", "changed", "missing", or null when the
        /// archive doesn't have it; the archived entry. The file's MD5 is worked out again only when its size
        /// or time changed; with <paramref name="later"/> not on this thread -- "checking" meanwhile, and
        /// <paramref name="later"/> called (on a worker) once it is known ("unreadable": it can't be read).</summary>
        public static (string State, Entry Entry) Check(string archive, string path, Action later = null)
        {
            var key = Key(path);
            var entry = KnownList(archive).FirstOrDefault(e => string.Equals(e.Path, key, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return (null, null);
            var state = StateOf(entry, key, later);
            if (later == null && state == "unreadable")
                throw new IOException("can't read " + key);
            return (state, entry);
        }

        /// <summary>The archived copy of <paramref name="path"/>, written over the file on disk (or to
        /// <paramref name="to"/>); false when the archive doesn't have it.</summary>
        public static bool Restore(string archive, string path, string to = null)
        {
            var key = Key(path);
            using (var zip = ZipFile.OpenRead(archive))
            {
                var entry = ReadList(zip).FirstOrDefault(e => string.Equals(e.Path, key, StringComparison.OrdinalIgnoreCase));
                var data = entry == null ? null : zip.GetEntry(entry.Md5);
                if (data == null)
                    return false;
                using (var source = data.Open())
                using (var memory = new MemoryStream())
                {
                    source.CopyTo(memory);
                    var bytes = memory.ToArray();
                    if (Md5Of(bytes) != entry.Md5)
                        throw new InvalidDataException("the archived copy of " + key + " doesn't match its hash");
                    File.WriteAllBytes(to ?? key, bytes);
                }
            }
            return true;
        }
    }
}
