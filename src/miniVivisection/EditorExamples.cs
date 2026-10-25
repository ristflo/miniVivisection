using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Languages;
using Rhino.Runtime.Code.Storage;

namespace MiniVivisection
{
    /// <summary>
    /// The lessons (the <c>examples</c> folder next to the .rhp) in a "miniVivisection"
    /// folder of the Script Editor's Examples tab (left panel).
    ///
    /// The tab shows the editor's examples library: the RhinoCode plug-in makes the editor
    /// with <c>ExamplesLibrary = EditorSupport.GetExamplesLibrary()</c>, a public
    /// <see cref="SourceLibrary"/> in memory, its items placed by their URI below its base
    /// ("Python 3/Example 1.py"). Items added to it show at once (the library raises its
    /// change event). Each lesson is an <see cref="ExampleFile"/>: unlike McNeel's examples,
    /// which open as untitled copies, it opens a file (as a dropped file does), so lesson 09
    /// finds its helper next to it and changes can be saved -- the user's own working copy
    /// (<see cref="WorkingCopies"/>), never the shipped lesson. Options: "Lessons in the
    /// Examples tab".
    /// </summary>
    internal static class EditorExamples
    {
        public const string Folder = "miniVivisection";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly List<WeakReference<object>> s_editors = new List<WeakReference<object>>();
        private static string s_problem;

        /// <summary>For the health line (EditorHooks): null when the lessons could be added (or weren't tried), else why not.</summary>
        internal static string HookProblem => s_problem;

        /// <summary>A lesson in the Examples tab that opens its file.</summary>
        internal sealed class ExampleFile : SourceCode
        {
            public ExampleFile(string path, Uri inLibrary)
                : base(LanguageSpec.Python3, File.ReadAllText(path), inLibrary)
            {
                Path = path;
            }

            public string Path { get; }

            public override Code CreateCode()
            {
                var code = new StorageEntry(new Uri(Path), LanguageSpec.Python3).CreateCode();
                // The editor finds an open example by its Id (RCE GetCode: ActiveState.FindCode):
                // opened again, the lesson's tab is shown instead of a second copy.
                if (code != null)
                    Id = code.Id;
                return code;
            }
        }

        /// <summary>The lessons next to the plug-in: <c>NN_*.py</c>, without helper files
        /// (<c>*_tools.py</c>, also a new version of one: <c>*_tools (new).py</c>).</summary>
        internal static IEnumerable<string> Lessons(string folder) =>
            Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.py").Where(f => char.IsDigit(System.IO.Path.GetFileName(f)[0]) && System.IO.Path.GetFileName(f).IndexOf("_tools", StringComparison.OrdinalIgnoreCase) < 0)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Empty<string>();

