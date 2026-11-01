using System;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using MiniVivisection.GH;

namespace MiniVivisection
{
    /// <summary>
    /// ProtectModel: Grasshopper's V°Protect for the Rhino file (RhinoProtect). It says the state on
    /// the command line, then offers what can be done: in setup -- Protect, Store, where the originals
    /// go (OriginalsIn=Archive or File: the archive next to the file, or the file itself), the files
    /// kept (AddFiles, ClearFiles; none: the Rhino file itself), Archive (another archive file);
    /// protected -- Revert (asked first) and BackToSetup. Markdown texts show the state with
    /// {{protected}} and {{modified}}, and [Revert](protect:revert) reverts.
    /// </summary>
    public class ProtectModelCommand : Command
    {
        public override string EnglishName => "ProtectModel";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            while (true)
            {
                var settings = RhinoProtect.Read(doc);
                RhinoApp.WriteLine("ProtectModel: " + RhinoProtect.Describe(doc));
                var get = new GetOption();
                get.AcceptNothing(true);
                int protect = -1, store = -1, revert = -1, back = -1, add = -1, clear = -1, archive = -1;
                OptionToggle inItself = null;
                if (settings.Protected)
                {
                    get.SetCommandPrompt("Protected -- Revert puts the original back (Enter: done)");
                    revert = get.AddOption("Revert");
                    back = get.AddOption("BackToSetup");
                }
                else
                {
                    get.SetCommandPrompt("Setup -- Protect saves the file and keeps it as the original (Enter: done)");
                    protect = get.AddOption("Protect");
                    store = get.AddOption("Store");
                    inItself = new OptionToggle(settings.InItself, "Archive", "File");
                    get.AddOptionToggle("OriginalsIn", ref inItself);
                    add = get.AddOption("AddFiles");
                    if (settings.Files.Count > 0)
                        clear = get.AddOption("ClearFiles");
                    if (!settings.InItself)
                        archive = get.AddOption("Archive");
                }
                var result = get.Get();
                if (result == GetResult.Nothing)
                    return Result.Success;
                if (result != GetResult.Option)
                    return Result.Cancel;
                var index = get.OptionIndex();
                string problem = null;
                if (index == protect)
                {
                    problem = RhinoProtect.Protect(doc);
                    if (problem == null)
                        RhinoApp.WriteLine("ProtectModel: saved and kept as the original (" + RhinoProtect.Where(doc, RhinoProtect.Read(doc)) + ").");
                }
                else if (index == store)
                {
                    foreach (var line in RhinoProtect.Store(doc))
                        RhinoApp.WriteLine("ProtectModel: " + line);
                }
                else if (inItself != null && get.Option()?.EnglishName == "OriginalsIn")
                {
                    var value = inItself.CurrentValue;
                    RhinoProtect.Change(doc, s => s.InItself = value);
                }
                else if (index == add)
                {
                    foreach (var file in AskFiles(doc, mode))
                    {
                        FileArchive.Pick(file); // added in this session: stored wherever it is
                        RhinoProtect.Change(doc, s =>
                        {
                            if (!s.Files.Any(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
                                s.Files.Add(file);
                        });
                    }
                }
                else if (index == clear)
                    RhinoProtect.Change(doc, s => s.Files.Clear());
                else if (index == archive)
                {
                    var path = AskArchive(doc, mode);
                    if (!string.IsNullOrEmpty(path))
                        FileArchive.Pick(path); // chosen in this session: used also on another computer
                    if (path != null)
                        RhinoProtect.Change(doc, s => s.Archive = path.Length == 0 ? null : path);
                }
                else if (index == revert)
                {
                    // Reverted once the command has ended: it closes this model and opens the file
                    // again (_-New, _-Open) -- only a script-runner command may run commands, and the
                    // model would go under this one
                    problem = RhinoProtect.RevertProblem(doc);
                    if (problem == null)
                    {
                        var serial = doc.RuntimeSerialNumber;
                        var ask = mode == RunMode.Interactive;
                        HelpCommands.Later(() =>
                        {
                            var model = RhinoDoc.FromRuntimeSerialNumber(serial);
                            var said = model == null ? "Not reverted: the model was closed." : RhinoProtect.Revert(model, ask);
                            if (!string.IsNullOrEmpty(said))
                                RhinoApp.WriteLine("ProtectModel: " + said);
                        });
                        return Result.Success;
                    }
                }
                else if (index == back)
                {
                    RhinoProtect.Unprotect(doc);
                    RhinoApp.WriteLine("ProtectModel: back to setup -- the originals are kept as they are.");
                }
                if (!string.IsNullOrEmpty(problem))
                    RhinoApp.WriteLine("ProtectModel: " + problem);
            }
        }

        // Files to keep besides (or instead of) the Rhino file: a dialog, or typed (scripted)
        private static string[] AskFiles(RhinoDoc doc, RunMode mode)
        {
            if (mode == RunMode.Interactive)
            {
                var dialog = new Eto.Forms.OpenFileDialog { MultiSelect = true, Title = "ProtectModel: files to keep" };
                if (!string.IsNullOrEmpty(doc.Path))
                    dialog.Directory = new Uri(Path.GetDirectoryName(doc.Path) + Path.DirectorySeparatorChar);
                return dialog.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) == Eto.Forms.DialogResult.Ok ? dialog.Filenames.Select(Path.GetFullPath).ToArray() : new string[0];
            }
            var typed = "";
            return RhinoGet.GetString("File to keep", false, ref typed) == Result.Success && typed.Trim().Length > 0 ? new[] { typed.Trim().Trim('"') } : new string[0];
        }

        // Another archive ("" : next to the file, the default)
        private static string AskArchive(RhinoDoc doc, RunMode mode)
        {
            if (mode == RunMode.Interactive)
            {
                var dialog = new Eto.Forms.SaveFileDialog { Title = "ProtectModel: the archive (an archive that exists is added to)" };
                dialog.Filters.Add(new Eto.Forms.FileFilter("Zip archive", ".zip"));
                var current = RhinoProtect.Archive(doc, RhinoProtect.Read(doc));
                if (current != null)
                    dialog.FileName = current;
                return dialog.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) == Eto.Forms.DialogResult.Ok ? Path.GetFullPath(dialog.FileName) : null;
            }
            var typed = "";
            return RhinoGet.GetString("Archive (Enter: next to the file)", true, ref typed) == Result.Cancel ? null : typed.Trim().Trim('"');
        }
    }
}
