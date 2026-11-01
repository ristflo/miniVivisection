using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;

namespace MiniVivisection.GH
{
    /// <summary>
    /// V°Protect: is a definition (or another file) still the original? No inputs or outputs: its
    /// files and where their originals go are set in its menu and saved with it; its state is in its
    /// message balloon (remarks, a warning when something isn't the original) and its tooltip.
    ///
    /// Where the originals go: an archive next to the definition ("(definition).protect.zip" by
    /// default, or one chosen: FileArchive -- each copy under its MD5, a list of the files by full
    /// path), or -- "Keep the originals in the definition itself" -- in the component, saved with the
    /// definition: the other files as a zip in memory, the definition as itself (see Protect).
    ///
    /// Setup (the start): the files -- those added in the menu, or, with none, the definition's own
    /// file -- are hashed and their copies stored (added; a changed file replaced) when the user says
    /// so in this session: placed, Store now, files added, a setting changed -- never when a definition
    /// is opened or just recomputes (it may come from anyone, and name any file). Stored are only files
    /// beside the definition and files picked in this session; an archive on another computer only
    /// when chosen in this session or beside the definition (FileArchive.StoreRefusal, ArchiveRefusal).
    /// The Protect button under it saves the definition and keeps it as the original.
    ///
    /// Protect: drawn as a Grasshopper Button (ProtectAttributes) -- in front "original" or "modified",
    /// the component green or red: the definition's file on disk against its original, and the open
    /// definition's changes; then a gray button Revert. A click on it, when modified, puts the original back
    /// -- over the file on disk when that changed -- and opens the definition again from it. Its menu:
    /// back to setup. No password and no write protection: it tells, and puts back.
    ///
    /// The definition in itself: a file can't hold its own bytes. So Protect saves the definition once
    /// without the original (those bytes are the original) and once more with them; the file on disk is
    /// the original when, loaded, every object's saved state but this original is the same -- a
    /// fingerprint (MD5 over each object's state) kept with it.
    /// </summary>
    public sealed class ProtectComponent : GH_Component
    {
        private bool _protected;
        private readonly List<string> _files = new List<string>(); // as set in the menu; none: the definition
        private string _archiveSetting;    // as set in the menu; null: the default
        private bool _inItself;            // the originals in the definition itself, not an archive
        private byte[] _keptZip;           // in itself: the other files' originals (a zip)
        private byte[] _original;          // in itself: the definition's original file, without itself
        private string _fingerprint;       // in itself: the original's fingerprint (Fingerprint)
        private Timer _timer;              // protect mode: the state looked at again
        private (DateTime Time, long Size, string State) _disk; // the definition's file, as looked at last
        private List<string> _log = new List<string>();
        private bool _storeNow = true;     // the next solution stores: placed (not read: opened, pasted), Store now, a setting changed
        internal string DiskState = "?";   // "original", "changed", "missing", "not archived", "not saved", "no archive"
        internal bool OpenModified;
        private List<(string Name, string State)> _others = new List<(string, string)>(); // the other files, protect mode

        // While a fingerprint is taken: this component's original and fingerprint aren't written (they
        // aren't part of what they describe)
        [ThreadStatic] private static bool t_fingerprinting;

        public ProtectComponent()
            : base(GhNames.Of("Protect"), GhNames.Of("Protect"),
                "Keeps the originals of files -- the definition's own by default; set them in its menu -- in an archive or in the definition itself. " +
                "Protect (the button): it says whether the definition is the original, and Revert puts it back.",
                "miniVivisection", "Files")
        {
        }

        public override Guid ComponentGuid => new Guid("4f3b9c2e-7a61-4d58-9e0b-2c8d61f5a7b3");

        protected override Bitmap Icon => Icons.Protect;

        public override GH_Exposure Exposure => GH_Exposure.primary;

        internal bool IsProtected => _protected;

        internal bool InItself => _inItself;

        /// <summary>The last store's lines (setup).</summary>
        internal IReadOnlyList<string> Log => _log;

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
        }

        public override void CreateAttributes() => m_attributes = new ProtectAttributes(this);

