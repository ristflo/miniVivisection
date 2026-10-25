using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Eto.Drawing;
using Eto.Forms;

namespace MiniVivisection
{
    /// <summary>
    /// A PowerShell prompt in a Script Editor tab: one PowerShell process per tab
    /// (<c>pwsh</c> when installed, else Windows PowerShell), started when the tab is
    /// first shown, in the open script's folder. Variables and the current location
    /// persist between inputs.
    ///
    /// The process reads commands from its standard input (<c>-Command -</c>). Each
    /// input goes over as one line -- the text Base64-encoded, run with
    /// <c>Invoke-Expression</c> in a dot-sourced block, all output streams formatted by
    /// <c>Out-String</c> --
    /// followed by a marker line with the new location, which ends the input. It's no
    /// console: programs that read the keyboard (a REPL, a password prompt) get the
    /// next inputs instead, so Ctrl+C ends the process; the next input starts a new one.
    /// Console style (TerminalView): the prompt shows the location, <c>cls</c> clears.
    /// </summary>
    internal sealed class PowerShellTerminal : Panel
    {
        private static readonly Regex s_ansi = new Regex(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07]*\x07|[@-Z\\-_])", RegexOptions.Compiled);

        private readonly object _editor;
        private readonly Func<string> _startFolder;
        private readonly TerminalView _view;
        private readonly StringBuilder _pending = new StringBuilder();
        private readonly string _marker = "\u0001minivivisection-done-" + Guid.NewGuid().ToString("N") + "\u0001";
        private readonly UITimer _flush;
        private Process _shell;

        // Every tab's shell, so Rhino's shutdown can end them (ShutdownAll).
        private static readonly List<Process> s_shells = new List<Process>();
        private string _shellName = "PowerShell";
        private string _location = "";
        private string _doneLocation;
        private bool _busy;
        private int? _exitCode;

        /// <param name="editor">The Script Editor it is in; null in Grasshopper's prompts window.</param>
        /// <param name="startFolder">Where it starts (null: the open script's folder).</param>
        public PowerShellTerminal(object editor, Func<string> startFolder = null)
        {
            _editor = editor;
            _startFolder = startFolder;
            _view = new TerminalView(editor, wrap: false) { InputLanguage = "powershell", InputColorsOn = () => MiniVivisectionSettings.ColorsPowerShell };
            _view.Submitted += Submit;
            _view.Interrupted += Stop;
            Content = _view;
            _flush = new UITimer { Interval = 0.05 };
            _flush.Elapsed += (sender, e) => Flush();
            _flush.Start();
            // The timer and the shell end with the editor (Dispose rarely comes for its tabs).
            EditorWindows.WhenClosed(editor, () =>
            {
                _flush.Stop();
                Kill();
            });
            _view.Write("PowerShell starts when this tab is shown.\n");
        }

