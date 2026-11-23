#if RHINO9
using System;
using System.Linq;
using System.Reflection;
using Rhino.Runtime.Code.Editor;
using Rhino.Runtime.Code.Editor.Commands;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// Rhino 9's Stop, Step Over, Step Into and Step Out (<c>DebugControlsCommand&lt;TEditor&gt;</c>)
    /// can run only while the editor's own run is a debug run (<c>Codes.RunKind == Debug</c>) and its
    /// debugger is paused -- worked out again only when the editor says RunKind or
    /// DebugControlsState changed. RunKind is set only by a run the editor started itself (its
    /// Debug button), and it says DebugControlsState changed only around such runs: a Grasshopper
    /// component paused in a debug run Grasshopper started -- Always debug, the pause at an uncaught
    /// error, Break now, Debug this component, a slider moved -- had them all off in its editor,
    /// only Continue on (Rhino 8 has no such condition). So at a pause, a Continue and a run's end
    /// every editor is told DebugControlsState changed (its commands follow Rhino's rule: off when
    /// nothing is paused), and while it is paused they are turned on -- the steps not at an
    /// exception, as Rhino has it. Not a patch of OnModelChanged: .NET recompiled that busy generic
    /// method after some calls and the patch was gone.
    /// </summary>
    internal static class Rhino9DebugButtons
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static MethodInfo s_setCanExecute;
        private static string s_error;

        /// <summary>For the health line (EditorHooks): null when it can work, else why not.</summary>
        internal static string HookProblem => s_error ?? (s_setCanExecute == null ? "not installed" : null);

        /// <summary>For the live test: debug commands turned on in a pause the editor didn't start.</summary>
        internal static int TurnedOn;

        public static void TryInstall()
        {
            if (s_setCanExecute != null || s_error != null)
                return;
            try
            {
                s_setCanExecute = typeof(Command).GetProperty(nameof(Command.CanExecute), Any)?.GetSetMethod(true)
                    ?? throw new MissingMethodException(typeof(Command).FullName, "set_CanExecute");
            }
            catch (Exception ex)
            {
                s_error = ex.Message;
                UserLog.Warning("Rhino 9: Stop and the steps stay off in a Grasshopper component's editor when Grasshopper started the debug run: {0}", ex.Message);
            }
        }

        /// <summary>At a pause, a Continue, a run's end (ScriptEditorHook, any thread): every editor's
        /// debug commands, now and twice shortly after (the editor may set its state after the
        /// debugger's event, and set them again when its current frame changes).</summary>
        internal static void RefreshSoon()
        {
            if (s_setCanExecute == null || Eto.Forms.Application.Instance == null)
                return;
            Eto.Forms.Application.Instance.AsyncInvoke(() =>
            {
                RefreshAll();
                foreach (var seconds in new[] { 0.25, 1.0 })
                {
                    var timer = new Eto.Forms.UITimer { Interval = seconds };
                    timer.Elapsed += (s, e) =>
                    {
                        timer.Stop();
                        timer.Dispose();
                        RefreshAll();
                    };
                    timer.Start();
                }
            });
        }

        private static void RefreshAll()
        {
            foreach (var window in Eto.Forms.Application.Instance.Windows)
            {
                var editor = Rhino9Editor.ModelOf(window);
                if (editor != null)
                    Refresh(editor);
            }
        }

        private static void Refresh(IEditor editor)
        {
            try
            {
                var codes = editor.Codes;
                if (codes == null)
                    return;
                // Rhino's rule, worked out again (off when nothing is paused)
                codes.GetType().GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null)
                    ?.Invoke(codes, new object[] { "DebugControlsState" });
                if (!MiniVivisectionSettings.PluginOn || codes.DebugControlsState != DebugControlsState.Paused || editor.Preview?.IsViewing == true)
                    return;
                var atException = (codes.Current?.DebugFrame ?? ExecFrame.Empty).Event == ExecEvent.Exception;
                var commands = editor.GetType().GetProperty("Commands", Any)?.GetValue(editor);
                if (commands == null)
                    return;
                foreach (var property in commands.GetType().GetProperties(Any).Where(p => p.GetIndexParameters().Length == 0))
                {
                    if (!(property.GetValue(commands) is Command command) || command.CanExecute || !Derives(command.GetType(), typeof(DebugControlsCommand<>)))
                        continue;
                    if (atException && (Derives(command.GetType(), typeof(DebugStepOverCommand<>)) || Derives(command.GetType(), typeof(DebugStepIntoCommand<>)) ||
                                        Derives(command.GetType(), typeof(DebugStepOutCommand<>))))
                        continue; // no step at an exception (the steps' own rule)
                    s_setCanExecute.Invoke(command, new object[] { true });
                    TurnedOn++;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Pause, "Stop and the steps in a paused run: {0}", Log.Describe(ex));
            }
        }

        private static bool Derives(Type type, Type genericDefinition)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == genericDefinition)
                    return true;
            }
            return false;
        }
    }
}
#endif
