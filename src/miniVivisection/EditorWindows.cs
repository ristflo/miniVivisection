using System;
using Eto.Forms;

namespace MiniVivisection
{
    /// <summary>The Script Editor's windows (RhinoCodeEditor's RCE, an Eto form): work tied
    /// to one -- timers, static event handlers -- ends when it closes.</summary>
    internal static class EditorWindows
    {
        /// <summary>Runs <paramref name="action"/> once when <paramref name="editor"/>'s window
        /// closes (not when it is only hidden). Nothing for a non-window.</summary>
        public static void WhenClosed(object editor, Action action)
        {
            if (!(editor is Window window))
                return;
            EventHandler<EventArgs> closed = null;
            closed = (sender, e) =>
            {
                window.Closed -= closed;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Scan, "after a Script Editor closed: {0}", Log.Describe(ex));
                }
            };
            window.Closed += closed;
        }
    }
}
