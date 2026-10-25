using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MiniVivisection
{
    /// <summary>
    /// The help and examples commands -- also the buttons of the miniVivisection toolbar
    /// (RhinoToolbar): miniVivisectionManual opens the manual (the PDF; -miniVivisectionManual
    /// Markdown: the Markdown in the Script Editor, as V°Help), miniVivisectionExamples your copy of
    /// the examples' overview (00_overview.3dm), miniVivisectionLessons the folder of your copies.
    /// The copies are made first when they aren't there (EditorExamples).
    /// </summary>
    internal static class HelpCommands
    {
        public static string Folder => Path.GetDirectoryName(typeof(HelpCommands).Assembly.Location) ?? "";

        /// <summary>The lessons' folder -- your copies, made or brought up to date first.</summary>
        public static string Lessons() => EditorExamples.WorkingCopies(EditorExamples.ShippedFolder, EditorExamples.WorkingFolder);

        /// <summary>A file (or folder) opened with its program, as a double-click in Explorer does.</summary>
        public static bool Shell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("miniVivisection: can't open {0}: {1}", path, ex.Message);
                return false;
            }
        }

        /// <summary>A Rhino file opened once the command has ended -- a changed model asked about
        /// first (saved, its changes dropped, or Cancel: MarkdownLinks.SaveFirst): the scripted
        /// -Open asks on the command line and would take the path for the answer.</summary>
        public static void OpenLater(string path)
        {
            Later(() =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc != null && doc.Modified && MarkdownLinks.SaveFirst(doc, path) is string stopped)
                {
                    RhinoApp.WriteLine("miniVivisection: " + stopped);
                    return;
                }
                RhinoApp.RunScript("_-Open \"" + path + "\"", false);
            });
        }

        /// <summary><paramref name="action"/> once Rhino is idle (after the command).</summary>
        public static void Later(Action action)
        {
            EventHandler then = null;
            then = (s, e) =>
            {
                RhinoApp.Idle -= then;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine("miniVivisection: {0}", ex.Message);
                }
            };
            RhinoApp.Idle += then;
        }

        /// <summary><paramref name="ready"/> asked every 0.2 s (up to <paramref name="seconds"/>) until it
        /// is true -- then <paramref name="then"/>; false at the end: <paramref name="gaveUp"/>.</summary>
        public static void When(Func<bool> ready, Action then, double seconds, Action gaveUp)
        {
            var timer = new Eto.Forms.UITimer { Interval = 0.2 };
            var start = DateTime.UtcNow;
            timer.Elapsed += (s, e) =>
            {
                bool done;
                try
                {
                    done = ready();
                }
                catch (Exception)
                {
                    done = false;
                }
                if (!done && (DateTime.UtcNow - start).TotalSeconds < seconds)
                    return;
                timer.Stop();
                try
                {
                    if (done)
                        then();
                    else
                        gaveUp?.Invoke();
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine("miniVivisection: {0}", ex.Message);
                }
            };
            timer.Start();
        }

        /// <summary>A Script Editor window (the first), or null.</summary>
        public static Eto.Forms.Window ScriptEditor() =>
            Eto.Forms.Application.Instance.Windows.FirstOrDefault(w => w.GetType().FullName == "RhinoCodeEditor.Editor.RCE");
    }

    /// <summary>miniVivisectionManual: the manual -- the PDF; scripted with Markdown, the Markdown in the Script Editor.</summary>
    public class ManualCommand : Command
    {
        public override string EnglishName => "miniVivisectionManual";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var markdown = false;
            if (mode != RunMode.Interactive)
            {
                var get = new GetOption();
                get.SetCommandPrompt("The manual (Enter: the PDF)");
                get.AcceptNothing(true);
                var pdf = get.AddOption("PDF");
                var md = get.AddOption("Markdown");
                var result = get.Get();
                if (result == GetResult.Cancel)
                    return Result.Cancel;
                markdown = result == GetResult.Option && get.OptionIndex() == md;
            }
            var path = Path.Combine(HelpCommands.Folder, markdown ? HelpButton.ManualFile : Path.ChangeExtension(HelpButton.ManualFile, ".pdf"));
            if (!File.Exists(path))
            {
                RhinoApp.WriteLine("miniVivisection: the manual isn't next to the plug-in ({0}).", path);
                return Result.Failure;
            }
            if (markdown)
            {
                // As V°Help opens it (the editor's own Open: rendered Markdown) -- in a Script
                // Editor, started first when there is none
                if (HelpCommands.ScriptEditor() is Eto.Forms.Window open)
                {
                    HelpButton.Open(open);
                    return Result.Success;
                }
                HelpCommands.Later(() =>
                {
                    RhinoApp.RunScript("_ScriptEditor", false);
                    HelpCommands.When(() => HelpCommands.ScriptEditor() != null, () => HelpButton.Open(HelpCommands.ScriptEditor()), 20,
                        () => RhinoApp.WriteLine("miniVivisection: the Script Editor didn't open -- the manual: {0}", path));
                });
                return Result.Success;
            }
            return HelpCommands.Shell(path) ? Result.Success : Result.Failure;
        }
    }

    /// <summary>miniVivisectionExamples: your copy of the examples' overview, opened in Rhino.</summary>
    public class ExamplesCommand : Command
    {
        public override string EnglishName => "miniVivisectionExamples";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var path = Path.Combine(HelpCommands.Lessons(), "00_overview.3dm");
            if (!File.Exists(path))
            {
                RhinoApp.WriteLine("miniVivisection: no examples' overview ({0}).", path);
                return Result.Failure;
            }
            if (!string.IsNullOrEmpty(doc?.Path) && string.Equals(Path.GetFullPath(doc.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                RhinoApp.WriteLine("miniVivisection: the examples' overview is open already.");
                return Result.Nothing;
            }
            HelpCommands.OpenLater(path);
            return Result.Success;
        }
    }

    /// <summary>miniVivisectionGrasshopperExamples: Grasshopper started (when it isn't running) and
    /// your copy of its examples' overview opened -- or shown when it is open (the .gha's
    /// ExamplesComponent.OpenOverview).</summary>
    public class GrasshopperExamplesCommand : Command
    {
        public override string EnglishName => "miniVivisectionGrasshopperExamples";

        private static System.Reflection.MethodInfo Opener() => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection.GH")
            ?.GetType("MiniVivisection.GH.ExamplesComponent")?.GetMethod("OpenOverview", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        // Grasshopper's editor there (its window made)
        private static bool Ready() => Opener() != null && AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Grasshopper")
            ?.GetType("Grasshopper.Instances")?.GetProperty("DocumentEditor")?.GetValue(null) != null;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            HelpCommands.Lessons(); // the copies, up to date
            HelpCommands.Later(() =>
            {
                if (!Ready())
                    RhinoApp.RunScript("_Grasshopper", false);
                HelpCommands.When(Ready, () =>
                {
                    if (Opener()?.Invoke(null, null) is string error)
                        RhinoApp.WriteLine("miniVivisection: " + error);
                }, 120, () => RhinoApp.WriteLine("miniVivisection: Grasshopper didn't start (or miniVivisection.GH isn't loaded)."));
            });
            return Result.Success;
        }
    }

    /// <summary>miniVivisectionSettings: Rhino's Options dialog, at the miniVivisection page.</summary>
    [CommandStyle(Style.ScriptRunner)] // it runs _Options: only a script-runner command may run commands
    public class SettingsCommand : Command
    {
        public override string EnglishName => "miniVivisectionSettings";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            MiniVivisectionOptionsPage.Open();
            return Result.Success;
        }
    }

    /// <summary>miniVivisectionLessons: the folder of your copies of the lessons, in Explorer.</summary>
    public class LessonsCommand : Command
    {
        public override string EnglishName => "miniVivisectionLessons";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var folder = HelpCommands.Lessons();
            RhinoApp.WriteLine("miniVivisection: your copies of the lessons -- {0}", folder);
            return HelpCommands.Shell(folder) ? Result.Success : Result.Failure;
        }
    }
}
