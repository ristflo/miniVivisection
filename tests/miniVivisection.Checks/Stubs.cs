// Stand-ins for the plug-in's parts that need a running Rhino.
using System;
using System.Collections.Generic;
using Rhino;

namespace MiniVivisection
{
    /// <summary>Stand-in for the plug-in: no Rhino, so no settings store -- the
    /// settings read their defaults.</summary>
    internal sealed class MiniVivisectionPlugin
    {
        public static MiniVivisectionPlugin Instance => null;

        public PersistentSettings Settings => null;

        public static string BuildTime => "checks";

        public static string FullVersion => "checks";

        internal static int BuildNumber => 0;

        internal const string ProductName = "miniVivisection";

        internal static string ProductVersion => "checks";
    }

    /// <summary>Stand-in for the hook: only what the checked files use.</summary>
    internal sealed class ScriptEditorHook
    {
        public static bool IsPythonPause(Rhino.Runtime.Code.Execution.Debugging.IDebugControls controls) => false;

        public bool RunOnPausedThread(Rhino.Runtime.Code.Execution.Debugging.IDebugControls controls, Action job) => false;

        public static string FirstLine(Exception ex)
        {
            while (ex is System.Reflection.TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException;
            var message = (ex.Message ?? ex.GetType().Name).Trim();
            var newline = message.IndexOf('\n');
            return newline < 0 ? message : message.Substring(0, newline).Trim();
        }
    }

    internal static class WatchActions
    {
        public const string NotReachable = "the paused script can't be reached";
    }

    internal static class EditorTheme
    {
        public static void Apply(object editor, params Eto.Forms.Control[] controls)
        {
        }
    }

    /// <summary>Stand-in for TerminalHost: records the Python commands' calls.</summary>
    public sealed class TerminalHost
    {
        public static TerminalHost Instance { get; } = new TerminalHost();

        public readonly List<string> Calls = new List<string>();

        public string Show(string label, object value, string interpretation)
        {
            Calls.Add("show " + label);
            return label + ": shown";
        }

        public string Hide(string label)
        {
            Calls.Add("hide " + label);
            return label + " hidden";
        }

        public string Clear()
        {
            Calls.Add("clear");
            return "cleared";
        }

        public string Bake(string label, object value)
        {
            Calls.Add("bake " + label);
            return label + ": baked";
        }

        public string Watch(string expression)
        {
            Calls.Add("watch " + expression);
            return expression + " watched";
        }

        public string Edit(string expression)
        {
            Calls.Add("edit " + expression);
            return "editing " + expression;
        }
    }
}