        public void FocusInput()
        {
            EnsureShell();
            _view.FocusInput();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _flush.Stop();
                Kill();
            }
            base.Dispose(disposing);
        }

        private void Submit(string line)
        {
            var command = line.Trim();
            if (command == "cls" || command == "clear" || command.Equals("Clear-Host", StringComparison.OrdinalIgnoreCase))
            {
                _view.Clear();
                Prompt();
                return;
            }
            if (command.Length == 0 || !EnsureShell())
            {
                Prompt();
                return;
            }
            var code = Convert.ToBase64String(Encoding.UTF8.GetBytes(line));
            Send("$__minivivisectionCode = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + code + "')); " +
                 // dot-sourced: the global scope keeps variables and functions; the block's
                 // redirection catches errors, which Invoke-Expression's own would miss
                 "try { . { Invoke-Expression $__minivivisectionCode } *>&1 | Out-String -Stream -Width 200 } " +
                 "catch { $_ | Out-String -Stream -Width 200 }; " + MarkerCommand);
        }

        private void Prompt() => _view.Prompt("PS " + _location + "> ");

        private string MarkerCommand => "'" + _marker + "' + $PWD.Path";

        private bool EnsureShell()
        {
            if (_shell != null)
                return true;
            try
            {
                var exe = FindShell();
                _shellName = Path.GetFileNameWithoutExtension(exe).Equals("pwsh", StringComparison.OrdinalIgnoreCase) ? "PowerShell 7" : "Windows PowerShell";
                var info = new ProcessStartInfo(exe, "-NoLogo " + (MiniVivisectionSettings.PowerShellLoadProfile ? "" : "-NoProfile ") + "-NonInteractive -ExecutionPolicy Bypass -Command -")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), // a BOM would spoil the first command
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                    WorkingDirectory = StartDirectory(),
                };
                info.Environment["NO_COLOR"] = "1";
                var shell = new Process { StartInfo = info, EnableRaisingEvents = true };
                shell.OutputDataReceived += (sender, e) => OnOutput(e.Data);
                shell.ErrorDataReceived += (sender, e) => OnError(e.Data);
                shell.Exited += (sender, e) => OnExited(shell);
                shell.Start();
                lock (s_shells)
                {
                    s_shells.RemoveAll(p => { try { return p.HasExited; } catch (InvalidOperationException) { return true; } });
                    s_shells.Add(shell);
                }
                shell.BeginOutputReadLine();
                shell.BeginErrorReadLine();
                _shell = shell;
                _exitCode = null;
                UserLog.Info("{0} started ({1}).", _shellName, exe);
                Write(_shellName + " (experimental, miniVivisection) -- " + exe + "\n" +
                      "No console: programs that wait for keyboard input block the prompt; Ctrl+C ends the process.\n");
                Send("[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; " +
                     "$ProgressPreference = 'SilentlyContinue'; if ($PSStyle) { $PSStyle.OutputRendering = 'PlainText' }; " + MarkerCommand);
                return true;
            }
            catch (Exception ex)
            {
                Write("Can't start PowerShell: " + ex.Message + "\n");
                _shell = null;
                if (!_view.IsPrompting)
                    Prompt();
                return false;
            }
        }

        private void Send(string line)
        {
            try
            {
                _busy = true;
                _shell.StandardInput.WriteLine(line);
                _shell.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                _busy = false;
                Write("PowerShell didn't take the input: " + ex.Message + "\n");
                Kill();
                Prompt();
            }
        }

        // Output arrives on reader threads; the timer shows it on the UI thread.
        private void OnOutput(string line)
        {
            if (line == null)
                return;
            lock (_pending)
            {
                var marker = line.IndexOf(_marker, StringComparison.Ordinal);
                if (marker >= 0)
                {
                    if (marker > 0)
                        _pending.Append(s_ansi.Replace(line.Substring(0, marker), "")).Append('\n');
                    _doneLocation = line.Substring(marker + _marker.Length);
                }
                else
                {
                    _pending.Append(s_ansi.Replace(line, "")).Append('\n');
                }
            }
        }

        private void OnError(string line)
        {
            if (line == null)
                return;
            lock (_pending)
                _pending.Append(s_ansi.Replace(line, "")).Append('\n');
        }

        private void OnExited(Process shell)
        {
            lock (_pending)
            {
                try
                {
                    _exitCode = shell.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    _exitCode = -1;
                }
            }
        }

        private void Flush()
        {
            string text;
            string location;
            int? exitCode;
            lock (_pending)
            {
                text = _pending.ToString();
                _pending.Clear();
                location = _doneLocation;
                _doneLocation = null;
                exitCode = _exitCode;
                _exitCode = null;
            }
            if (text.Length > 0)
                Write(text);
            if (location != null)
            {
                _location = location;
                _busy = false;
                if (!_view.IsPrompting)
                    Prompt();
            }
            if (exitCode.HasValue)
            {
                Write(string.Format("[{0} ended, exit code {1}; the next input starts a new one]\n", _shellName, exitCode.Value));
                UserLog.Info("{0} ended (exit code {1}).", _shellName, exitCode.Value);
                _shell?.Dispose();
                _shell = null;
                _busy = false;
                if (!_view.IsPrompting)
                    Prompt();
            }
        }

        private void Stop()
        {
            if (_shell == null || !_busy)
            {
                if (_view.IsPrompting)
                    _view.Input = ""; // Ctrl+C at the prompt: a fresh line, as in PowerShell
                return;
            }
            Write("[stopped]\n");
            Kill();
        }

        /// <summary>Rhino shuts down: every tab's PowerShell process ends with it.</summary>
        public static int ShutdownAll()
        {
            List<Process> shells;
            lock (s_shells)
            {
                shells = s_shells.ToList();
                s_shells.Clear();
            }
            var ended = 0;
            foreach (var shell in shells)
            {
                try
                {
                    if (!shell.HasExited)
                    {
                        shell.Kill(entireProcessTree: true);
                        ended++;
                    }
                }
                catch (Exception)
                {
                    // ended meanwhile
                }
            }
            return ended;
        }

        private void Kill()
        {
            var shell = _shell;
            if (shell == null)
                return;
            try
            {
                if (!shell.HasExited)
                    shell.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // ended meanwhile
            }
        }

        private string StartDirectory()
        {
            try
            {
                var wanted = _startFolder?.Invoke();
                if (wanted != null && Directory.Exists(wanted))
                    return wanted;
                var path = _editor == null ? null : new ScriptEditorAccess(_editor).ScriptPath;
                var folder = path == null ? null : Path.GetDirectoryName(path);
                if (folder != null && Directory.Exists(folder))
                    return folder;
            }
            catch (Exception)
            {
                // no script open
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        /// <summary>The Options page's shell, else pwsh.exe on the PATH or in its default
        /// folder, else Windows PowerShell.</summary>
        private static string FindShell()
        {
            var set = MiniVivisectionSettings.PowerShellPath;
            if (!string.IsNullOrEmpty(set) && File.Exists(set))
                return set;
            var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Concat(new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7") });
            foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                try
                {
                    var candidate = Path.Combine(folder.Trim().Trim('"'), "pwsh.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        }

        private void Write(string text) => _view.Write(text);
    }
}
