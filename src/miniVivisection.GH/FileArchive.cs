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

        /// <summary>Check, against an archive in memory.</summary>
        public static (string State, Entry Entry) Check(byte[] zipBytes, string path)
        {
            if (zipBytes == null || zipBytes.Length == 0)
                return (null, null);
            var key = Key(path);
            Entry entry;
            using (var zip = OpenBytes(zipBytes))
                entry = ReadList(zip).FirstOrDefault(e => string.Equals(e.Path, key, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return (null, null);
            if (!File.Exists(key))
                return ("missing", entry);
            return (Md5Of(key) == entry.Md5 ? "original" : "changed", entry);
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
        /// archive doesn't have it; the archived entry.</summary>
        public static (string State, Entry Entry) Check(string archive, string path)
        {
            var key = Key(path);
            var entry = Read(archive).FirstOrDefault(e => string.Equals(e.Path, key, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return (null, null);
            if (!File.Exists(key))
                return ("missing", entry);
            return (Md5Of(key) == entry.Md5 ? "original" : "changed", entry);
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
