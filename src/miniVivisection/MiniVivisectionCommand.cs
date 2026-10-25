using System.Diagnostics;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>Shows what the plug-in follows and draws; quick switches for the most
    /// used settings (the same as on the Options page, Tools > Options >
    /// MiniVivisection, which has them all). While paused it runs only with
    /// FreePause on -- Rhino's own Select Action prompt allows no other commands.</summary>
    [CommandStyle(Style.ScriptRunner)] // its Settings option runs _Options: only a script-runner command may run commands
    public class MiniVivisectionCommand : Command
    {
        public override string EnglishName => "miniVivisection";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = MiniVivisectionPlugin.Instance;
            plugin.Hook.Scan();
            while (true)
            {
                var draw = new OptionToggle((bool)MiniVivisectionSettings.Get("Draw"), "Off", "On");
                var freePause = new OptionToggle((bool)MiniVivisectionSettings.Get("FreePause"), "Off", "On");
                var hover = new OptionToggle((bool)MiniVivisectionSettings.Get("HoverPreview"), "Off", "On");
                var watchExpressions = new OptionToggle((bool)MiniVivisectionSettings.Get("WatchExpressions"), "Off", "On");
                var get = new GetOption();
                get.SetCommandPrompt(string.Format(
                    "miniVivisection: {0} Script Editor(s), {1} value(s) drawn, {2} object(s) in edit",
                    plugin.Hook.EditorCount, plugin.Conduit.Count, plugin.Hook.Edits.Count));
                get.AddOptionToggle("Draw", ref draw);
                get.AddOptionToggle("FreePause", ref freePause);
                get.AddOptionToggle("HoverPreview", ref hover);
                get.AddOptionToggle("WatchExpressions", ref watchExpressions);
                var bakeIndex = get.AddOption("BakeAll");
                var running = plugin.Hook.ControlsIn(DebugControlsState.Debugging);
                var breakIndex = get.AddOption("BreakNow");
                var whereIndex = get.AddOption("WhereIsEveryone");
                var paused = plugin.Hook.PausedControls();
                var stopPausedIndex = paused.Count > 0 ? get.AddOption("StopPaused") : -1;
                var stopEditingIndex = plugin.Hook.Edits.Count > 0 ? get.AddOption("StopEditing") : -1;
                var promptsIndex = get.AddOption("GrasshopperPrompts");
                var settingsIndex = get.AddOption("Settings");
                var logIndex = get.AddOption("LogFolder");
                get.AcceptNothing(true);
                var result = get.Get();
                if (result != GetResult.Option)
                    return result == GetResult.Nothing ? Result.Success : Result.Cancel;
                var index = get.Option().Index;
                if (index == bakeIndex)
                    WatchActions.Bake(doc, plugin.Conduit.Values);
                else if (index == breakIndex)
                {
                    if (running.Count == 0)
                    {
                        RhinoApp.WriteLine(plugin.Hook.PausedControls().Count > 0
                            ? "miniVivisection: the script is paused already."
                            : "miniVivisection: Break now needs a debug run -- a plain Run isn't in the debugger. Set a breakpoint (or use Debug) and run again.");
                        return Result.Nothing;
                    }
                    foreach (var controls in running)
                        Threads.RequestBreak(controls, 0);
                    UserLog.Info("Break now: the script pauses at its next line.");
                    RhinoApp.WriteLine("miniVivisection: Break now -- the script pauses at its next line.");
                    return Result.Success;
                }
                else if (index == whereIndex)
                {
                    var debuggers = running.Concat(plugin.Hook.PausedControls()).ToList();
                    if (debuggers.Count == 0)
                        PrintThreads(null, plugin.Hook); // a plain Run: still Python's threads
                    foreach (var controls in debuggers)
                        PrintThreads(controls, plugin.Hook);
                    return Result.Success;
                }
                else if (index == stopPausedIndex)
                {
                    // A way out when a paused script's editor can't be reached any more.
                    foreach (var controls in paused)
                        controls.Stop();
                    UserLog.Info("Stopped {0} paused script(s).", paused.Count);
                    RhinoApp.WriteLine("miniVivisection: stopped {0} paused script(s).", paused.Count);
                    return Result.Success;
                }
                else if (index == stopEditingIndex)
                    UserLog.Info("Stopped editing {0} object(s).", plugin.Hook.Edits.EndAll(doc));
                else if (index == promptsIndex)
                    GrasshopperPrompts.Show();
                else if (index == settingsIndex)
                    MiniVivisectionOptionsPage.Open(); // the dialog, at the plug-in's page
                else if (index == logIndex)
                    OpenLogFolder();
                MiniVivisectionSettings.Draw = draw.CurrentValue;
                MiniVivisectionSettings.FreePause = freePause.CurrentValue;
                MiniVivisectionSettings.HoverPreview = hover.CurrentValue;
                MiniVivisectionSettings.WatchExpressions = watchExpressions.CurrentValue;
            }
        }

        /// <summary>Where each thread of a debugged script is, on Rhino's command line (V Threads' "Where is everyone?").</summary>
        private static async void PrintThreads(IDebugControls controls, ScriptEditorHook hook)
        {
            var snapshot = await Threads.TakeAsync(controls, hook, System.TimeSpan.FromSeconds(3));
            RhinoApp.InvokeOnUiThread(new System.Action(() =>
            {
                RhinoApp.WriteLine("miniVivisection -- threads ({0}):", snapshot.WhilePaused ? "paused in " + snapshot.PausedLabel : "running");
                foreach (var row in snapshot.Rows)
                    RhinoApp.WriteLine("  {0,-14} {1,-26} {2}", row.Label, row.State, row.Location?.Where ?? "");
                if (snapshot.Note != null)
                    RhinoApp.WriteLine("  " + snapshot.Note);
            }));
        }

        private static void OpenLogFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Log.Folder);
                Process.Start(new ProcessStartInfo(Log.Folder) { UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                UserLog.Warning("Can't open the log folder: {0}", ex.Message);
            }
        }
    }
}
