using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;

namespace MiniVivisection
{
    /// <summary>
    /// Grasshopper's prompts window: V°Claude (GrasshopperChat), V°Python and V°PowerShell for
    /// the open definition, as tabs -- each one on or off in the Options' tab "Grasshopper
    /// prompts". Opened by its button on Grasshopper's toolbar (GrasshopperToolbar) or the
    /// command (miniVivisection GrasshopperPrompts). One window for the session: closing it
    /// hides it, so the chat, the Python namespace and the PowerShell process stay. With "The
    /// window stays with Grasshopper" it is owned by Grasshopper's window (above it, minimized
    /// with it) -- a Win32 owner, as Grasshopper is WinForms and the window is Eto.
    /// </summary>
    internal static class GrasshopperPrompts
    {
        private static Form s_window;
        private static TabControl s_tabs;
        private static TabPage s_claudePage, s_pythonPage, s_shellPage;
        private static GrasshopperChat s_claude;
        private static PythonTerminal s_python;
        private static PowerShellTerminal s_shell;
        private static bool s_closing;

        internal static GrasshopperChat Claude => s_claude;
        internal static PythonTerminal Python => s_python;
        internal static PowerShellTerminal Shell => s_shell;
        internal static Form Window => s_window;

        /// <summary>The tabs shown now, by title (for tests).</summary>
        internal static string[] TabTitles => s_tabs?.Pages.Select(p => p.Text).ToArray() ?? new string[0];

        /// <summary>Opens (or brings back) the window; <paramref name="tab"/>: "claude", "python" or
        /// "powershell" to show that tab (UI thread).</summary>
        public static void Show(string tab = null)
        {
            if (!MiniVivisectionSettings.PluginOn && !MiniVivisectionSettings.AiOn)
            {
                UserLog.Info("The prompts window: the V Debug and V AI switches are off.");
                return;
            }
            if (!MiniVivisectionSettings.GhPromptsAny)
            {
                UserLog.Info("The prompts window has no tab on: turn one on in Tools > Options > miniVivisection, tab Grasshopper, The prompts window" +
                             (MiniVivisectionSettings.AiOn ? "." : " (V Claude also needs the V AI switch on)."));
                return;
            }
            Ensure();
            ApplyTabs();
            if (!s_window.Visible)
                s_window.Show();
            Own(); // at once: Shown may come later (it does while the session is connected), and Grasshopper's window may be new
            s_window.BringToFront();
            var page = tab == "claude" ? s_claudePage : tab == "python" ? s_pythonPage : tab == "powershell" ? s_shellPage : null;
            if (page != null && s_tabs.Pages.Contains(page))
                s_tabs.SelectedPage = page;
            FocusSelected();
        }

        private static void Ensure()
        {
            if (s_window != null)
                return;
            s_tabs = new TabControl();
            s_tabs.SelectedIndexChanged += (s, e) => FocusSelected();
            s_window = new Form
            {
                Title = VNames.Text("miniVivisection -- prompts for Grasshopper"),
                ClientSize = new Size(780, 560),
                MinimumSize = new Size(420, 260),
                Resizable = true,
                Minimizable = true,
                Maximizable = true,
                ShowInTaskbar = !MiniVivisectionSettings.GhWindowOwned,
                Content = s_tabs,
            };
            s_window.Closing += (s, e) =>
            {
                if (s_closing)
                    return;
                e.Cancel = true; // hidden: its chat, namespace and shell stay
                s_window.Visible = false;
            };
            s_window.Shown += (s, e) => Own();
            Log.Info(LogArea.Tabs, "Grasshopper prompts window made");
        }

        /// <summary>The tabs as the settings say (UI thread); the window closes when none is left.</summary>
        public static void ApplyTabs()
        {
            if (s_window == null)
                return;
            try
            {
                Sync(ref s_claudePage, MiniVivisectionSettings.GhPromptClaude, "V Claude", 0, () => s_claude = s_claude ?? new GrasshopperChat());
                Sync(ref s_pythonPage, MiniVivisectionSettings.GhPromptPython, "V Python", 1,
                    () => s_python = s_python ?? new PythonTerminal(null, MiniVivisectionPlugin.Instance.Hook));
                Sync(ref s_shellPage, MiniVivisectionSettings.GhPromptPowerShell, "V PowerShell", 2, () => s_shell = s_shell ?? new PowerShellTerminal(null, ShellFolder));
                if (s_tabs.Pages.Count == 0 && s_window.Visible)
                    s_window.Visible = false;
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Tabs, "Grasshopper prompts window tabs: {0}", Log.Describe(ex));
            }
        }

        private static void Sync(ref TabPage page, bool wanted, string title, int order, Func<Control> content)
        {
            if (wanted && page == null)
            {
                page = new TabPage { Text = VNames.Text(title), Content = content(), Tag = order };
                var at = s_tabs.Pages.Count(p => (int)p.Tag < order);
                s_tabs.Pages.Insert(at, page);
            }
            else if (!wanted && page != null)
            {
                s_tabs.Pages.Remove(page);
                page.Content = null; // the control is kept (s_claude ...) for when it comes back
                page = null;
            }
        }

        private static void FocusSelected()
        {
            try
            {
                var content = s_tabs?.SelectedPage?.Content;
                if (content is GrasshopperChat chat)
                    chat.FocusInput();
                else if (content is PythonTerminal python)
                    python.FocusInput();
                else if (content is PowerShellTerminal shell)
                    shell.FocusInput(); // starts PowerShell the first time
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "Grasshopper prompts window focus: {0}", Log.Describe(ex));
            }
        }

        /// <summary>V°PowerShell's start: the definition's folder (Options), else Documents.</summary>
        private static string ShellFolder()
        {
            if (MiniVivisectionSettings.GhPowerShellFolder)
            {
                try
                {
                    var path = GrasshopperBridge.DocumentPath();
                    var folder = path == null ? null : System.IO.Path.GetDirectoryName(path);
                    if (folder != null && System.IO.Directory.Exists(folder))
                        return folder;
                }
                catch (Exception)
                {
                    // Grasshopper not loaded
                }
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        // --- owned by Grasshopper's window ------------------------------------------------------------

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        private const int GwlpHwndParent = -8;

        /// <summary>The window's owner: Grasshopper's (its Handle, by reflection: WinForms).</summary>
        private static void Own()
        {
            if (!MiniVivisectionSettings.GhWindowOwned)
                return;
            try
            {
                var grasshopper = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Grasshopper");
                var editor = grasshopper?.GetType("Grasshopper.Instances")?.GetProperty("DocumentEditor", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var owner = editor?.GetType().GetProperty("Handle")?.GetValue(editor) as IntPtr? ?? IntPtr.Zero;
                var handle = s_window.NativeHandle;
                if (owner != IntPtr.Zero && handle != IntPtr.Zero)
                    SetWindowLongPtr(handle, GwlpHwndParent, owner);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "Grasshopper prompts window owner: {0}", Log.Describe(ex));
            }
        }

        /// <summary>Rhino shuts down: the answer stops, the window goes (PowerShell ends with ShutdownAll).</summary>
        public static void Shutdown()
        {
            try
            {
                s_claude?.Shutdown();
                if (s_window != null)
                {
                    s_closing = true;
                    s_window.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "Grasshopper prompts window at shutdown: {0}", Log.Describe(ex));
            }
        }
    }
}
