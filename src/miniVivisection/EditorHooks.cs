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
            var missing = hooks.Where(h => h.Problem != null).ToList();
            if (missing.Count == 0)
                UserLog.Info("Editor hooks: all {0} in place.", hooks.Count);
            else
                UserLog.Warning("Editor hooks: {0} of {1} in place; off: {2}.", hooks.Count - missing.Count, hooks.Count,
                    string.Join("; ", missing.Select(m => m.Feature + " (" + m.Problem + ")")));
        }
    }
}