        // --- the files and where their originals go --------------------------------------------------

        private string DefinitionPath => OnPingDocument()?.FilePath is string path && path.Length > 0 ? Path.GetFullPath(path) : null;

        /// <summary>The files kept: those set, or the definition's own; full paths (empty: an unsaved
        /// definition without files set).</summary>
        internal List<string> Files
        {
            get
            {
                if (_files.Count > 0)
                    return _files.Select(Resolve).Where(p => p != null).ToList();
                var self = DefinitionPath;
                return self == null ? new List<string>() : new List<string> { self };
            }
        }

        /// <summary>The archive: as set, or "(definition).protect.zip" next to it; null for an unsaved
        /// definition without a full path set, and when the originals are in the definition itself.</summary>
        internal string Archive
        {
            get
            {
                if (_inItself)
                    return null;
                if (!string.IsNullOrWhiteSpace(_archiveSetting))
                    return Resolve(_archiveSetting);
                var self = DefinitionPath;
                return self == null ? null : Path.Combine(Path.GetDirectoryName(self), Path.GetFileNameWithoutExtension(self) + ".protect.zip");
            }
        }

        /// <summary>Where the originals go, in words ("archive.zip", "the definition itself").</summary>
        internal string Where => _inItself ? "the definition itself" : Archive == null ? "(save the definition first)" : Archive;

        /// <summary>The originals can be kept now (a saved definition, an archive or the definition itself).</summary>
        internal bool CanKeep => DefinitionPath != null && (_inItself || Archive != null);

