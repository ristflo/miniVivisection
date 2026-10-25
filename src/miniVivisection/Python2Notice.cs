using System;
using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// A notice line on top of the V Python and V Claude tabs while an IronPython 2 script
    /// (<c>#! python2</c>) is being debugged: their Python is Rhino's Python 3, which can't
    /// reach that script's variables (ScriptKinds). Shown while a debug run of such a script
    /// is going on, gone when it ends. Updated at every pause, continue and end
    /// (ConsoleTabs.UpdateStatus) and when the tab is shown.
    /// </summary>
    internal sealed class Python2Notice : Panel
    {
        private static readonly List<WeakReference<Python2Notice>> s_all = new List<WeakReference<Python2Notice>>();

        private readonly object _editor;

        public Python2Notice(object editor, string text)
        {
            _editor = editor;
            Visible = false;
            BackgroundColor = Color.FromArgb(255, 243, 205); // amber, readable on light and dark themes
            Padding = new Padding(6, 3);
            Content = new Label { Text = text, TextColor = Color.FromArgb(102, 77, 3), Wrap = WrapMode.Word };
            Load += (sender, e) => Update();
            lock (s_all)
                s_all.Add(new WeakReference<Python2Notice>(this));
        }

        /// <summary>Above <paramref name="content"/>: the notice, then the tab.</summary>
        public static Control Above(Control content, Python2Notice notice) =>
            new TableLayout { Rows = { new TableRow(notice), new TableRow(content) { ScaleHeight = true } } };

        /// <summary>Every notice follows its editor's debug run (UI thread).</summary>
        public static void UpdateAll()
        {
            lock (s_all)
            {
                s_all.RemoveAll(r => !r.TryGetTarget(out _));
                foreach (var reference in s_all)
                {
                    if (reference.TryGetTarget(out var notice))
                        notice.Update();
                }
            }
        }

        private void Update()
        {
            try
            {
                var access = new ScriptEditorAccess(_editor);
                var controls = access.DebugControls;
                var debugging = controls != null && (controls.State == DebugControlsState.Debugging || controls.State == DebugControlsState.Paused);
                var frameUri = controls?.Results?.CurrentThread?.CurrentFrame?.Reference?.Uri;
                var kind = controls?.State == DebugControlsState.Paused && frameUri != null
                    ? ScriptKinds.Of(frameUri)
                    : ScriptKinds.Of(access.Code?.Uri, access.Language);
                var show = debugging && kind == ScriptKind.IronPython;
                if (Visible != show)
                    Visible = show;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "Python 2 notice: {0}", Log.Describe(ex));
            }
        }
    }
}
