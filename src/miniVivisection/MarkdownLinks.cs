using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MiniVivisection
{
    /// <summary>
    /// Links in Markdown -- a Grasshopper Panel's, a Markdown text's in Rhino, V Text's -- and
    /// where they go:
    ///   gh:Name        a Grasshopper object (its nickname, name or instance id): the canvas
    ///                  centered on it, a red circle marking it for a moment;
    ///   ghview:Name    a named view of the Grasshopper canvas, restored;
    ///   rhino:Name     a Rhino object (its name, or id): zoomed to, marked the same way;
    ///   rhinoview:Name a named view of Rhino's, restored in the active viewport -- or a
    ///                  viewport by its title (Top, Perspective), made the active one;
    ///   script:file    a script (.py, .cs) or a Markdown file opened in the Script Editor (not run);
    ///   protect:revert the protected file put back to its original (Rhino: ProtectModel; a Panel's
    ///                  definition: its V°Protect -- ScriptPanels follows that one itself);
    ///   http(s):, mailto:, file: in the browser or the app for it.
    /// A file before the name -- gh:grasshopper/gh11_markdown_notes.gh#Ring,
    /// rhino:../00_overview.3dm#Circle -- is opened first when it isn't open (Grasshopper
    /// started for it; asked first whether to save a changed model it replaces); a relative path is from the
    /// file holding the link (the .3dm, the .gh). A file alone just opens it. So Rhino's
    /// Markdown links into Grasshopper and Grasshopper's into Rhino. A name with spaces:
    /// &lt;gh:My Twist&gt; or gh:My%20Twist. Shared: the .rhp and the .gha (Grasshopper's
    /// targets in the .gha: GhLinks; the .rhp reaches it by reflection, loading Grasshopper
    /// when a link names a definition).
    /// </summary>
    internal static class MarkdownLinks
    {
        private static readonly Color MarkColor = Color.FromArgb(230, 30, 30);
        private const double MarkSeconds = 2.5;

        /// <summary>The .gha's follower for gh: and ghview: (scheme, target, the linking file's
        /// folder -> what went wrong, or null); set by the .gha's copy of this class.</summary>
        public static Func<string, string, string, string> Grasshopper = null;

        /// <summary>Why a link that opens a file can't be followed now (a script paused in its
        /// document, a Grasshopper component computing), or null; set by the .rhp
        /// (MarkdownTextMouse.Install). The .gha's copy asks the .rhp's.</summary>
        public static Func<string> FileLinksWait = null;

        /// <summary>The follower of protect: links (the target: "revert" -> what went wrong, or null);
        /// set by the .rhp (RhinoProtect.Install) for Rhino's files.</summary>
        public static Func<string, string> ProtectLink = null;

        private static string FileLinkWaits()
        {
            try
            {
                if (FileLinksWait != null)
                    return FileLinksWait();
                var rhp = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection")
                    ?.GetType("MiniVivisection.MarkdownLinks")?.GetField(nameof(FileLinksWait), BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Func<string>;
                return rhp?.Invoke();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The end of the hint over a link: how it's followed (setting "Markdown links:
        /// follow with" -- <paramref name="follow"/> Click, Ctrl+click or Off), or, Off, where to
        /// switch that on. The same in Rhino and in Grasshopper's Panels.</summary>
        public static string HowToFollow(string follow, bool doubleClickEdits) =>
            follow == "Click" ? " -- click" + (doubleClickEdits ? " (double-click: edit)" : "")
            : follow == "Off" ? " -- following links is off: V Settings > Markdown > \"Markdown links: follow with\""
            : " -- Ctrl+click";

        /// <summary>Follows <paramref name="url"/>, a relative file in it from
        /// <paramref name="folder"/> (the linking file's); what went wrong, or null.</summary>
        public static string Follow(string url, string folder = null)
        {
            url = Plain((url ?? "").Trim());
            if (url.Length == 0)
                return "The link has no target.";
            var colon = url.IndexOf(':');
            var scheme = colon > 0 ? url.Substring(0, colon).ToLowerInvariant() : "";
            var target = colon > 0 ? url.Substring(colon + 1) : url;
            try
            {
                target = Uri.UnescapeDataString(target).Trim();
            }
            catch (Exception)
            {
                // as written
            }
            try
            {
                var file = Split(target).File;
                if (scheme == "protect" && FileLinkWaits() is string waiting)
                    return "The link reverts the file: not while " + waiting + ". Continue or Stop it first.";
                if (file != null && (scheme == "gh" || scheme == "ghview" || scheme == "rhino" || scheme == "rhinoview"))
                {
                    if (FileKindMismatch(scheme, file) is string mismatch)
                        return "The link " + scheme + ":" + target + " " + mismatch;
                    // A file opened or replaced under a script in its solution: not now
                    if (FileLinkWaits() is string busy)
                        return "The link opens " + file + ": not while " + busy + ". Continue or Stop it first.";
                }
                switch (scheme)
                {
                    case "gh":
                    case "ghview":
                        return FollowInGrasshopper(scheme, target, folder);
                    case "rhino":
                        return RhinoObjectLink(target, folder);
                    case "rhinoview":
                        return RhinoViewLink(target, folder);
                    case "script":
                        return OpenScript(target, folder);
                    case "protect":
                        return ProtectLink != null ? ProtectLink(target) : "No protection to revert here.";
                    case "http":
                    case "https":
                    case "mailto":
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        return null;
                    case "file":
                        // Opened with its program -- which runs it when it is one: asked first, always
                        if (!Confirm("The link opens " + target + " with the program for it, as a double-click in Explorer does.", "Open it?"))
                            return "The link " + url + ": not opened.";
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        return null;
                    default:
                        return "Links go to gh:Name, ghview:Name, rhino:Name, rhinoview:Name (a file before the name: file#Name), script:file.py, protect:revert or a web address -- not \"" + url + "\".";
                }
            }
            catch (Exception ex)
            {
                return "The link \"" + url + "\": " + ex.Message;
            }
        }

        private static readonly string[] FileKinds = { ".gh", ".ghx", ".3dm" };

        /// <summary>A link to a file without a scheme -- [next](gh3_stop_a_hang.gh), [the model](../model.3dm),
        /// C:\path\file.gh, a name after # too -- as gh: (a definition) or rhino: (a model), a script
        /// ([lesson 1](01_watch_geometry.py)) as script:; other links as they are.</summary>
        internal static string Plain(string url)
        {
            var colon = url.IndexOf(':');
            if (colon > 1) // a scheme (a drive letter is one letter)
                return url;
            var file = Split(url).File ?? url;
            var extension = System.IO.Path.GetExtension(file.Split('?')[0]).ToLowerInvariant();
            return extension == ".gh" || extension == ".ghx" ? "gh:" + url : extension == ".3dm" ? "rhino:" + url : IsScript(file) ? "script:" + url : url;
        }

        private static readonly string[] ScriptKinds = { ".py", ".cs", ".md" };

        /// <summary>A file the Script Editor opens: a Python or C# script, Markdown.</summary>
        internal static bool IsScript(string file) =>
            ScriptKinds.Any(k => (file ?? "").Split('?')[0].EndsWith(k, StringComparison.OrdinalIgnoreCase));

        // A script in the Script Editor: opened, not run (an open one is shown)
        private static string OpenScript(string file, string folder)
        {
            if (!IsScript(file))
                return "The link script:" + file + " names no script (.py, .cs) or Markdown (.md) file.";
            var (path, error) = Resolve(file, folder);
            if (path == null)
                return error;
            RhinoApp.RunScript("_-ScriptEditor _Open \"" + path + "\"", false);
            return null;
        }

        /// <summary>A Grasshopper link opens a definition, a Rhino link a model -- not the other
        /// way round: what is wrong with <paramref name="file"/> for <paramref name="scheme"/>, or null.</summary>
        internal static string FileKindMismatch(string scheme, string file)
        {
            var rhino = scheme.StartsWith("rhino", StringComparison.Ordinal);
            return rhino == file.EndsWith(".3dm", StringComparison.OrdinalIgnoreCase) ? null
                : "names " + (rhino ? "a definition: that is a gh: link." : "a model: that is a rhino: link.");
        }

        /// <summary>Whether <paramref name="full"/> is near the linking file's <paramref name="folder"/>:
        /// in it or in its parent folder (the lessons link one folder up), so it is followed
        /// without asking.</summary>
        internal static bool Near(string full, string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return false;
            var home = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            var parent = System.IO.Path.GetDirectoryName(home) ?? home;
            return full.StartsWith(parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A target's file (null: none) and name: "file.gh#Name", "file.3dm", "Name".</summary>
        public static (string File, string Name) Split(string target)
        {
            target = target ?? "";
            var hash = target.IndexOf('#');
            if (hash > 0 && FileKinds.Any(k => target.Substring(0, hash).EndsWith(k, StringComparison.OrdinalIgnoreCase)))
                return (target.Substring(0, hash).Trim(), target.Substring(hash + 1).Trim());
            if (FileKinds.Any(k => target.EndsWith(k, StringComparison.OrdinalIgnoreCase)))
                return (target.Trim(), "");
            return (null, target);
        }

        /// <summary>A link's file as a full path: relative ones from <paramref name="folder"/>.
        /// Null and the reason when it can't be -- or when it may not be opened: a file that
        /// isn't near the linking one (its folder, or that folder's parent) is asked about first,
        /// as it may be anywhere (a definition runs its scripts when opened; a path on a share
        /// is contacted just to look for it).</summary>
        public static (string Path, string Error) Resolve(string file, string folder)
        {
            var expanded = System.Environment.ExpandEnvironmentVariables(file).Replace('/', System.IO.Path.DirectorySeparatorChar);
            var near = false;
            if (!System.IO.Path.IsPathRooted(expanded))
            {
                if (string.IsNullOrEmpty(folder))
                    return (null, "The link names \"" + file + "\" relative to its file -- save the file holding the link first.");
                expanded = System.IO.Path.Combine(folder, expanded);
                near = true;
            }
            var full = System.IO.Path.GetFullPath(expanded);
            near = near && Near(full, folder);
            if (!near && !Confirm("The link opens " + full + (full.StartsWith(@"\\", StringComparison.Ordinal) ? " on another computer." : ".") +
                (full.EndsWith(".3dm", StringComparison.OrdinalIgnoreCase) ? " Rhino's model is replaced by it." : IsScript(full) ? " The Script Editor opens it (it doesn't run)." : " Grasshopper opens it, and its scripts run."), "Open it?"))
                return (null, "The link's file " + full + ": not opened.");
            return System.IO.File.Exists(full) ? (full, null) : (null, "No file " + full + ".");
        }

        private static bool Confirm(string message, string title) =>
            Rhino.UI.Dialogs.ShowMessage(message, "miniVivisection -- " + title, Rhino.UI.ShowMessageButton.YesNo, Rhino.UI.ShowMessageIcon.Question) == Rhino.UI.ShowMessageResult.Yes;

        private static string FollowInGrasshopper(string scheme, string target, string folder)
        {
            if (Grasshopper != null)
                return Grasshopper(scheme, target, folder);
            MethodInfo Follower() => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection.GH")
                ?.GetType("MiniVivisection.GH.GhLinks")?.GetMethod("Follow", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var follow = Follower();
            if (follow == null && Split(target).File != null)
            {
                // A definition named: Grasshopper started for it
                RhinoApp.RunScript("_Grasshopper", false);
                follow = Follower();
            }
            if (follow == null)
                return "Open Grasshopper with the definition first: the link goes to " + scheme + ":" + target + ".";
            return follow.Invoke(null, new object[] { scheme, target, folder }) as string;
        }

        // --- Rhino ----------------------------------------------------------------------------------

        // The link's file the active document -- opened when it isn't (asked first whether to save the one it replaces)
        private static string OpenRhinoFile(string file, string folder)
        {
            var (path, error) = Resolve(file, folder);
            if (path == null)
                return error;
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null && !string.IsNullOrEmpty(doc.Path) && string.Equals(System.IO.Path.GetFullPath(doc.Path), path, StringComparison.OrdinalIgnoreCase))
                return null;
            if (doc != null && doc.Modified && SaveFirst(doc, path) is string unsaved)
                return unsaved;
            RhinoApp.RunScript("_-Open \"" + path + "\"", false);
            doc = RhinoDoc.ActiveDoc;
            if (doc == null || string.IsNullOrEmpty(doc.Path) || !string.Equals(System.IO.Path.GetFullPath(doc.Path), path, StringComparison.OrdinalIgnoreCase))
                return "Rhino didn't open " + path + ".";
            return null;
        }

        // A changed model about to be replaced: saved, or its changes dropped, as the user says --
        // asked here, since the scripted -Open asks on the command line and takes the link's
        // path for the answer (the file then isn't opened). What stopped it, or null.
        internal static string SaveFirst(RhinoDoc doc, string path)
        {
            var name = string.IsNullOrEmpty(doc.Path) ? "the untitled model" : System.IO.Path.GetFileName(doc.Path);
            var answer = Rhino.UI.Dialogs.ShowMessage("The link opens " + System.IO.Path.GetFileName(path) + " in place of " + name + ", which has unsaved changes.\n\nSave the changes first?",
                "miniVivisection -- Save changes?", Rhino.UI.ShowMessageButton.YesNoCancel, Rhino.UI.ShowMessageIcon.Question);
            if (answer == Rhino.UI.ShowMessageResult.Cancel)
                return "The link's file " + path + ": not opened.";
            if (answer == Rhino.UI.ShowMessageResult.No)
            {
                doc.Modified = false; // the changes dropped: -Open doesn't ask again
                return null;
            }
            if (string.IsNullOrEmpty(doc.Path))
                RhinoApp.RunScript("_Save", false); // an untitled model: Rhino's dialog for its name
            else
                doc.Save();
            return doc.Modified ? "The link's file " + path + ": not opened (" + name + " wasn't saved)." : null;
        }

        private static string RhinoObjectLink(string target, string folder)
        {
            var (file, name) = Split(target);
            if (file != null)
            {
                var opened = OpenRhinoFile(file, folder);
                if (opened != null || name.Length == 0)
                    return opened;
            }
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return "No Rhino document.";
            List<RhinoObject> found;
            if (Guid.TryParse(name, out var id))
            {
                var o = doc.Objects.FindId(id);
                found = o == null ? new List<RhinoObject>() : new List<RhinoObject> { o };
            }
            else
            {
                found = doc.Objects.GetObjectList(new ObjectEnumeratorSettings { HiddenObjects = false, VisibleFilter = true })
                    .Where(o => string.Equals(o.Attributes.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (found.Count == 0)
                return "No Rhino object named \"" + name + "\" (rhino:" + target + ").";
            var box = BoundingBox.Empty;
            foreach (var o in found)
                box.Union(o.Geometry.GetBoundingBox(true));
            var view = doc.Views.ActiveView;
            if (view != null)
            {
                // The object about ObjectSize % of the view: a box that many times larger, fitted
                var share = ObjectSize / 100.0;
                var size = Math.Max(Math.Max(box.Diagonal.X, box.Diagonal.Y), Math.Max(box.Diagonal.Z, doc.ModelAbsoluteTolerance * 1000)); // its largest side
                var zoom = new BoundingBox(box.Center, box.Center);
                zoom.Inflate(size / share / 2);
                view.ActiveViewport.ZoomBoundingBox(zoom);
            }
            Mark(doc, box);
            return null;
        }

        // Setting "Links to Rhino objects: their size in the view (%)" (MiniVivisectionSettings, by
        // reflection: this file is in the .gha too); 10 without it
        private static int ObjectSize
        {
            get
            {
                try
                {
                    var property = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection")
                        ?.GetType("MiniVivisection.MiniVivisectionSettings")?.GetProperty("LinkObjectSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    return property?.GetValue(null) is int percent ? Math.Max(1, Math.Min(100, percent)) : 10;
                }
                catch (Exception)
                {
                    return 10;
                }
            }
        }

        /// <summary>What a link does, in a few words: for the hint over it.</summary>
        public static string Describe(string url)
        {
            url = Plain((url ?? "").Trim());
            var colon = url.IndexOf(':');
            var scheme = colon > 0 ? url.Substring(0, colon).ToLowerInvariant() : "";
            var target = colon > 0 ? url.Substring(colon + 1) : url;
            try
            {
                target = Uri.UnescapeDataString(target).Trim();
            }
            catch (Exception)
            {
                // as written
            }
            var (file, name) = Split(target);
            var where = file == null ? "" : " in " + System.IO.Path.GetFileName(file);
            switch (scheme)
            {
                case "protect":
                    return "Revert to the protected original";
                case "gh":
                    return name.Length == 0 ? "Grasshopper: open " + System.IO.Path.GetFileName(file) : "Grasshopper: go to " + name + where;
                case "ghview":
                    return name.Length == 0 ? "Grasshopper: open " + System.IO.Path.GetFileName(file) : "Grasshopper: the view " + name + where;
                case "rhino":
                    return name.Length == 0 ? "Rhino: open " + System.IO.Path.GetFileName(file) : "Rhino: zoom to " + name + where;
                case "rhinoview":
                    return name.Length == 0 ? "Rhino: open " + System.IO.Path.GetFileName(file) : "Rhino: the view " + name + where;
                case "script":
                    return "Script Editor: open " + System.IO.Path.GetFileName(target);
                case "mailto":
                    return "E-mail " + target;
                default:
                    return url;
            }
        }

        private static string RhinoViewLink(string target, string folder)
        {
            var (file, name) = Split(target);
            if (file != null)
            {
                var opened = OpenRhinoFile(file, folder);
                if (opened != null || name.Length == 0)
                    return opened;
            }
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return "No Rhino document.";
            var view = doc.Views.ActiveView;
            var index = doc.NamedViews.FindByName(name);
            if (index >= 0)
            {
                if (view == null)
                    return "No Rhino view.";
                doc.NamedViews.RestoreAnimatedConstantTime(index, view.ActiveViewport, 20, 10);
                view.Redraw();
                return null;
            }
            // A viewport by its title: the active one (maximized when the one before was)
            var titled = doc.Views.Find(name, false);
            if (titled == null)
                return "No named view or viewport \"" + name + "\" in Rhino (rhinoview:" + target + ").";
            var maximized = view != null && view.Maximized;
            doc.Views.ActiveView = titled;
            if (maximized)
                titled.Maximized = true;
            doc.Views.Redraw();
            return null;
        }

        // --- the mark: a red circle around a box, for a moment ----------------------------------

        private static MarkConduit s_conduit;
        private static Timer s_timer;

        private sealed class MarkConduit : DisplayConduit
        {
            public readonly List<(BoundingBox Box, DateTime Until)> Marks = new List<(BoundingBox, DateTime)>();

            protected override void DrawForeground(DrawEventArgs e)
            {
                var now = DateTime.UtcNow;
                lock (Marks)
                {
                    foreach (var (box, until) in Marks)
                    {
                        var left = (until - now).TotalSeconds;
                        if (left <= 0)
                            continue;
                        var viewport = e.Viewport;
                        if (!viewport.GetCameraFrame(out var frame))
                            continue;
                        var center = box.Center;
                        viewport.GetWorldToScreenScale(center, out var pixelsPerUnit);
                        if (pixelsPerUnit <= 0)
                            continue;
                        var screen = viewport.WorldToClient(center);
                        var radius = box.GetCorners().Max(c => viewport.WorldToClient(c).DistanceTo(screen)) + 14;
                        var alpha = (int)(255 * Math.Min(1, left / 0.8));
                        frame.Origin = center;
                        e.Display.DrawCircle(new Circle(frame, radius / pixelsPerUnit), Color.FromArgb(alpha, MarkColor), 4);
                    }
                }
            }
        }

        /// <summary>A red circle around <paramref name="box"/> in Rhino's views, fading out after a moment.</summary>
        public static void Mark(RhinoDoc doc, BoundingBox box)
        {
            if (!box.IsValid)
                return;
            if (s_conduit == null)
                s_conduit = new MarkConduit();
            lock (s_conduit.Marks)
            {
                s_conduit.Marks.RemoveAll(m => m.Until < DateTime.UtcNow);
                s_conduit.Marks.Add((box, DateTime.UtcNow.AddSeconds(MarkSeconds)));
            }
            s_conduit.Enabled = true;
            doc.Views.Redraw();
            s_timer?.Dispose();
            s_timer = new Timer(_ => RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                bool any;
                lock (s_conduit.Marks)
                {
                    s_conduit.Marks.RemoveAll(m => m.Until < DateTime.UtcNow);
                    any = s_conduit.Marks.Count > 0;
                }
                if (!any)
                {
                    s_conduit.Enabled = false;
                    s_timer?.Dispose();
                    s_timer = null;
                }
                RhinoDoc.ActiveDoc?.Views.Redraw();
            })), null, 100, 100);
        }

        /// <summary>For a test: whether a mark is shown now.</summary>
        public static bool Marking
        {
            get
            {
                if (s_conduit == null || !s_conduit.Enabled)
                    return false;
                lock (s_conduit.Marks)
                    return s_conduit.Marks.Any(m => m.Until > DateTime.UtcNow);
            }
        }
    }
}
