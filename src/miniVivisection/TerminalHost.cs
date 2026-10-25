using System;
using Rhino;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// What the Python commands <c>show</c>, <c>hide</c>, <c>clear</c>, <c>bake</c>,
    /// <c>watch</c> and <c>edit</c> call (PythonBridge's helpers, through pythonnet --
    /// hence public). They run while the Python tab's input or Claude's run_python
    /// runs: on the paused thread, or on the UI thread when nothing is paused; the GIL
    /// is held either way, so Python values (lists) can be read.
    /// </summary>
    public sealed class TerminalHost
    {
        private ScriptEditorAccess _access;
        private ScriptEditorHook _hook;

        private TerminalHost()
        {
        }

        public static TerminalHost Instance { get; } = new TerminalHost();

        /// <summary>The Script Editor the next input belongs to.</summary>
        internal void Use(ScriptEditorAccess access, ScriptEditorHook hook)
        {
            _access = access;
            _hook = hook;
        }

        private IDebugControls PausedControls()
        {
            var controls = _access?.DebugControls;
            return controls != null && controls.State == DebugControlsState.Paused ? controls : null;
        }

        /// <summary>show(value): draws a copy until hide/clear.</summary>
        public string Show(string label, object value, string interpretation)
        {
            if (_hook == null)
                return "show: not available here";
            var items = WatchGeometry.Drawable(value, interpretation, true, out _, out _, out var problem);
            if (problem != null)
                return string.Format("{0}: {1}", label, problem);
            if (items.Count == 0)
                return string.Format("{0}: nothing to draw ({1})", label, ScriptEditorHook.TypeName(value));
            _hook.Conduit.SetShown(label, new WatchedValue(label, WatchGeometry.ColorFor(label), items));
            Redraw();
            return string.Format("{0}: shown in Rhino ({1})", label, items.Count == 1 ? Describe(value) : items.Count + " items");
        }

        public string Hide(string label)
        {
            if (_hook == null)
                return "hide: not available here";
            if (!_hook.Conduit.HideShown(label))
                return label + " isn't shown";
            Redraw();
            return label + " hidden";
        }

        public string Clear()
        {
            if (_hook == null)
                return "clear: not available here";
            var count = _hook.Conduit.ClearShown();
            Redraw();
            return string.Format("cleared {0} shown value(s)", count);
        }

        /// <summary>bake(value): copies into the document.</summary>
        public string Bake(string label, object value)
        {
            var items = WatchGeometry.Drawable(value, "auto", true, out _, out _, out _);
            if (items.Count == 0)
                return string.Format("{0}: nothing to bake ({1})", label, ScriptEditorHook.TypeName(value));
            var watched = new WatchedValue(label, WatchGeometry.ColorFor(label), items);
            RhinoApp.InvokeOnUiThread(new Action(() => WatchActions.Bake(RhinoDoc.ActiveDoc, new[] { watched })));
            return string.Format("{0}: baking {1} object(s) to layer {2}::Baked", label, items.Count, WatchActions.RootLayer);
        }

        /// <summary>watch("expression"): onto the V Watch tab, drawn at every pause.</summary>
        public string Watch(string expression)
        {
            var controls = PausedControls();
            if (controls == null || _hook == null)
                return "watch: the debugger isn't paused";
            return _hook.AddRhinoWatch(controls, expression)
                ? expression + VNames.Text(" added to the V Watch tab")
                : "watch: this Script Editor isn't known yet";
        }

        /// <summary>breakpoints(): the script's breakpoint conditions, hit counts and log
        /// messages, with this run's hits and errors.</summary>
        public string Breakpoints()
        {
            var uri = _access?.Code?.Uri;
            if (uri == null)
                return "breakpoints: no script is open";
            var specs = BreakpointSpecs.Of(uri);
            if (specs.Count == 0)
                return "No breakpoint has a condition, hit count or log message (right-click a line: Breakpoint Condition\u2026).";
            var lines = new System.Collections.Generic.List<string>();
            foreach (var spec in specs)
                lines.Add(string.Format("line {0}: {1} -- {2} hit(s){3}", spec.Line, spec.Summary, spec.Hits, spec.LastError != null ? ", error: " + spec.LastError : ""));
            if (!MiniVivisectionSettings.ConditionalBreakpoints)
                lines.Add(VNames.Text("(conditional breakpoints are off -- V Settings > Debugger > Conditional breakpoints\u2026: every breakpoint pauses)"));
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>reload_visualizers(): the Options page's visualizer scripts again.</summary>
        public string ReloadVisualizers() => VisualizerScripts.LoadHere();

        /// <summary>edit("expression"): Edit in Rhino, written back into the script.</summary>
        public string Edit(string expression)
        {
            var controls = PausedControls();
            if (controls == null || _hook == null)
                return "edit: the debugger isn't paused";
            _hook.Edits.Start(controls, expression);
            return "editing " + expression + " in Rhino (see the command line)";
        }

        private static string Describe(object value) => PythonBridge.IsPython(value) ? "a Python value" : value?.GetType().Name ?? "None";

        private static void Redraw() => RhinoApp.InvokeOnUiThread(new Action(() => RhinoDoc.ActiveDoc?.Views.Redraw()));
    }
}
