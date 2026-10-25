using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.PlugIns;

namespace MiniVivisection
{
    /// <summary>
    /// Offers to install RhinoAI (McNeel's Rhino MCP Platform: Claude working in the Rhino
    /// model from V Claude) when it isn't installed. Checked once Rhino has finished
    /// starting -- its first idle moment, plus a few seconds -- so plug-ins loading after
    /// this one count: RhinoAI known to Rhino (PlugInExists: installed, loaded or not), or
    /// its package already in Rhino's packages folder (installed, waiting for a restart).
    /// Installing is RhinoAI's own way: Rhino's Yak.exe install Rhino-MCP-Platform, from
    /// McNeel's package server; it loads at the next start. Asked once per session, only
    /// while V AI is on. Options: "Offer to install RhinoAI" (Don't ask again turns it off).
    /// </summary>
    internal static class RhinoAiInstaller
    {
        public const string PackageName = "Rhino-MCP-Platform";
        private static readonly Guid PlugInId = new Guid("2668d7ed-f507-4a68-8295-8172147a0e39"); // RhinoAI (RhinoAiServer)
        private static bool s_started;
        private static bool s_asked;

        /// <summary>At load: the check waits for Rhino to finish starting.</summary>
        public static void Start()
        {
            if (s_started)
                return;
            s_started = true;
            RhinoApp.Idle += OnFirstIdle;
        }

        private static void OnFirstIdle(object sender, EventArgs e)
        {
            RhinoApp.Idle -= OnFirstIdle;
            // A few seconds more: plug-ins that load at startup after this one, and Rhino's
            // own start-up work, are done by then.
            var timer = new UITimer { Interval = 5 };
            timer.Elapsed += (s, a) =>
            {
                timer.Stop();
                Check();
            };
            timer.Start();
        }

        /// <summary>Whether RhinoAI is installed: known to Rhino, or its package waiting for a restart.</summary>
        internal static bool IsInstalled(out string how)
        {
            if (PlugIn.PlugInExists(PlugInId, out var loaded, out var loadProtected))
            {
                how = loaded ? "loaded" : loadProtected ? "installed, but disabled (Options > Plug-ins)" : "installed";
                return true;
            }
            var packages = PackagesFolder();
            if (packages != null && Directory.Exists(packages) && Directory.EnumerateDirectories(packages).Any())
            {
                how = "installed, loads at the next start of Rhino";
                return true;
            }
            how = "not installed";
            return false;
        }

        private static string PackagesFolder() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McNeel", "Rhinoceros", "packages",
                RhinoApp.Version.Major + ".0", PackageName);

        private static void Check()
        {
            try
            {
                var installed = IsInstalled(out var how);
                Log.Info(LogArea.Startup, "RhinoAI: {0}", how);
                if (installed || s_asked || !MiniVivisectionSettings.AiOn || !MiniVivisectionSettings.OfferRhinoAi)
                    return;
                s_asked = true;
                switch (Ask())
                {
                    case Answer.Install:
                        Install();
                        break;
                    case Answer.Never:
                        MiniVivisectionSettings.Set("OfferRhinoAi", false);
                        UserLog.Info("RhinoAI: not offered again (V Settings > Claude: \"Offer to install RhinoAI\").");
                        break;
                    default:
                        UserLog.Info("RhinoAI: not installed now -- the next start of Rhino offers it again.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Startup, "RhinoAI check: {0}", Log.Describe(ex));
            }
        }

        private enum Answer
        {
            Later,
            Install,
            Never,
        }

        private static Answer Ask()
        {
            var answer = Answer.Later;
            var dialog = new Dialog
            {
                Title = "miniVivisection: install RhinoAI?",
                Padding = new Padding(14),
                Resizable = false,
            };
            Button Choice(string text, Answer value) => new Button((s, e) =>
            {
                answer = value;
                dialog.Close();
            }) { Text = text };
            var install = Choice("Install", Answer.Install);
            var later = Choice("Not now", Answer.Later);
            var never = Choice("Don't ask again", Answer.Never);
            dialog.DefaultButton = install;
            dialog.AbortButton = later;
            dialog.Content = new TableLayout
            {
                Spacing = new Size(8, 12),
                Rows =
                {
                    new Label
                    {
                        Text = VNames.Text("RhinoAI isn't installed in this Rhino.\n\n" +
                            "With it, Claude in V Claude can also work in the Rhino model: read and change objects,\n" +
                            "run commands and scripts. Without it, Claude still works on the Script Editor and the paused script.\n\n" +
                            "Install installs RhinoAI's package (" + PackageName + ") from McNeel's package server, as\n" +
                            "Rhino's Package Manager does. It loads at the next start of Rhino."),
                        Wrap = WrapMode.None,
                    },
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        HorizontalContentAlignment = HorizontalAlignment.Right,
                        Items = { null, never, later, install },
                    },
                },
            };
            dialog.ShowModal(RhinoEtoApp());
            return answer;
        }

        private static Control RhinoEtoApp() => Rhino.UI.RhinoEtoApp.MainWindow;

        /// <summary>Rhino's Yak.exe install Rhino-MCP-Platform, in the background; the
        /// outcome on the command line and in V Log.</summary>
        private static void Install()
        {
            var yak = Path.Combine(RhinoApp.GetExecutableDirectory().FullName, "Yak.exe");
            if (!File.Exists(yak))
            {
                UserLog.Warning("RhinoAI: Rhino's Yak.exe isn't there ({0}) -- install it with the PackageManager command: search for {1}.", yak, PackageName);
                return;
            }
            RhinoApp.WriteLine("miniVivisection: installing RhinoAI ({0}) ...", PackageName);
            UserLog.Info("RhinoAI: installing {0} ...", PackageName);
            Task.Run(() =>
            {
                string output;
                int exitCode;
                try
                {
                    using (var process = Process.Start(new ProcessStartInfo(yak, "install " + PackageName)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    }))
                    {
                        var stdout = process.StandardOutput.ReadToEndAsync();
                        var stderr = process.StandardError.ReadToEndAsync();
                        if (!process.WaitForExit(180000))
                        {
                            try
                            {
                                process.Kill();
                            }
                            catch (Exception)
                            {
                                // gone already
                            }
                            output = "it took more than 3 minutes";
                            exitCode = -1;
                        }
                        else
                        {
                            output = (stdout.Result + "\n" + stderr.Result).Trim();
                            exitCode = process.ExitCode;
                        }
                    }
                }
                catch (Exception ex)
                {
                    output = ex.Message;
                    exitCode = -1;
                }
                RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    Log.Info(LogArea.Startup, "Yak install {0}: exit {1}\n{2}", PackageName, exitCode, output);
                    if (exitCode == 0)
                    {
                        RhinoApp.WriteLine("miniVivisection: RhinoAI is installed -- restart Rhino to load it.");
                        UserLog.Success("RhinoAI is installed: restart Rhino to load it.");
                    }
                    else
                    {
                        var last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "no output";
                        RhinoApp.WriteLine("miniVivisection: RhinoAI couldn't be installed ({0}). Try the PackageManager command: search for {1}.", last, PackageName);
                        UserLog.Warning("RhinoAI couldn't be installed: {0} -- try the PackageManager command and search for {1}.", last, PackageName);
                    }
                }));
            });
        }
    }
}
