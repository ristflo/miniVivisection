using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MiniVivisection.GH;
using Rhino;
using Rhino.FileIO;

namespace MiniVivisection
{
    /// <summary>
    /// Is a Rhino file still the original? Grasshopper's V°Protect for Rhino files (the ProtectModel
    /// command): its settings and originals in the file's document strings, no password, no write
    /// protection -- it tells, and puts back.
    ///
    /// Setup: the files -- those added, or, with none, the Rhino file itself -- are stored (Store):
    /// in an archive next to it ("(file).protect.zip" by default, or one chosen: FileArchive, each
    /// copy under its MD5), or -- "in the file itself" -- the other files as a zip in the file's
    /// strings, and the file as itself. Protect saves the file and keeps it as the original. From then
    /// on it is "original" while the file on disk is the original and the open model has no changes,
    /// "modified" when either isn't so; Revert puts the original back and opens it again.
    ///
    /// The file in itself: a file can't hold its own bytes. So Protect saves it once without the
    /// original (those bytes are the original) and once more with them; the file on disk is the
    /// original when, read, every object, table and string but this original is the same -- a
    /// fingerprint (MD5 over their serialized states) kept with it.
    ///
    /// Markdown texts show the state with the fields {{protected}} and {{modified}}, and a link
    /// protect:revert reverts (MarkdownFields): the texts with fields are laid out again when the
    /// state changes (looked at every 1.5 s while Rhino is idle).
    /// </summary>
    internal static class RhinoProtect
    {
        public const string SettingsKey = "minivivisection.protect";
        public const string OriginalKey = "minivivisection.protect.original";
        public const string FingerprintKey = "minivivisection.protect.fingerprint";
        public const string KeptKey = "minivivisection.protect.kept";

        /// <summary>A file's protection settings: on or not, where the originals go, the files.</summary>
        public sealed class Settings
        {
            public bool Protected;
            public bool InItself;
            public string Archive;                              // as set; null: next to the file
            public readonly List<string> Files = new List<string>(); // as set; none: the file itself

            public override string ToString()
            {
                var lines = new List<string> { "protected=" + (Protected ? 1 : 0), "initself=" + (InItself ? 1 : 0) };
                if (!string.IsNullOrWhiteSpace(Archive))
                    lines.Add("archive=" + Archive);
                lines.AddRange(Files.Select(f => "file=" + f));
                return string.Join("\n", lines);
            }

            public static Settings Parse(string text)
            {
                var settings = new Settings();
                foreach (var line in (text ?? "").Split('\n'))
                {
                    var equals = line.IndexOf('=');
                    if (equals <= 0)
                        continue;
                    var key = line.Substring(0, equals).Trim();
                    var value = line.Substring(equals + 1).Trim();
                    switch (key)
                    {
                        case "protected":
                            settings.Protected = value == "1";
                            break;
                        case "initself":
                            settings.InItself = value == "1";
                            break;
                        case "archive":
                            settings.Archive = value.Length > 0 ? value : null;
                            break;
                        case "file":
                            if (value.Length > 0)
                                settings.Files.Add(value);
                            break;
                    }
                }
                return settings;
            }
        }

        public static Settings Read(RhinoDoc doc) => Settings.Parse(doc?.Strings.GetValue(SettingsKey));

        private static void Write(RhinoDoc doc, Settings settings) => doc.Strings.SetString(SettingsKey, settings.ToString());

        /// <summary>Whether <paramref name="doc"/> is set up at all (the command was used on it).</summary>
        public static bool IsSetUp(RhinoDoc doc) => doc?.Strings.GetValue(SettingsKey) != null;

        // --- the files and where their originals go ------------------------------------------------

        public static string ModelPath(RhinoDoc doc) => string.IsNullOrEmpty(doc?.Path) ? null : Path.GetFullPath(doc.Path);

        private static string Resolve(RhinoDoc doc, string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0)
                return null;
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(ModelPath(doc) ?? "");
            return string.IsNullOrEmpty(folder) ? null : Path.GetFullPath(Path.Combine(folder, path));
        }

