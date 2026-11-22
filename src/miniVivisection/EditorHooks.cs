using System;
using System.Collections.Generic;
using System.Linq;

namespace MiniVivisection
{
    /// <summary>
    /// One line on the V Log page once the first Script Editor is set up: which of the
    /// plug-in's hooks into Rhino's internal editor code are in place. A Rhino update that
    /// renames an internal member then shows here, in one place, with the features it
    /// costs -- instead of one warning per feature, or none.
    /// </summary>
    internal static class EditorHooks
    {
        private static bool s_reported;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, HashSet<string>> s_failed =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, HashSet<string>>();

        /// <summary>Whether this is the first failure of <paramref name="what"/> for
        /// <paramref name="editor"/>: said once, retried quietly at the next scans.</summary>
        public static bool FirstFailure(object editor, string what)
        {
            if (editor == null)
                return true;
            lock (s_failed)
                return s_failed.GetValue(editor, _ => new HashSet<string>()).Add(what);
        }

        public static void ReportOnce()
        {
            if (s_reported)
                return;
            s_reported = true;
#if RHINO9
            // Rhino 9: its own editor, its own hooks (Rhino9Ui, Rhino9ErrorPause, ...).
            var hooks = new List<(string Feature, string Problem)>
            {
                ("FreePause", FreePause.HookProblem),
                ("no freeze on the debugger's shared lock", TraceLockGuard.HookProblem),
                ("conditional breakpoints", BreakpointConditions.HookProblem),
                ("step over across files", StepTracking.HookProblem),
                ("V tabs in the console tray", Rhino9Ui.HookProblem),
                ("V left panel pages", BrowserPages.HookProblem9),
                ("folder drop", FolderDrop.HookProblem),
                ("lessons in the Examples tab", EditorExamples.HookProblem),
                ("tooltip values", HoverValues.HookProblem),
                ("breakpoint conditions and Watch / Bake / Edit in the code view's menu", Rhino9CodeMenu.HookProblem),
                ("editor open while debugging", KeepEditorOpen.HookProblem),
                ("Run and Debug buttons", DebugRunButton.HookProblem),
                ("ScriptEditor Run in the debugger", ScriptRunCommand.HookProblem),
                ("RunPythonScript Debug / Stop and record", ScriptRunCommand.RunPythonHookProblem),
                ("pause at an uncaught error, Variables without a breakpoint", Rhino9ErrorPause.HookProblem),
                ("Grasshopper always debug", GrasshopperDebug.HookProblem),
                ("Grasshopper runs (failing runs, Break now)", GrasshopperRuns.HookProblem),
                ("Grasshopper pause guard", GrasshopperPauseGuard.HookProblem),
                ("Grasshopper canvas while paused", GrasshopperPauseCanvas.HookProblem),
                ("Grasshopper hidden editors", GrasshopperHiddenEditors.HookProblem),
            };
#else
            var hooks = new List<(string Feature, string Problem)>
            {
                ("FreePause", FreePause.HookProblem),
                ("no freeze on the debugger's shared lock", TraceLockGuard.HookProblem),
                ("conditional breakpoints", BreakpointConditions.HookProblem),
                ("step over across files", StepTracking.HookProblem),
                ("Watch tab expressions", WatchExpressions.HookProblem),
                ("tooltip values", HoverValues.HookProblem),
                ("V console tabs", ConsoleTabs.HookProblem),
                ("V left panel pages", BrowserPages.HookProblem),
                ("folder drop", FolderDrop.HookProblem),
                ("editor open while debugging", KeepEditorOpen.HookProblem),
                ("Run and Debug buttons", DebugRunButton.HookProblem),
                ("Grasshopper always debug", GrasshopperDebug.HookProblem),
                ("ScriptEditor Run in the debugger", ScriptRunCommand.HookProblem),
                ("lessons in the Examples tab", EditorExamples.HookProblem),
                ("left pages in the compact layout", BrowserPages.CompactHookProblem),
                ("RunPythonScript Debug / Stop and record", ScriptRunCommand.RunPythonHookProblem),
                ("Grasshopper runs (failing runs, Break now)", GrasshopperRuns.HookProblem),
                ("Grasshopper pause guard", GrasshopperPauseGuard.HookProblem),
                ("Grasshopper hidden editors", GrasshopperHiddenEditors.HookProblem),
                ("Grasshopper canvas while paused", GrasshopperPauseCanvas.HookProblem),
            };
#endif
            // Grasshopper's hooks install when Grasshopper loads (Rhino 9 says so: "waiting for
            // Grasshopper"): not off, said apart.
            const string Waiting = "waiting for Grasshopper";
            var waiting = hooks.Where(h => h.Problem == Waiting).Select(h => h.Feature).ToList();
            var missing = hooks.Where(h => h.Problem != null && h.Problem != Waiting).ToList();
            var later = waiting.Count == 0 ? "" : "; " + Waiting + ": " + string.Join(", ", waiting);
            if (missing.Count == 0)
                UserLog.Info("Editor hooks: all {0} in place{1}.", hooks.Count - waiting.Count, later);
            else
                UserLog.Warning("Editor hooks: {0} of {1} in place; off: {2}{3}.", hooks.Count - waiting.Count - missing.Count, hooks.Count - waiting.Count,
                    string.Join("; ", missing.Select(m => m.Feature + " (" + m.Problem + ")")), later);
        }
    }
}
