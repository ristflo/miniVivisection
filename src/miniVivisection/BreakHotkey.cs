using System;
using System.Runtime.InteropServices;
using System.Threading;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// A hotkey for Break now (Options: "Break now hotkey", default Ctrl+Alt+B). A script
    /// without <c># async:true</c> runs on Rhino's UI thread, which then handles no keys at
    /// all -- so the key is a Windows hotkey, registered on a thread of its own with its own
    /// message loop: it arrives while the UI thread runs the script. It asks for a break
    /// (Threads.RequestBreak): the script pauses at its next traced line, and FreePause
    /// keeps Rhino usable there. Registered only while a script runs (a Windows hotkey is
    /// the whole system's), and for debug runs only it can pause anything.
    /// </summary>
    internal static class BreakHotkey
    {
        private const int HotkeyId = 0x4D56; // "MV"
        private const int WmHotkey = 0x0312;
        private const int WmRegister = 0x0400 + 1; // WM_USER + 1
        private const int WmUnregister = 0x0400 + 2;
        private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;

        private static Thread s_thread;
        private static uint s_threadId;
        private static ScriptEditorHook s_hook;
        private static readonly ManualResetEventSlim s_ready = new ManualResetEventSlim();

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out Msg msg, IntPtr hWnd, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>The key of the setting, as Windows modifiers and virtual key; null: off.</summary>
        internal static (uint Modifiers, uint Key)? KeyOf(string setting)
        {
            switch (setting)
            {
                case "Ctrl+Alt+B":
                    return (ModControl | ModAlt, 0x42);
                case "Pause":
                    return (0, 0x13); // VK_PAUSE
                case "Ctrl+Break":
                    return (ModControl, 0x03); // VK_CANCEL: Ctrl+Pause
                case "Ctrl+Shift+B":
                    return (ModControl | ModShift, 0x42);
                default:
                    return null;
            }
        }

        /// <summary>The hotkey's thread, once (at load).</summary>
        public static void Start(ScriptEditorHook hook)
        {
            if (s_thread != null)
                return;
            s_hook = hook;
            s_thread = new Thread(Loop) { IsBackground = true, Name = "miniVivisection break hotkey" };
            s_thread.Start();
            s_ready.Wait(TimeSpan.FromSeconds(2));
        }

        private static int s_runs; // runs going on now: the key is registered while > 0

        /// <summary>A run started: the hotkey works until the last run going ends. Runs
        /// overlap -- a Grasshopper component's solution inside the editor's debug run, a
        /// RunPythonScript run -- so they're counted: registered at the first, released at the
        /// last (a run's end must match its start).</summary>
        public static void RunStarted()
        {
            if (System.Threading.Interlocked.Increment(ref s_runs) == 1 && s_threadId != 0)
                PostThreadMessage(s_threadId, WmRegister, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>A run ended: when it was the last one, the key is the system's again.</summary>
        public static void RunEnded()
        {
            var left = System.Threading.Interlocked.Decrement(ref s_runs);
            if (left < 0)
            {
                System.Threading.Interlocked.Exchange(ref s_runs, 0); // an end without its start
                return;
            }
            if (left == 0 && s_threadId != 0)
                PostThreadMessage(s_threadId, WmUnregister, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>The hotkey setting changed: during a run, the new key now.</summary>
        public static void Refresh()
        {
            if (System.Threading.Volatile.Read(ref s_runs) > 0 && s_threadId != 0)
                PostThreadMessage(s_threadId, WmRegister, IntPtr.Zero, IntPtr.Zero);
        }

        private static void Loop()
        {
            s_threadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // makes this thread's message queue
            s_ready.Set();
            if (System.Threading.Volatile.Read(ref s_runs) > 0)
                PostThreadMessage(s_threadId, WmRegister, IntPtr.Zero, IntPtr.Zero); // a run started before this thread was up
            var registered = false;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                try
                {
                    switch (msg.Message)
                    {
                        case WmRegister:
                            if (registered)
                                UnregisterHotKey(IntPtr.Zero, HotkeyId);
                            registered = false;
                            var setting = MiniVivisectionSettings.PluginOn ? MiniVivisectionSettings.BreakHotkey : "Off";
                            if (KeyOf(setting) is (uint modifiers, uint key))
                            {
                                registered = RegisterHotKey(IntPtr.Zero, HotkeyId, modifiers | ModNoRepeat, key);
                                if (registered)
                                    Log.Debug(LogArea.Pause, "break hotkey {0} on for this run", setting);
                                else
                                    UserLog.Warning("The Break now hotkey {0} is taken by another program (error {1}): choose another in V Settings.", setting, Marshal.GetLastWin32Error());
                            }
                            break;
                        case WmUnregister:
                            if (registered)
                                UnregisterHotKey(IntPtr.Zero, HotkeyId);
                            registered = false;
                            break;
                        case WmHotkey:
                            Break();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(LogArea.Pause, "break hotkey: {0}", Log.Describe(ex));
                }
            }
        }

        /// <summary>The hotkey: every debugged script pauses at its next line (any thread).</summary>
        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint type);

        private const uint BeepOk = 0x40;    // MB_ICONASTERISK: pausing or stopping
        private const uint BeepNo = 0x10;    // MB_ICONHAND: nothing it can pause or stop

        /// <summary>The hotkey's answer, at once: while a script runs on Rhino's UI thread
        /// nothing on screen can change, so it's a sound (V Log has the words).</summary>
        private static void Beep(bool ok) => MessageBeep(ok ? BeepOk : BeepNo);

        private static void Break()
        {
            var running = s_hook?.ControlsIn(DebugControlsState.Debugging);
            // A Grasshopper component running plainly holds Rhino's UI thread: it's stopped first,
            // whatever else is -- or only seems to be -- in the debugger. A pause asked of those
            // never came: a debug run that failed and never ended had left its debugger
            // "debugging", and Ctrl+Alt+B on GH 10's hanging Spiral froze Rhino.
            if (running != null && running.Count > 0 && GrasshopperRuns.RunsPlainly(running) && GrasshopperRuns.Stop() == true)
            {
                Beep(true);
                return;
            }
            if (running == null || running.Count == 0)
            {
                var grasshopper = GrasshopperRuns.Stop();
                if (grasshopper == true)
                {
                    Beep(true); // a Grasshopper script component without its editor: stopped, then shown
                    return;
                }
                if (grasshopper == false && !StoppedRuns.Active)
                {
                    Beep(false); // it can't be stopped (not Python 3), and nothing else runs
                    return;
                }
                // false with a RunPythonScript run going on: that one, below
            }
            if ((running == null || running.Count == 0) && StoppedRuns.Active)
            {
                Beep(true);
                UserLog.Info("Break now ({0}): stopping the script RunPythonScript runs -- the Script Editor then shows where.", MiniVivisectionSettings.BreakHotkey);
                StoppedRuns.Stop(); // RunPythonScript, Stop and record: no debugger to pause it
                return;
            }
            if (running == null || running.Count == 0)
            {
                Beep(false);
                UserLog.Info("Break now ({0}): no script runs in the debugger -- a plain Run isn't in the debugger. Debug it (a breakpoint and F5, or V Threads > Debug).",
                    MiniVivisectionSettings.BreakHotkey);
                return;
            }
            Beep(true);
            foreach (var controls in running)
                Threads.RequestBreak(controls, 0);
            UserLog.Info("Break now ({0}): the script pauses at its next line.", MiniVivisectionSettings.BreakHotkey);
        }
    }
}
