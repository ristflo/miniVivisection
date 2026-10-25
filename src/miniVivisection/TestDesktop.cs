using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Eto.Forms;

namespace MiniVivisection
{
    /// <summary>
    /// Live tests only: the test Rhino's windows on another virtual desktop, out of the user's
    /// way. The test runner (tests/live/run_live.py --desktop) sets MINIVIVISECTION_TEST_DESKTOP:
    /// "vscode:Name" -- the desktop of VS Code's window whose title has Name (the repository's
    /// folder), or of its first window -- or a number (Windows' order, from the registry). Windows lets a program move only its own windows to another desktop
    /// (IVirtualDesktopManager.MoveWindowToDesktop), so the plug-in moves Rhino's: every top-level
    /// window of this process, as it appears (a UI timer, every 0.1 s). Nothing without the
    /// variable. Rhino and Grasshopper activate their own windows, though, and Windows then
    /// takes the user to that desktop: run_live.py --desktop hidden (a Win32 desktop of its own,
    /// never on screen) is the quiet way.
    /// </summary>
    internal static class TestDesktop
    {
        [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopManager
        {
            [PreserveSig]
            int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out bool onCurrentDesktop);

            [PreserveSig]
            int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

            [PreserveSig]
            int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
        }

        [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
        private class VirtualDesktopManager
        {
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int length);

        private static UITimer s_timer;
        private static IVirtualDesktopManager s_manager;
        private static Guid s_target;
        private static readonly HashSet<IntPtr> s_moved = new HashSet<IntPtr>();
        private static bool s_shown;

        /// <summary>At load: only with MINIVIVISECTION_TEST_DESKTOP (the live tests).</summary>
        public static void Start()
        {
            var wanted = System.Environment.GetEnvironmentVariable("MINIVIVISECTION_TEST_DESKTOP");
            if (string.IsNullOrWhiteSpace(wanted) || s_timer != null)
                return;
            try
            {
                s_manager = (IVirtualDesktopManager)new VirtualDesktopManager();
                if (!TryFindDesktop(wanted.Trim(), out s_target))
                {
                    Log.Warning(LogArea.General, "test desktop: no virtual desktop for '{0}' -- the windows stay where they are", wanted);
                    return;
                }
                Log.Info(LogArea.General, "test desktop: Rhino's windows go to virtual desktop {0} ('{1}')", s_target, wanted);
                MoveAll();
                s_timer = new UITimer { Interval = 0.1 };
                s_timer.Elapsed += (s, e) => MoveAll();
                s_timer.Start();
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.General, "test desktop: {0}", Log.Describe(ex));
            }
        }

        private static bool TryFindDesktop(string wanted, out Guid desktop)
        {
            desktop = Guid.Empty;
            if (wanted.StartsWith("vscode", StringComparison.OrdinalIgnoreCase))
            {
                // VS Code's windows (one process has them all): the one whose title names the folder, else the first
                var name = wanted.Length > 7 ? wanted.Substring(7) : "";
                var code = new HashSet<uint>(Process.GetProcessesByName("Code").Select(p => (uint)p.Id));
                var found = Guid.Empty;
                var first = Guid.Empty;
                EnumWindows((hwnd, _) =>
                {
                    if (!IsWindowVisible(hwnd) || GetWindowThreadProcessId(hwnd, out var pid) == 0 || !code.Contains(pid))
                        return true;
                    var title = new System.Text.StringBuilder(512);
                    if (GetWindowText(hwnd, title, title.Capacity) == 0 || s_manager.GetWindowDesktopId(hwnd, out var id) != 0 || id == Guid.Empty)
                        return true;
                    if (first == Guid.Empty)
                        first = id;
                    if (name.Length > 0 && title.ToString().IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = id;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
                desktop = found != Guid.Empty ? found : first;
                return desktop != Guid.Empty;
            }
            if (!int.TryParse(wanted, out var number) || number < 1)
                return Guid.TryParse(wanted, out desktop);
            // Windows' desktops, in its order: HKCU\...\Explorer\VirtualDesktops, VirtualDesktopIDs (16 bytes each)
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops"))
            {
                if (!(key?.GetValue("VirtualDesktopIDs") is byte[] ids) || ids.Length < number * 16)
                    return false;
                desktop = new Guid(ids.Skip((number - 1) * 16).Take(16).ToArray());
                return true;
            }
        }

        // This process's visible top-level windows not on the test desktop yet: moved there.
        private static void MoveAll()
        {
            try
            {
                var own = (uint)Process.GetCurrentProcess().Id;
                var windows = new List<IntPtr>();
                EnumWindows((hwnd, _) =>
                {
                    if (IsWindowVisible(hwnd) && GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == own)
                        windows.Add(hwnd);
                    return true;
                }, IntPtr.Zero);
                foreach (var hwnd in windows)
                {
                    if (s_manager.GetWindowDesktopId(hwnd, out var desktop) != 0)
                        continue;
                    if (desktop != s_target)
                    {
                        var target = s_target;
                        var result = s_manager.MoveWindowToDesktop(hwnd, ref target);
                        if (s_moved.Add(hwnd))
                            Log.Debug(LogArea.General, "test desktop: window {0} moved (0x{1:X8})", hwnd.ToInt64(), result);
                        if (result != 0)
                            continue;
                    }
                    // Rhino's main window starts minimized (run_live.py): shown once it's on the test
                    // desktop, not activated -- before, it would have shown on the user's desktop.
                    if (!s_shown && IsIconic(hwnd) && hwnd == Rhino.RhinoApp.MainWindowHandle())
                    {
                        s_shown = true;
                        ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.General, "test desktop: {0}", Log.Describe(ex));
            }
        }
    }
}