        private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>The files kept: those set, or the Rhino file itself (empty: an unsaved model without files set).</summary>
        public static List<string> Files(RhinoDoc doc, Settings settings)
        {
            if (settings.Files.Count > 0)
                return settings.Files.Select(f => Resolve(doc, f)).Where(p => p != null).ToList();
            var self = ModelPath(doc);
            return self == null ? new List<string>() : new List<string> { self };
        }

        private static List<string> OtherFiles(RhinoDoc doc, Settings settings) =>
            Files(doc, settings).Where(f => ModelPath(doc) == null || !SamePath(f, ModelPath(doc))).ToList();

        /// <summary>The archive: as set, or "(file).protect.zip" next to the file; null in the file itself, or unsaved.</summary>
        public static string Archive(RhinoDoc doc, Settings settings)
        {
            if (settings.InItself)
                return null;
            if (!string.IsNullOrWhiteSpace(settings.Archive))
                return Resolve(doc, settings.Archive);
            var self = ModelPath(doc);
            return self == null ? null : Path.Combine(Path.GetDirectoryName(self), Path.GetFileNameWithoutExtension(self) + ".protect.zip");
        }

        /// <summary>Where the originals go, in words.</summary>
        public static string Where(RhinoDoc doc, Settings settings) => settings.InItself ? "the file itself" : Archive(doc, settings) ?? "(save the file first)";

        // --- setup: storing -----------------------------------------------------------------------

        /// <summary>Changes the settings (setup): what <paramref name="change"/> does, kept in the file.</summary>
        public static void Change(RhinoDoc doc, Action<Settings> change)
        {
            var settings = Read(doc);
            change(settings);
            Write(doc, settings);
            Forget(doc);
        }

        /// <summary>The files' originals stored (setup): in the archive, or -- in the file itself -- the
        /// other files in the file's strings (the file itself: when it's protected). The log.</summary>
        public static List<string> Store(RhinoDoc doc)
        {
            var settings = Read(doc);
            if (ModelPath(doc) == null)
                return new List<string> { "Save the file first: it is kept by default, and the archive goes next to it." };
            if (settings.InItself)
            {
                var others = OtherFiles(doc, settings);
                var kept = Kept(doc);
                var (zip, log, _) = FileArchive.Store(kept, others);
                doc.Strings.SetString(KeptKey, others.Count == 0 ? "" : Convert.ToBase64String(zip));
                log.Add("the file itself: kept when it is protected");
                return log;
            }
            Write(doc, settings);
            return FileArchive.Store(Archive(doc, settings), Files(doc, settings)).Log;
        }

        private static byte[] Kept(RhinoDoc doc) => Bytes(doc.Strings.GetValue(KeptKey));