        /// <summary>A path as set: full, or relative to the definition (null when it is unsaved).</summary>
        private string Resolve(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0)
                return null;
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(DefinitionPath ?? "");
            return string.IsNullOrEmpty(folder) ? null : Path.GetFullPath(Path.Combine(folder, path));
        }

        private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        private List<string> OtherFiles => Files.Where(f => DefinitionPath == null || !SamePath(f, DefinitionPath)).ToList();

        // --- solving: storing (setup), the state (protect) ----------------------------------------------

        protected override void SolveInstance(IGH_DataAccess da)
        {
            if (_protected)
            {
                Message = null;
                Look();
                ShowState();
                return;
            }
            Message = "setup";
            if (!CanKeep || Files.Count == 0)
            {
                _log = new List<string>();
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, DefinitionPath == null
                    ? "Save the definition first: it is kept by default, and the archive goes next to it."
                    : "Set where the originals go: an archive, or the definition itself (the menu).");
                return;
            }
            // Stored only when the user said so -- not when the definition is opened, nor at a recompute
            var store = _storeNow;
            _storeNow = false;
            if (store)
            {
                if (!_inItself && FileArchive.ArchiveRefusal(Archive, DefinitionPath) is string refusal)
                {
                    _log = new List<string>();
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, refusal + " (Archive > Choose...)");
                    return;
                }
                try
                {
                    if (_inItself)
                    {
                        // The other files in the component (the definition itself: when Protect saves it)
                        var (others, refused) = Storable(OtherFiles);
                        var (zip, log, _) = FileArchive.Store(_keptZip, others);
                        _keptZip = OtherFiles.Count == 0 ? null : zip;
                        _log = log;
                        _log.AddRange(refused);
                        _log.Add("the definition itself: kept when you press Protect");
                    }
                    else
                    {
                        var (files, refused) = Storable(Files);
                        _log = FileArchive.Store(Archive, files).Log;
                        _log.AddRange(refused);
                    }
                }
                catch (Exception ex)
                {
                    _log = new List<string>();
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Keeping the originals (" + Where + "): " + ex.Message);
                    return;
                }
            }
            if (_log.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Nothing stored yet: Store now (its menu) stores the files' originals (" + Where + "); Protect saves the definition and keeps it.");
                return;
            }
            // The balloon: what was stored, by file name
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, _inItself
                ? "Kept in the definition itself (" + ((_keptZip?.Length ?? 0) / 1024) + " KB of other files): save the definition to keep them."
                : _log.FirstOrDefault() ?? "archive " + Archive);
            foreach (var line in _log.Skip(1).Take(12))
            {
                var problem = line.StartsWith("missing") || line.StartsWith("can't") || line.StartsWith("not a path") || line.StartsWith("not stored");
                AddRuntimeMessage(problem ? GH_RuntimeMessageLevel.Warning : GH_RuntimeMessageLevel.Remark, Short(line));
            }
            if (_log.Count > 13)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "... and " + (_log.Count - 13) + " more (the tooltip has them all).");
            if (_inItself && (_keptZip?.Length ?? 0) > 10 * 1024 * 1024)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "The definition carries " + _keptZip.Length / (1024 * 1024) + " MB of other files: an archive next to it may be better.");
            var self = DefinitionPath;
            if (!_inItself && self != null && !Files.Any(p => SamePath(p, self)))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "The definition itself is stored when you press Protect.");
        }

        /// <summary>The files that may be stored -- beside the definition, or picked in this session -- and a
        /// log line for each other one (FileArchive.StoreRefusal).</summary>
        private (List<string> Files, List<string> Refused) Storable(IEnumerable<string> files)
        {
            var self = DefinitionPath;
            var allowed = new List<string>();
            var refused = new List<string>();
            foreach (var file in files)
            {
                if (FileArchive.StoreRefusal(file, self) is string refusal)
                    refused.Add(refusal);
                else
                    allowed.Add(file);
            }
            return (allowed, refused);
        }

        /// <summary>A log line with the file's name instead of its full path: "added: notes.txt (...)".</summary>
        private static string Short(string line)
        {
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0)
                return line;
            var rest = line.Substring(colon + 2);
            var space = rest.IndexOf(" (", StringComparison.Ordinal);
            var path = space < 0 ? rest : rest.Substring(0, space);
            return line.Substring(0, colon + 2) + Path.GetFileName(path) + (space < 0 ? "" : rest.Substring(space));
        }

        /// <summary>Protect mode: the state in the balloon (a warning when not the original), redrawn.</summary>
        private void ShowState()
        {
            ClearRuntimeMessages();
            var (text, _) = ProtectAttributes.State(this);
            var level = text == "modified" ? GH_RuntimeMessageLevel.Warning : GH_RuntimeMessageLevel.Remark;
            AddRuntimeMessage(level, "The definition on disk: " + (DiskState == "original" ? "the original" : DiskState) + ".");
            AddRuntimeMessage(level, "The open definition: " + (OpenModified ? "changed, not saved" : "unchanged") + ".");
            if (!_inItself && FileArchive.ArchiveRefusal(Archive, DefinitionPath) is string refusal)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, refusal + " (back to setup: Archive > Choose...)");
            foreach (var (name, state) in _others)
                AddRuntimeMessage(state == "original" || state == "checking" ? GH_RuntimeMessageLevel.Remark : GH_RuntimeMessageLevel.Warning, name + ": " + (state == "original" ? "the original" : state) + ".");
            if (text == "modified")
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Click Revert to put the original back.");
        }

        // --- the definition's fingerprint (in itself) -----------------------------------------------------

        /// <summary>A definition file's fingerprint: MD5 over each object's saved state (by instance id),
        /// a V°Protect's original and fingerprint left out. The file is loaded for it, not opened.</summary>
        internal static string Fingerprint(string path)
        {
            var text = new StringBuilder();
            t_fingerprinting = true; // also while loading: its V°Protect mustn't start watching
            GH_Document doc = null;
            try
            {
                var io = new GH_DocumentIO();
                if (!io.Open(path) || io.Document == null)
                    throw new IOException("Grasshopper couldn't read " + Path.GetFileName(path));
                doc = io.Document;
                doc.Enabled = false;
                foreach (var obj in doc.Objects.OrderBy(o => o.InstanceGuid))
                {
                    var chunk = new GH_LooseChunk("object");
                    obj.Write(chunk);
                    text.Append(obj.InstanceGuid.ToString("N")).Append(':').Append(FileArchive.Md5Of(chunk.Serialize_Binary())).Append('\n');
                }
            }
            finally
            {
                t_fingerprinting = false;
                foreach (var protect in doc?.Objects.OfType<ProtectComponent>() ?? Enumerable.Empty<ProtectComponent>())
                    protect.StopWatching();
            }
            return FileArchive.Md5Of(Encoding.UTF8.GetBytes(text.ToString()));
        }

        // --- protect mode ------------------------------------------------------------------------

        /// <summary>The Protect button: the definition saved, kept as the original, protect mode on.</summary>
        internal void Protect()
        {
            var doc = OnPingDocument();
            if (doc == null)
                return;
            if (!CanKeep)
            {
                MessageBox.Show(Instances.DocumentEditor, "Save the definition first: protecting needs its file.", GhNames.Of("Protect"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RecordUndoEvent("Protect");
            _protected = true;
            Attributes?.ExpireLayout();
            // Its new look first: the recompute marks the definition modified -- then the original,
            // the definition as saved now, with this component protecting it
            ExpireSolution(true);
            var path = doc.FilePath;
            try
            {
                if (!_inItself && FileArchive.ArchiveRefusal(Archive, path) is string refusal)
                    throw new InvalidOperationException(refusal);
                if (_inItself)
                {
                    _keptZip = OtherFiles.Count == 0 ? null : FileArchive.Store(_keptZip, Storable(OtherFiles).Files).Zip;
                    // Saved without the original: those bytes are the original; then with it
                    _original = null;
                    _fingerprint = null;
                    if (!new GH_DocumentIO(doc).SaveQuiet(path))
                        throw new IOException("the definition couldn't be saved");
                    _original = File.ReadAllBytes(path);
                    _fingerprint = Fingerprint(path);
                    if (!new GH_DocumentIO(doc).SaveQuiet(path))
                        throw new IOException("the definition couldn't be saved");
                }
                else
                {
                    if (!new GH_DocumentIO(doc).SaveQuiet(path))
                        throw new IOException("the definition couldn't be saved");
                    FileArchive.Store(Archive, Storable(Files).Files.Concat(new[] { path }).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                }
            }
            catch (Exception ex)
            {
                _protected = false;
                Attributes?.ExpireLayout();
                MessageBox.Show(Instances.DocumentEditor, "Not protected: " + ex.Message, GhNames.Of("Protect"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            doc.IsModified = false;
            _disk = default;
            StartWatching();
            Instances.InvalidateCanvas();
        }

        /// <summary>The menu's Back to setup: storing again, the state no longer shown.</summary>
        internal void Unprotect()
        {
            RecordUndoEvent("Back to setup");
            _protected = false;
            StopWatching();
            Attributes?.ExpireLayout();
            ExpireSolution(true);
        }

        /// <summary>The state now: the definition's file on disk against its original (looked at again only
        /// when its time or size changed), the open definition's modified flag, the other files. True when
        /// anything changed (redraw).</summary>
        internal bool Look()
        {
            var doc = OnPingDocument();
            var before = (DiskState, OpenModified, string.Join("|", _others));
            OpenModified = doc?.IsModified == true;
            var path = DefinitionPath;
            var archive = Archive;
            if (path == null)
                DiskState = "not saved";
            else if (_inItself ? _fingerprint == null : archive == null || FileArchive.ArchiveRefusal(archive, path) != null || !File.Exists(archive))
                DiskState = _inItself ? "not kept" : "no archive";
            else if (!File.Exists(path))
                DiskState = "missing";
            else
            {
                var info = new FileInfo(path);
                if (_disk.State == null || _disk.Time != info.LastWriteTimeUtc || _disk.Size != info.Length)
                {
                    string state;
                    try
                    {
                        state = _inItself
                            ? Fingerprint(path) == _fingerprint ? "original" : "changed"
                            : FileArchive.Check(archive, path).State ?? "not archived";
                    }
                    catch (Exception)
                    {
                        state = "unreadable";
                    }
                    _disk = (info.LastWriteTimeUtc, info.Length, state);
                }
                DiskState = _disk.State;
            }
            // The other files: each hashed again only when its size or time changed, and not here but on a
            // worker (Checked); one on another computer that the user didn't add isn't contacted
            var others = new List<(string, string)>();
            var archived = !_inItself && path != null && DiskState != "no archive";
            foreach (var file in OtherFiles)
            {
                if (FileArchive.OnNetwork(file) && FileArchive.StoreRefusal(file, path) != null)
                {
                    others.Add((Path.GetFileName(file), "not looked at (on another computer)"));
                    continue;
                }
                try
                {
                    var state = _inItself ? FileArchive.Check(_keptZip, file, Checked).State : archived ? FileArchive.Check(archive, file, Checked).State : null;
                    others.Add((Path.GetFileName(file), state ?? "not kept"));
                }
                catch (Exception)
                {
                    others.Add((Path.GetFileName(file), "unreadable"));
                }
            }
            _others = others;
            return before != (DiskState, OpenModified, string.Join("|", _others));
        }

        // A file's hash worked out (on a worker): the state looked at again, on the UI thread
        private void Checked() => Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
        {
            if (_protected && OnPingDocument() is GH_Document doc && Instances.DocumentServer.Contains(doc) && Look())
            {
                ShowState();
                Attributes?.ExpireLayout();
                Instances.InvalidateCanvas();
            }
        }));

        internal bool NeedsRevert => _protected && (OpenModified || DiskState == "changed" || DiskState == "missing");

        /// <summary>The state the Markdown fields ({{protected}}, {{modified}}) of <paramref name="doc"/>'s
        /// Panels show: its V°Protect's (the one protecting it), the open definition's changes as they are now.</summary>
        internal static MiniVivisection.MarkdownFields.State FieldState(GH_Document doc)
        {
            var protect = doc?.Objects.OfType<ProtectComponent>().FirstOrDefault(p => p.IsProtected);
            if (protect == null)
                return MiniVivisection.MarkdownFields.State.None;
            var disk = protect.DiskState;
            var modified = disk == "changed" || disk == "missing" || (doc.IsModified && disk == "original");
            return new MiniVivisection.MarkdownFields.State { IsProtected = true, Disk = modified ? "modified" : disk == "original" ? "original" : "unknown" };
        }

        /// <summary>A link protect:revert in a Panel of <paramref name="doc"/>: its V°Protect's Revert
        /// (asked first). What went wrong, or null.</summary>
        internal static string FollowLink(GH_Document doc, string target)
        {
            if (!string.Equals((target ?? "").Trim(), "revert", StringComparison.OrdinalIgnoreCase))
                return "Links to protect: are protect:revert -- not protect:" + target + ".";
            var protect = doc?.Objects.OfType<ProtectComponent>().FirstOrDefault(p => p.IsProtected);
            if (protect == null)
                return "This definition has no " + GhNames.Of("Protect") + " protecting it.";
            if (Waits() is string waiting)
                return "The link reverts the definition: not while " + waiting + ". Continue or Stop it first.";
            protect.Look();
            if (!protect.NeedsRevert)
                return "The definition is the original already.";
            protect.Revert(ask: true);
            return null;
        }

        // Why the definition can't be opened again now -- a script paused, a component computing: the
        // definition holding them would go -- or null; what MarkdownLinks asks before a link opens a file
        private static string Waits() => MiniVivisection.MarkdownLinks.FileLinkWaits();

        internal void Revert() => Revert(ask: true);

        /// <summary>A click on Revert: asked first; the original over the file on disk when that isn't the
        /// original; the definition opened again from the file (after this click is done). Not while a
        /// script is paused or a component computes. Why it wasn't reverted (said, when asked), or null.</summary>
        /// <param name="ask">False for the tests: no question.</param>
        internal string Revert(bool ask)
        {
            var doc = OnPingDocument();
            var path = DefinitionPath;
            if (doc == null || path == null || !CanKeep)
                return "Save the definition first: protecting needs its file.";
            if (Waits() is string waiting)
            {
                var said = "Revert opens the definition again: not while " + waiting + ". Continue or Stop it first.";
                if (ask)
                    MessageBox.Show(Instances.DocumentEditor, said, GhNames.Of("Protect"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return said;
            }
            Look();
            var disk = DiskState;
            var what = (disk == "changed" || disk == "missing" ? "The file on disk isn't the original: the original is written over it. " : "") +
                       (OpenModified ? "Your changes to the open definition are dropped. " : "") +
                       "The definition is then opened again from its file.";
            if (ask && MessageBox.Show(Instances.DocumentEditor, what + "\n\nRevert to the original?", GhNames.Of("Protect"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return null;
            try
            {
                if (disk == "changed" || disk == "missing")
                {
                    if (_inItself)
                        WriteOriginal(path);
                    else if (FileArchive.ArchiveRefusal(Archive, path) is string refusal)
                        throw new InvalidOperationException(refusal);
                    else if (!FileArchive.Restore(Archive, path))
                        throw new InvalidOperationException("the archive has no copy of " + path);
                }
            }
            catch (Exception ex)
            {
                if (ask)
                    MessageBox.Show(Instances.DocumentEditor, "Not reverted: " + ex.Message, GhNames.Of("Protect"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return "Not reverted: " + ex.Message;
            }
            // Out of this component's own mouse click first: its definition goes
            Instances.DocumentEditor.BeginInvoke(new Action(() => Reopen(doc, path)));
            return null;
        }

        /// <summary>In itself: the original written to <paramref name="path"/> -- its bytes (the definition as
        /// saved without them), loaded, given them and its fingerprint again, and saved there.</summary>
        private void WriteOriginal(string path)
        {
            if (_original == null || _fingerprint == null)
                throw new InvalidOperationException("the definition holds no original");
            var temp = Path.Combine(Path.GetTempPath(), "minivivisection-protect-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            try
            {
                File.WriteAllBytes(temp, _original);
                var io = new GH_DocumentIO();
                t_fingerprinting = true; // loading: its V°Protect mustn't start watching
                bool opened;
                try
                {
                    opened = io.Open(temp) && io.Document != null;
                }
                finally
                {
                    t_fingerprinting = false;
                }
                if (!opened)
                    throw new IOException("the original couldn't be read");
                io.Document.Enabled = false;
                var twin = io.Document.Objects.OfType<ProtectComponent>().FirstOrDefault(p => p.InstanceGuid == InstanceGuid)
                           ?? throw new InvalidDataException("the original has no V°Protect of this id");
                twin.StopWatching();
                twin._original = _original;
                twin._fingerprint = _fingerprint;
                if (!new GH_DocumentIO(io.Document).SaveQuiet(path))
                    throw new IOException("the original couldn't be written");
            }
            finally
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

        internal static void Reopen(GH_Document doc, string path)
        {
            var canvas = Instances.ActiveCanvas;
            var shown = ReferenceEquals(canvas?.Document, doc);
            doc.Enabled = false;
            doc.IsModified = false; // no "save changes?": the user chose to drop them
            Instances.DocumentServer.RemoveDocument(doc);
            Instances.DocumentEditor.ScriptAccess_OpenDocument(path);
            var opened = Instances.DocumentServer.FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) && SamePath(d.FilePath, path));
            if (opened != null && shown && canvas != null)
                canvas.Document = opened;
            Rhino.RhinoApp.WriteLine(GhNames.Of("Protect") + ": " + Path.GetFileName(path) + " reverted to the original.");
        }

        private void StartWatching()
        {
            if (_timer == null)
            {
                _timer = new Timer { Interval = 1500 };
                _timer.Tick += (s, e) =>
                {
                    // Only in a definition Grasshopper has open (not one loaded for a fingerprint)
                    var doc = OnPingDocument();
                    if (doc == null || !Instances.DocumentServer.Contains(doc))
                    {
                        StopWatching();
                        return;
                    }
                    if (Look())
                    {
                        ShowState();
                        Attributes?.ExpireLayout(); // the state's words: another width
                        Instances.InvalidateCanvas();
                    }
                };
            }
            Look();
            _timer.Start();
        }

        private void StopWatching() => _timer?.Stop();

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            if (_protected && !t_fingerprinting)
                StartWatching();
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            StopWatching();
            base.RemovedFromDocument(document);
        }

        // --- the menu: the settings ---------------------------------------------------------------------

        // A setting changed in the menu: the files stored again (the user's word)
        private void Change(string what, Action change)
        {
            RecordUndoEvent(what);
            change();
            StoreNow();
        }

        /// <summary>The menu's Store now (and a setting changed): the files' originals stored at once.</summary>
        internal void StoreNow()
        {
            _storeNow = true;
            ExpireSolution(true);
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            if (_protected)
            {
                Menu_AppendItem(menu, "Revert to the original", (s, e) => Revert(), NeedsRevert);
                Menu_AppendItem(menu, "Back to setup", (s, e) => Unprotect()).ToolTipText = "Its files and where their originals go can be changed again; Store now stores them.";
                Menu_AppendItem(menu, "Originals: " + Where, null, false);
                return;
            }
            // The files: each with Remove; none set -- the definition's own
            var files = Menu_AppendItem(menu, _files.Count == 0 ? "Files: this definition" : "Files (" + _files.Count + ")");
            files.ToolTipText = _files.Count == 0 ? "None set: the definition's own file is kept." : "The files kept; each one's menu removes it.";
            foreach (var file in _files.ToList())
            {
                var item = new ToolStripMenuItem(Path.GetFileName(file)) { ToolTipText = Resolve(file) ?? file };
                item.DropDownItems.Add(new ToolStripMenuItem("Remove", null, (s, e) => Change("Remove a file", () => _files.Remove(file))));
                files.DropDownItems.Add(item);
            }
            if (_files.Count == 0)
                files.DropDownItems.Add(new ToolStripMenuItem(DefinitionPath == null ? "(the definition: not saved yet)" : Path.GetFileName(DefinitionPath)) { Enabled = false });
            files.DropDownItems.Add(new ToolStripSeparator());
            files.DropDownItems.Add(new ToolStripMenuItem("Add files...", null, (s, e) => AddFiles()));
            var self = DefinitionPath;
            if (self != null && _files.Count > 0 && !_files.Any(f => Resolve(f) != null && SamePath(Resolve(f), self)))
                files.DropDownItems.Add(new ToolStripMenuItem("Add this definition", null, (s, e) => Change("Add the definition", () => _files.Add(self))));
            if (_files.Count > 0)
                files.DropDownItems.Add(new ToolStripMenuItem("Remove all (keep this definition)", null, (s, e) => Change("Remove the files", () => _files.Clear())));

            // Where the originals go
            var inItself = Menu_AppendItem(menu, "Keep the originals in the definition itself", (s, e) => Change("Originals", () => _inItself = !_inItself), true, _inItself);
            inItself.ToolTipText = "The originals saved with the definition, in this component -- no archive next to it. The definition's file grows by the other files' size.";
            if (!_inItself)
            {
                var archive = Archive;
                var archiveItem = Menu_AppendItem(menu, "Archive: " + (archive == null ? "(save the definition first)" : Path.GetFileName(archive)));
                archiveItem.ToolTipText = archive ?? "By default next to the definition: (its name).protect.zip.";
                archiveItem.DropDownItems.Add(new ToolStripMenuItem("Choose...", null, (s, e) => ChooseArchive()));
                if (!string.IsNullOrWhiteSpace(_archiveSetting))
                    archiveItem.DropDownItems.Add(new ToolStripMenuItem("Next to the definition (the default)", null, (s, e) => Change("Archive", () => _archiveSetting = null)));
            }
            Menu_AppendItem(menu, "Store now", (s, e) => StoreNow(), CanKeep).ToolTipText = "Stores the files' originals (added, replaced, unchanged): those next to the definition, and those added in this session.";
            Menu_AppendItem(menu, "Protect", (s, e) => Protect(), CanKeep).ToolTipText = "Saves the definition, keeps it as the original, and shows from now on whether it still is.";
        }

        private void AddFiles()
        {
            using (var dialog = new OpenFileDialog { Multiselect = true, Title = GhNames.Of("Protect") + ": files to keep", InitialDirectory = Path.GetDirectoryName(DefinitionPath ?? "") ?? "" })
            {
                if (dialog.ShowDialog(Instances.DocumentEditor) != DialogResult.OK)
                    return;
                Change("Add files", () =>
                {
                    foreach (var name in dialog.FileNames)
                    {
                        FileArchive.Pick(name); // picked: stored wherever it is
                        if (!_files.Any(f => Resolve(f) != null && SamePath(Resolve(f), name)))
                            _files.Add(Path.GetFullPath(name));
                    }
                });
            }
        }

        private void ChooseArchive()
        {
            var current = Archive;
            using (var dialog = new SaveFileDialog
                   {
                       Title = GhNames.Of("Protect") + ": the archive",
                       Filter = "Zip archive (*.zip)|*.zip",
                       OverwritePrompt = false, // an archive that exists is added to
                       FileName = current == null ? "protect.zip" : Path.GetFileName(current),
                       InitialDirectory = current == null ? "" : Path.GetDirectoryName(current),
                   })
            {
                if (dialog.ShowDialog(Instances.DocumentEditor) == DialogResult.OK)
                {
                    FileArchive.Pick(dialog.FileName); // chosen: used also on another computer
                    Change("Archive", () => _archiveSetting = Path.GetFullPath(dialog.FileName));
                }
            }
        }

        /// <summary>For the tests: the menu's settings without its dialogs -- picked, as there -- and stored.</summary>
        internal void SetForTests(IEnumerable<string> files, string archive, bool inItself = false)
        {
            _files.Clear();
            _files.AddRange(files ?? Enumerable.Empty<string>());
            _archiveSetting = archive;
            _inItself = inItself;
            foreach (var file in Files)
                FileArchive.Pick(file);
            if (!string.IsNullOrWhiteSpace(archive) && Archive != null)
                FileArchive.Pick(Archive);
            StoreNow();
        }

        // --- saved with the definition -------------------------------------------------------------------

        public override bool Write(GH_IWriter writer)
        {
            writer.SetBoolean("MiniVivisectionProtected", _protected);
            writer.SetString("MiniVivisectionArchive", _archiveSetting ?? "");
            writer.SetInt32("MiniVivisectionFiles", _files.Count);
            for (var i = 0; i < _files.Count; i++)
                writer.SetString("MiniVivisectionFile", i, _files[i]);
            writer.SetBoolean("MiniVivisectionInItself", _inItself);
            if (_keptZip != null)
                writer.SetByteArray("MiniVivisectionKept", _keptZip);
            if (!t_fingerprinting && _original != null && _fingerprint != null)
            {
                writer.SetByteArray("MiniVivisectionOriginal", _original);
                writer.SetString("MiniVivisectionFingerprint", _fingerprint);
            }
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            var on = false;
            _protected = reader.TryGetBoolean("MiniVivisectionProtected", ref on) && on;
            var archive = "";
            _archiveSetting = reader.TryGetString("MiniVivisectionArchive", ref archive) && archive.Length > 0 ? archive : null;
            _files.Clear();
            var count = 0;
            if (reader.TryGetInt32("MiniVivisectionFiles", ref count))
            {
                for (var i = 0; i < count; i++)
                {
                    var file = "";
                    if (reader.TryGetString("MiniVivisectionFile", i, ref file) && file.Length > 0)
                        _files.Add(file);
                }
            }
            var inItself = false;
            _inItself = reader.TryGetBoolean("MiniVivisectionInItself", ref inItself) && inItself;
            _keptZip = reader.ItemExists("MiniVivisectionKept") ? reader.GetByteArray("MiniVivisectionKept") : null;
            _original = reader.ItemExists("MiniVivisectionOriginal") ? reader.GetByteArray("MiniVivisectionOriginal") : null;
            var fingerprint = "";
            _fingerprint = reader.TryGetString("MiniVivisectionFingerprint", ref fingerprint) && fingerprint.Length > 0 ? fingerprint : null;
            _disk = default;
            _storeNow = false; // read (opened, an undo): nothing stored until the user says so
            return base.Read(reader);
        }
    }
}