        /// <summary>A new version of a lesson the user changed goes beside the user's copy, as
        /// <c>name (new).ext</c>.</summary>
        internal static string NewVersionPath(string target) =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target) ?? "", System.IO.Path.GetFileNameWithoutExtension(target) + " (new)" + System.IO.Path.GetExtension(target));

        /// <summary>The shipped lessons, next to the plug-in.</summary>
        internal static string ShippedFolder =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(EditorExamples).Assembly.Location) ?? "", "examples");

        /// <summary>The user's copies, which the Examples tab opens.</summary>
        internal static string WorkingFolder =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "miniVivisection", "lessons");

        private const string Manifest = ".shipped.txt";

        /// <summary>
        /// The lessons (and their helpers; the Grasshopper lessons and their script files in grasshopper\) copied from <paramref name="shipped"/> to
        /// <paramref name="working"/>, where the user may change and save them: a fix saved in a
        /// lesson never changes the shipped one. A copy is made when missing; replaced when the
        /// shipped lesson changed (a new version) and the copy is still as it was shipped;
        /// kept when the user changed it (delete it to get the shipped one back). The manifest
        /// remembers each copy's shipped text (its hash). Returns <paramref name="working"/>, or
        /// <paramref name="shipped"/> when the copies can't be made.
        /// </summary>
        internal static string WorkingCopies(string shipped, string working)
        {
            try
            {
                if (!Directory.Exists(shipped))
                    return shipped;
                Directory.CreateDirectory(working);
                var manifestPath = System.IO.Path.Combine(working, Manifest);
                var recorded = File.Exists(manifestPath)
                    ? File.ReadAllLines(manifestPath).Select(l => l.Split('\t')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var grasshopper = System.IO.Path.Combine(shipped, "grasshopper");
                // Grasshopper's lessons with their script files (GH 10 runs one, named without a folder)
                var sources = Directory.GetFiles(shipped, "*.py")
                    .Concat(Directory.GetFiles(shipped, "*.3dm")) // Rhino examples (MarkdownText)
                    .Concat(Directory.Exists(grasshopper) ? Directory.GetFiles(grasshopper, "*.gh").Concat(Directory.GetFiles(grasshopper, "*.py")) : Array.Empty<string>());
                foreach (var source in sources)
                {
                    var name = System.IO.Path.GetRelativePath(shipped, source); // "01_x.py", "grasshopper\gh1_x.gh"
                    var target = System.IO.Path.Combine(working, name);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    var shippedHash = Hash(source);
                    if (!File.Exists(target))
                    {
                        File.Copy(source, target);
                    }
                    else if (recorded.TryGetValue(name, out var was) && was != shippedHash)
                    {
                        if (Hash(target) == was)
                            File.Copy(source, target, overwrite: true); // unchanged by the user: the new version
                        else
                        {
                            // The user's own: kept. The new version beside it, so it can be found
                            // (a lesson saved with Run set back to False counts as changed too).
                            var beside = NewVersionPath(target);
                            File.Copy(source, beside, overwrite: true);
                            UserLog.Info("Lesson {0}: your changed copy is kept; its new version is beside it as {1}.", name, System.IO.Path.GetFileName(beside));
                        }
                    }
                    recorded[name] = shippedHash;
                }
                File.WriteAllLines(manifestPath, recorded.Select(p => p.Key + "\t" + p.Value));
                return working;
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Scan, "lessons: no working copies ({0}) -- the shipped files open", Log.Describe(ex));
                return shipped;
            }
        }

        private static string Hash(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(path))
                return Convert.ToHexString(sha.ComputeHash(stream));
        }

        /// <summary>The lessons in <paramref name="library"/>'s miniVivisection folder: added
        /// when <paramref name="on"/> and missing, removed when off. Returns how many it holds.</summary>
        internal static int Apply(SourceLibrary library, string folder, bool on)
        {
            var ours = library.OfType<ExampleFile>().ToList();
            if (!on)
            {
                foreach (var lesson in ours)
                    Remove(library, lesson);
                return 0;
            }
            var listed = new HashSet<string>(ours.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            // Placed as McNeel places its examples (EditorSupport.GetExamplesLibrary: new Uri(base
            // without its trailing '/', "Python 3/Example 1.py")), so the tab shows them alike.
            var root = new Uri(library.Base.ToString().TrimEnd('/'));
            foreach (var path in Lessons(folder).Where(p => !listed.Contains(p)))
                library.Add(new ExampleFile(path, new Uri(root, Folder + "/" + System.IO.Path.GetFileName(path))));
            return library.OfType<ExampleFile>().Count();
        }

        // SourceLibrary has no public Remove: Library's protected LockAndRemove (it raises the change event).
        private static void Remove(SourceLibrary library, SourceCode code)
        {
            var remove = library.GetType().GetMethod("LockAndRemove", Any, null, new[] { typeof(SourceCode) }, null)
                ?? throw new MissingMethodException("Library", "LockAndRemove");
            remove.Invoke(library, new object[] { code });
        }

        /// <summary>The editor's examples library: RCE.Browser.Examples.Library.</summary>
        private static object LibraryOf(object editor)
        {
            var browser = editor.GetType().GetProperty("Browser", Any)?.GetValue(editor);
            var examples = browser?.GetType().GetProperty("Examples", Any)?.GetValue(browser);
            return examples?.GetType().GetProperty("Library", Any)?.GetValue(examples);
        }

        /// <summary>Follows <paramref name="editor"/> (an RCE window) and adds the lessons once.</summary>
        public static void TryAdd(object editor)
        {
            lock (s_editors)
            {
                if (s_editors.Any(r => r.TryGetTarget(out var e) && ReferenceEquals(e, editor)))
                    return;
                s_editors.RemoveAll(r => !r.TryGetTarget(out _));
                s_editors.Add(new WeakReference<object>(editor));
            }
            ApplyTo(editor);
        }

        /// <summary>The setting or the V Debug switch changed: every followed editor (UI thread).</summary>
        public static void ApplyAll()
        {
            List<object> editors;
            lock (s_editors)
                editors = s_editors.Select(r => r.TryGetTarget(out var e) ? e : null).Where(e => e != null).ToList();
            foreach (var editor in editors)
                ApplyTo(editor);
        }

        private static void ApplyTo(object editor)
        {
            try
            {
                var library = LibraryOf(editor);
                if (library == null)
                    return; // the left panel isn't built yet: the next scan
                if (!(library is SourceLibrary source))
                {
                    Log.Info(LogArea.Tabs, "Examples tab: a folder of the user's own ({0}); the lessons aren't added.", library.GetType().Name);
                    return;
                }
                var count = Apply(source, WorkingCopies(ShippedFolder, WorkingFolder), MiniVivisectionSettings.ExamplesTab);
                Log.Debug(LogArea.Tabs, "Examples tab: {0} lesson(s) in '{1}'.", count, Folder);
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                if (s_problem == null)
                    UserLog.Warning("Can't add the lessons to the Examples tab: {0}", ex.Message);
                s_problem = ex.Message;
            }
        }
    }
}