        // The original in the file's strings: gzipped, base64 ("gz:" first) -- a third of its size or less
        private static string Packed(byte[] bytes)
        {
            using var memory = new MemoryStream();
            using (var zip = new System.IO.Compression.GZipStream(memory, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                zip.Write(bytes, 0, bytes.Length);
            return "gz:" + Convert.ToBase64String(memory.ToArray());
        }

        private static byte[] Unpacked(string text)
        {
            if (text == null || !text.StartsWith("gz:", StringComparison.Ordinal))
                return Bytes(text);
            var packed = Bytes(text.Substring(3));
            if (packed == null)
                return null;
            using var zip = new System.IO.Compression.GZipStream(new MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress);
            using var memory = new MemoryStream();
            zip.CopyTo(memory);
            return memory.ToArray();
        }

        private static byte[] Bytes(string base64)
        {
            if (string.IsNullOrEmpty(base64))
                return null;
            try
            {
                return Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // --- protecting ----------------------------------------------------------------------------

        // While Protect lays the fields out: the state they show (the file as it is about to be kept)
        private static MarkdownFields.State s_forced;

        /// <summary>The file saved and kept as the original, its state shown from now on. What went wrong, or null.</summary>
        public static string Protect(RhinoDoc doc)
        {
            var path = ModelPath(doc);
            if (path == null)
                return "Save the file first: protecting needs its file.";
            var settings = Read(doc);
            settings.Protected = true;
            Write(doc, settings);
            try
            {
                // The fields as the original shows them: protected, not modified
                s_forced = new MarkdownFields.State { IsProtected = true, Disk = "original" };
                MarkdownRhinoText.RefreshFields(doc, keepModified: false);
                if (settings.InItself)
                {
                    var others = OtherFiles(doc, settings);
                    doc.Strings.SetString(KeptKey, others.Count == 0 ? "" : Convert.ToBase64String(FileArchive.Store(Kept(doc), others).Zip));
                    // Saved without the original: those bytes are the original; then with it
                    doc.Strings.Delete(OriginalKey);
                    doc.Strings.Delete(FingerprintKey);
                    Save(doc, path);
                    var original = File.ReadAllBytes(path);
                    var fingerprint = Fingerprint(path);
                    doc.Strings.SetString(OriginalKey, Packed(original));
                    doc.Strings.SetString(FingerprintKey, fingerprint);
                    Save(doc, path);
                }
                else
                {
                    Save(doc, path);
                    FileArchive.Store(Archive(doc, settings), Files(doc, settings).Concat(new[] { path }).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                }
            }
            catch (Exception ex)
            {
                settings.Protected = false;
                Write(doc, settings);
                Log.Warning(LogArea.General, "ProtectModel: not protected: {0}", Log.Describe(ex));
                return "Not protected: " + ex.Message;
            }
            finally
            {
                s_forced = null;
            }
            doc.Modified = false;
            Forget(doc);
            return null;
        }

        /// <summary>Back to setup: the state no longer shown, the originals kept as they are.</summary>
        public static void Unprotect(RhinoDoc doc)
        {
            Change(doc, s => s.Protected = false);
            doc.Strings.Delete(OriginalKey);
            doc.Strings.Delete(FingerprintKey);
            MarkdownRhinoText.RefreshFields(doc, keepModified: true);
        }

        // The document written to path as it is (Rhino's own file writing, no dialog)
        private static void Save(RhinoDoc doc, string path)
        {
            if (!doc.WriteFile(path, new FileWriteOptions { SuppressDialogBoxes = true, UpdateDocumentPath = false }))
                throw new IOException("the file couldn't be saved");
        }

        // --- the state -------------------------------------------------------------------------------

        /// <summary>The file's state: protected, its file on disk ("original", "changed", "missing",
        /// "not archived", "no archive", "not kept", "not saved", "unreadable"), the open model changed,
        /// the other files'.</summary>
        public sealed class Status
        {
            public bool Protected;
            public string Disk = "not protected";
            public bool OpenModified;
            public List<(string Name, string State)> Others = new List<(string, string)>();

            /// <summary>"original", "modified", or the disk state (not tracked).</summary>
            public string Text => Disk == "changed" || Disk == "missing" || (OpenModified && Disk == "original") ? "modified" : Disk;
        }

        // The file on disk as looked at last, per document (looked at again when its time or size changed)
        private static readonly Dictionary<uint, (string Path, DateTime Time, long Size, string State)> s_disk = new Dictionary<uint, (string, DateTime, long, string)>();

        private static void Forget(RhinoDoc doc)
        {
            lock (s_disk)
                s_disk.Remove(doc.RuntimeSerialNumber);
        }

        public static Status Look(RhinoDoc doc)
        {
            var status = new Status();
            if (doc == null)
                return status;
            var settings = Read(doc);
            status.Protected = settings.Protected;
            status.OpenModified = doc.Modified;
            if (!settings.Protected)
                return status;
            var path = ModelPath(doc);
            var archive = Archive(doc, settings);
            var fingerprint = doc.Strings.GetValue(FingerprintKey);
            if (path == null)
                status.Disk = "not saved";
            else if (settings.InItself ? string.IsNullOrEmpty(fingerprint) : archive == null || !File.Exists(archive))
                status.Disk = settings.InItself ? "not kept" : "no archive";
            else if (!File.Exists(path))
                status.Disk = "missing";
            else
            {
                var info = new FileInfo(path);
                (string Path, DateTime Time, long Size, string State) known;
                lock (s_disk)
                    s_disk.TryGetValue(doc.RuntimeSerialNumber, out known);
                if (known.State == null || known.Path != path || known.Time != info.LastWriteTimeUtc || known.Size != info.Length)
                {
                    string state;
                    try
                    {
                        state = settings.InItself
                            ? Fingerprint(path) == fingerprint ? "original" : "changed"
                            : FileArchive.Check(archive, path).State ?? "not archived";
                    }
                    catch (Exception)
                    {
                        state = "unreadable";
                    }
                    known = (path, info.LastWriteTimeUtc, info.Length, state);
                    lock (s_disk)
                        s_disk[doc.RuntimeSerialNumber] = known;
                }
                status.Disk = known.State;
            }
            foreach (var file in OtherFiles(doc, settings))
            {
                try
                {
                    var state = settings.InItself ? FileArchive.Check(Kept(doc), file).State : archive != null && File.Exists(archive) ? FileArchive.Check(archive, file).State : null;
                    status.Others.Add((Path.GetFileName(file), state ?? "not kept"));
                }
                catch (Exception)
                {
                    status.Others.Add((Path.GetFileName(file), "unreadable"));
                }
            }
            return status;
        }

        /// <summary>The state the Markdown fields show for <paramref name="doc"/>.</summary>
        public static MarkdownFields.State FieldState(RhinoDoc doc)
        {
            if (s_forced != null)
                return s_forced;
            if (doc == null || !Read(doc).Protected)
                return MarkdownFields.State.None;
            var text = Look(doc).Text;
            return new MarkdownFields.State { IsProtected = true, Disk = text == "modified" || text == "original" ? text : "unknown" };
        }

        /// <summary>The state in a line, for the command line.</summary>
        public static string Describe(RhinoDoc doc)
        {
            var settings = Read(doc);
            if (!settings.Protected)
            {
                var files = Files(doc, settings);
                return "not protected (setup) -- files: " + (settings.Files.Count == 0 ? "this file" : string.Join(", ", files.Select(Path.GetFileName))) + "; originals: " + Where(doc, settings);
            }
            var status = Look(doc);
            var line = "protected: " + status.Text + " -- the file on disk: " + (status.Disk == "original" ? "the original" : status.Disk) +
                       "; the open model: " + (status.OpenModified ? "changed, not saved" : "unchanged");
            foreach (var (name, state) in status.Others)
                line += "; " + name + ": " + (state == "original" ? "the original" : state);
            return line;
        }

        // --- the file's fingerprint (in itself) ------------------------------------------------------

        /// <summary>A Rhino file's fingerprint: MD5 over its settings, objects, tables, named views and
        /// strings -- serialized, each by its id --, the original and fingerprint left out. Read, not opened.</summary>
        public static string Fingerprint(string path)
        {
            using var file = File3dm.Read(path) ?? throw new IOException("Rhino couldn't read " + Path.GetFileName(path));
            var options = new SerializationOptions();
            var text = new StringBuilder();
            string Json(Rhino.Runtime.CommonObject value)
            {
                try
                {
                    return value == null ? "" : FileArchive.Md5Of(Encoding.UTF8.GetBytes(value.ToJSON(options) ?? ""));
                }
                catch (Exception)
                {
                    return "?";
                }
            }
            void Add(string what, Guid id, Rhino.Runtime.CommonObject value)
            {
                text.Append(what).Append(':').Append(id.ToString("N")).Append(':');
                try
                {
                    text.Append(value == null ? "" : FileArchive.Md5Of(Encoding.UTF8.GetBytes(value.ToJSON(options) ?? "")));
                }
                catch (Exception)
                {
                    text.Append('?');
                }
                text.Append('\n');
            }
            text.Append("units:").Append(file.Settings.ModelUnitSystem).Append(':').Append(file.Settings.ModelAbsoluteTolerance.ToString("R")).Append('\n');
            // Each object read once, in the table's order (its wrappers don't outlive the enumeration), then sorted by id
            var objects = new List<(Guid Id, string Geometry, string Attributes)>();
            foreach (var o in file.Objects)
            {
                var attributes = o?.Attributes;
                objects.Add((attributes?.ObjectId ?? Guid.Empty, Json(o?.Geometry), Json(attributes)));
            }
            foreach (var (id, geometry, attributes) in objects.OrderBy(o => o.Id))
                text.Append("object:").Append(id.ToString("N")).Append(':').Append(geometry).Append(':').Append(attributes).Append('\n');
            foreach (var layer in file.AllLayers.OrderBy(l => l.Id))
                Add("layer", layer.Id, layer);
            foreach (var material in file.AllMaterials.OrderBy(m => m.Id))
                Add("material", material.Id, material);
            foreach (var linetype in file.AllLinetypes.OrderBy(l => l.Id))
                Add("linetype", linetype.Id, linetype);
            foreach (var style in file.AllDimStyles.OrderBy(d => d.Id))
                Add("dimstyle", style.Id, style);
            foreach (var hatch in file.AllHatchPatterns.OrderBy(h => h.Id))
                Add("hatch", hatch.Id, hatch);
            foreach (var group in file.AllGroups.OrderBy(g => g.Id))
                Add("group", group.Id, group);
            foreach (var definition in file.AllInstanceDefinitions.OrderBy(d => d.Id))
                Add("block", definition.Id, definition);
            foreach (var view in file.AllNamedViews.OrderBy(v => v.Name, StringComparer.Ordinal))
                text.Append("view:").Append(view.Name).Append(':').Append(FileArchive.Md5Of(Encoding.UTF8.GetBytes(view.Viewport?.ToJSON(options) ?? ""))).Append('\n');
            var strings = new List<string>();
            for (var i = 0; i < file.Strings.Count; i++)
            {
                var key = file.Strings.GetKey(i);
                if (key == OriginalKey || key == FingerprintKey)
                    continue;
                strings.Add(key + "=" + FileArchive.Md5Of(Encoding.UTF8.GetBytes(file.Strings.GetValue(i) ?? "")));
            }
            foreach (var entry in strings.OrderBy(s => s, StringComparer.Ordinal))
                text.Append("string:").Append(entry).Append('\n');
            return FileArchive.Md5Of(Encoding.UTF8.GetBytes(text.ToString()));
        }

        // --- reverting ---------------------------------------------------------------------------------

        // A file reverted in itself: its original and fingerprint, put back into it once it is open again
        private static (string Path, string Original, string Fingerprint, string Kept)? s_reopening;

        /// <summary>Revert: the original over the file on disk when that isn't the original; the file
        /// opened again from it (the open model's changes dropped). <paramref name="ask"/>: asked
        /// first. What went wrong, or null; "" when the user said no.</summary>
        public static string Revert(RhinoDoc doc, bool ask)
        {
            var settings = Read(doc);
            var path = ModelPath(doc);
            if (!settings.Protected || path == null)
                return "This file isn't protected (ProtectModel protects it).";
            Forget(doc);
            var status = Look(doc);
            if (status.Text != "modified")
                return status.Text == "original" ? "This file is the original already." : "The original can't be put back: " + status.Disk + ".";
            var disk = status.Disk;
            var what = (disk == "changed" || disk == "missing" ? "The file on disk isn't the original: the original is written over it. " : "") +
                       (status.OpenModified ? "Your changes to the open model are dropped. " : "") +
                       "The file is then opened again.";
            if (ask && Rhino.UI.Dialogs.ShowMessage(what + "\n\nRevert to the original?", "miniVivisection -- Protect", Rhino.UI.ShowMessageButton.YesNo, Rhino.UI.ShowMessageIcon.Question) != Rhino.UI.ShowMessageResult.Yes)
                return "";
            // The original first to a file of its own: Rhino keeps the open model's file locked
            string temp = null;
            try
            {
                if (disk == "changed" || disk == "missing")
                {
                    temp = Path.Combine(Path.GetTempPath(), "minivivisection-revert-" + Guid.NewGuid().ToString("N") + ".3dm");
                    if (settings.InItself)
                    {
                        var original = Unpacked(doc.Strings.GetValue(OriginalKey)) ?? throw new InvalidOperationException("the file holds no original");
                        File.WriteAllBytes(temp, original);
                        // The original is the file saved without it: put back once it's open
                        s_reopening = (path, doc.Strings.GetValue(OriginalKey), doc.Strings.GetValue(FingerprintKey), doc.Strings.GetValue(KeptKey));
                    }
                    else if (!FileArchive.Restore(Archive(doc, settings), path, temp))
                        throw new InvalidOperationException("the archive has no copy of " + path);
                }
                // The model closed (its changes dropped: the user chose so), the original put in place, opened
                doc.Modified = false;
                RhinoApp.RunScript("_-New _None", false);
                if (temp != null)
                    File.Copy(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                s_reopening = null;
                return "Not reverted: " + ex.Message;
            }
            finally
            {
                if (temp != null)
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (Exception)
                    {
                        // a temp file
                    }
                }
            }
            RhinoApp.RunScript("_-Open \"" + path + "\"", false);
            var opened = RhinoDoc.ActiveDoc;
            if (opened == null || string.IsNullOrEmpty(opened.Path) || !SamePath(opened.Path, path))
            {
                s_reopening = null;
                return "Rhino didn't open " + path + " again.";
            }
            PutBack(opened);
            Forget(opened);
            RhinoApp.WriteLine("miniVivisection: " + Path.GetFileName(path) + " reverted to the original.");
            return null;
        }

        // In itself: the reopened original given its original and fingerprint again, and saved so
        private static void PutBack(RhinoDoc doc)
        {
            var reopening = s_reopening;
            s_reopening = null;
            if (reopening == null || !SamePath(reopening.Value.Path, ModelPath(doc) ?? ""))
                return;
            doc.Strings.SetString(OriginalKey, reopening.Value.Original);
            doc.Strings.SetString(FingerprintKey, reopening.Value.Fingerprint);
            if (!string.IsNullOrEmpty(reopening.Value.Kept))
                doc.Strings.SetString(KeptKey, reopening.Value.Kept);
            Save(doc, reopening.Value.Path);
            doc.Modified = false;
        }

        /// <summary>A link protect:revert (MarkdownLinks): what went wrong, or null.</summary>
        public static string FollowLink(RhinoDoc doc, string target)
        {
            if (!string.Equals((target ?? "").Trim(), "revert", StringComparison.OrdinalIgnoreCase))
                return "Links to protect: are protect:revert -- not protect:" + target + ".";
            var result = Revert(doc, ask: true);
            return result == "" ? null : result;
        }

        // --- watching: the fields follow the state -----------------------------------------------------

        private static long s_lastLook;
        private static string s_lastSignature;

        /// <summary>The state looked at every 1.5 s while Rhino is idle: Markdown texts with fields laid
        /// out again when it changed; the protect: link followed here.</summary>
        public static void Install()
        {
            MarkdownLinks.ProtectLink = target => FollowLink(RhinoDoc.ActiveDoc, target);
            RhinoDoc.EndOpenDocument += (s, e) =>
            {
                Forget(e.Document);
                s_lastSignature = null;
                MarkdownRhinoText.ForgetFields();
            };
            // Whether the model has texts with fields: known from the objects that change, not found again in all of them
            RhinoDoc.AddRhinoObject += (s, e) => MarkdownRhinoText.FieldsChanged(null, e.TheObject?.Attributes);
            RhinoDoc.DeleteRhinoObject += (s, e) => MarkdownRhinoText.FieldsChanged(e.TheObject?.Attributes, null);
            RhinoDoc.ReplaceRhinoObject += (s, e) => MarkdownRhinoText.FieldsChanged(e.OldRhinoObject?.Attributes, e.NewRhinoObject?.Attributes);
            RhinoDoc.ModifyObjectAttributes += (s, e) => MarkdownRhinoText.FieldsChanged(e.OldAttributes, e.NewAttributes);
            RhinoDoc.NewDocument += (s, e) => MarkdownRhinoText.ForgetFields();
            RhinoApp.Idle += (s, e) =>
            {
                var now = Environment.TickCount64;
                if (now - s_lastLook < 1500)
                    return;
                s_lastLook = now;
                try
                {
                    var doc = RhinoDoc.ActiveDoc;
                    if (doc == null || Rhino.Commands.Command.InCommand() || !MarkdownRhinoText.HasFields(doc))
                        return;
                    var signature = doc.RuntimeSerialNumber + "|" + FieldState(doc).Signature;
                    if (signature == s_lastSignature)
                        return;
                    s_lastSignature = signature;
                    if (MarkdownRhinoText.RefreshFields(doc, keepModified: true) > 0)
                        doc.Views.Redraw();
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.General, "Protect: the fields: {0}", Log.Describe(ex));
                }
            };
        }
    }
}
