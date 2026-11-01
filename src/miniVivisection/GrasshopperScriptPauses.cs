using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eto.Forms;
using Rhino;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// A Grasshopper script paused in the debugger -- at a breakpoint, a step, Ctrl+Alt+B or an
    /// exception -- shown where its script is edited:
    /// - the editor whose debugger paused it is brought up (shown, restored, in front): the
    ///   component's own script editor, or the Script Editor showing its script input's file --
    ///   the file itself, or a Panel's working copy (GrasshopperInputScripts.TryDebugRun). A
    ///   hidden, cloaked editor is uncovered by ScriptRunCommand.Reveal first;
    /// - a Panel's script: the Panel marks the paused line and says why along its bottom
    ///   (miniVivisection.GH's ScriptPanels.ShowPause) until the run continues or ends; a
    ///   double-click on it then brings the Script Editor up (<see cref="Raise"/>).
    /// A Panel also shows the breakpoints the Script Editor has in its working copy, in its gutter
    /// (ScriptPanels.ShowBreakpoints) -- followed through the breakpoints' Changed event.
    /// </summary>
    internal static class GrasshopperScriptPauses
    {
        private sealed class Paused
        {
            public WeakReference<object> Panel;
        }

        // The Panel each paused debugger marks now, cleared when it continues or ends
        private static readonly ConditionalWeakTable<IDebugControls, Paused> s_paused = new ConditionalWeakTable<IDebugControls, Paused>();
        // A paused Panel's editor (its debugger): what a double-click on the Panel brings up
        private static readonly ConditionalWeakTable<object, WeakReference<Window>> s_editorOf = new ConditionalWeakTable<object, WeakReference<Window>>();
        // The breakpoint sets followed, and each Panel's breakpoints as last shown ("3,7-": 7 off)
        private static readonly ConditionalWeakTable<DebugBreakpointSet, object> s_followedSets = new ConditionalWeakTable<DebugBreakpointSet, object>();
        private static readonly ConditionalWeakTable<object, string> s_shownBreakpoints = new ConditionalWeakTable<object, string>();

        /// <summary>ScriptEditorHook's Paused, on the paused thread: a Grasshopper run's editor
        /// brought up, a Panel's script marked at the line (on the UI thread, which the pause keeps
        /// answering: FreePause).</summary>
        internal static void AtPause(IDebugControls controls, object editor)
        {
            try
            {
                var frame = controls.Results?.CurrentThread?.CurrentFrame;
                var uri = frame?.Reference?.Uri;
                var path = uri != null && uri.IsFile ? uri.LocalPath : null;
                var line = frame?.Reference?.Position.LineNumber ?? 0;
                var grasshopper = GrasshopperRuns.RunningComponent != null || ReferenceEquals(GrasshopperInputScripts.DebuggingControls, controls);
                var panel = path == null ? null : GrasshopperInputScripts.PanelOf(path);
                if (!grasshopper && panel == null)
                    return;
                var title = panel == null ? null : Title(controls, frame, path, line);
                if (panel != null)
                    s_paused.AddOrUpdate(controls, new Paused { Panel = new WeakReference<object>(panel) });
                RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    var window = editor as Window;
                    if (grasshopper && window != null)
                        Bring(window);
                    if (panel == null)
                        return;
                    if (window != null)
                        s_editorOf.AddOrUpdate(panel, new WeakReference<Window>(window));
                    CallPanels("ShowPause", panel, line, title);
                    Log.Info(LogArea.Breakpoints, "Panel '{0}': {1} -- shown on the Panel", GrasshopperInputScripts.NickName(panel), title);
                }));
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Breakpoints, "Grasshopper: the pause's editor and Panel: {0}", Log.Describe(ex));
            }
        }

        /// <summary>ScriptEditorHook's Continued, Stopped and the like: the Panel it marked shows no
        /// pause any more.</summary>
        internal static void Resumed(IDebugControls controls)
        {
            if (controls == null || !s_paused.TryGetValue(controls, out var paused))
                return;
            s_paused.Remove(controls);
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                if (paused.Panel.TryGetTarget(out var panel))
                    CallPanels("ShowPause", panel, 0, null);
            }));
        }

        /// <summary>A double-click on a paused Panel (miniVivisection.GH's ScriptPanels, by
        /// reflection; UI thread): the Script Editor whose debugger paused it, brought up. False
        /// when there is none.</summary>
        internal static bool Raise(object panel)
        {
            if (panel == null || !s_editorOf.TryGetValue(panel, out var editor) || !editor.TryGetTarget(out var window))
                return false;
            Bring(window);
            return true;
        }

        // Shown, restored, in front (UI thread) -- left alone when it has the focus (a step)
        private static void Bring(Window window)
        {
            try
            {
                if (!window.Visible)
                {
                    if (window is Form form)
                        form.Show(); // closed: Grasshopper's and the Script Editor's windows only hide
                    else
                        window.Visible = true;
                }
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                if (!window.HasFocus)
                    window.BringToFront();
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper: the paused editor brought up: {0}", Log.Describe(ex));
            }
        }

        // The Panel's line about the pause: at a breakpoint, an exception, or a step / Ctrl+Alt+B
        private static string Title(IDebugControls controls, ExecFrame frame, string path, int line)
        {
            if (frame.Event == ExecEvent.Exception)
                return string.Format("Stopped at an exception on line {0}: {1}", line, frame.Exception?.Id);
            var atBreakpoint = controls.Breakpoints.OfType<CodeReferenceBreakpoint>().Any(b => b.IsActive && b.Reference?.Uri != null && b.Reference.Uri.IsFile
                && string.Equals(b.Reference.Uri.LocalPath, path, StringComparison.OrdinalIgnoreCase) && b.Reference.Position.LineNumber == line);
            return (atBreakpoint ? "Paused at the breakpoint on line " : "Paused on line ") + line;
        }

        // --- a Panel's breakpoints ------------------------------------------------------------------

        /// <summary>The Script Editor's code for a Panel's working copy is open (GrasshopperInputScripts,
        /// UI thread): the breakpoints shown in the Panels, and followed as they change.</summary>
        internal static void FollowBreakpoints(Rhino.Runtime.Code.Code code)
        {
            try
            {
                if (code == null || !code.HasDebugControls)
                    return;
                var set = code.DebugControls.Breakpoints;
                if (!s_followedSets.TryGetValue(set, out _))
                {
                    s_followedSets.Add(set, null);
                    set.Changed += _ => RhinoApp.InvokeOnUiThread(new Action(UpdateBreakpoints));
                }
                UpdateBreakpoints();
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "a Panel's breakpoints followed: {0}", Log.Describe(ex));
            }
        }

        /// <summary>Every followed Panel's breakpoints, as the Script Editor's code for its working copy
        /// has them (none when no editor shows it) -- given to the Panels that show others (UI thread).</summary>
        internal static void UpdateBreakpoints()
        {
            try
            {
                var codes = GrasshopperInputScripts.OpenCodes().Select(c => c.Code).ToList();
                foreach (var (path, panel) in GrasshopperInputScripts.FollowedPanels())
                {
                    var code = codes.FirstOrDefault(c => GrasshopperInputScripts.Shows(c, path) && c.HasDebugControls);
                    var breakpoints = code == null ? new List<CodeReferenceBreakpoint>()
                        : code.DebugControls.Breakpoints.WhereReferences(code).OrderBy(b => b.Reference.Position.LineNumber).ToList();
                    var shown = string.Join(",", breakpoints.Select(b => b.Reference.Position.LineNumber + (b.IsActive ? "" : "-")));
                    if (s_shownBreakpoints.TryGetValue(panel, out var before) && before == shown)
                        continue;
                    s_shownBreakpoints.AddOrUpdate(panel, shown);
                    CallPanels("ShowBreakpoints", panel, breakpoints.Select(b => b.Reference.Position.LineNumber).ToArray(), breakpoints.Select(b => b.IsActive).ToArray());
                    Log.Info(LogArea.Breakpoints, "Panel '{0}': breakpoints {1}", GrasshopperInputScripts.NickName(panel), shown.Length == 0 ? "none" : shown);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "the Panels' breakpoints: {0}", Log.Describe(ex));
            }
        }

        private static readonly Dictionary<string, MethodInfo> s_panelMethods = new Dictionary<string, MethodInfo>();

        // miniVivisection.GH's ScriptPanels.<method>, by reflection (the .rhp doesn't reference the .gha)
        private static void CallPanels(string method, params object[] args)
        {
            try
            {
                if (!s_panelMethods.TryGetValue(method, out var found) || found == null)
                {
                    found = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection.GH")
                        ?.GetType("MiniVivisection.GH.ScriptPanels")
                        ?.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    s_panelMethods[method] = found;
                }
                found?.Invoke(null, args);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "the Panel ({0}): {1}", method, Log.Describe(ex));
            }
        }
    }
}
