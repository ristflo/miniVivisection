using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Eto.Forms;
using Harmony = HarmonyLib.Harmony;
using HarmonyMethod = HarmonyLib.HarmonyMethod;
using Rhino;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// A Grasshopper script component that takes its script from its "script" input -- the text
    /// of a Panel, or a file's path -- has no script editor of its own (Grasshopper disables
    /// it). Its script opens in the Script Editor instead, as a file:
    /// - a path: that file -- saving it saves the file, and the component recomputes;
    /// - a Panel's text: a working copy (%TEMP%\rhinovivisection\minivivisection\gh-inputs) --
    ///   saving it writes the text back into the Panel, which recomputes the component;
    /// - text from anywhere else: a copy to read; edits can't go back (V°Log says so).
    /// From the component's menu ("miniVivisection: Edit the script input"), and by itself after
    /// Ctrl+Alt+B stopped it -- at the line where it stopped, with the explanation.
    /// </summary>
    internal static class GrasshopperInputScripts
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private sealed class Followed
        {
            public WeakReference<object> Component;
            public WeakReference<object> Panel; // null: the file is the script's own
            public FileSystemWatcher Watcher;
            public DateTime Pending;
            public string Written;              // the Panel's text the file was made from ("\n" line breaks)
        }

        private static readonly Dictionary<string, Followed> s_followed = new Dictionary<string, Followed>(StringComparer.OrdinalIgnoreCase);

        internal static string Folder => Path.Combine(Path.GetTempPath(), "rhinovivisection", "minivivisection", "gh-inputs");

        /// <summary>Whether the component runs the script of its "script" input.</summary>
        public static bool UsesInputScript(object component) =>
            component?.GetType().GetProperty("UsingScriptInputParam", Any)?.GetValue(component) as bool? == true;

        /// <summary>The input script in the Script Editor (UI thread); after a stop, at its line
        /// with the explanation (<paramref name="record"/>).</summary>
        public static void Open(object component, StoppedRuns.Record record = null, Action<Window, Rhino.Runtime.Code.Code> then = null)
        {
            try
            {
                var (path, text, panel, from) = Locate(component);
                if (path == null)
                {
                    UserLog.Warning("'{0}': its script input has no script to open ({1}).", GrasshopperRuns.NickName(component), from);
                    return;
                }
                var rewritten = text != null && WriteWorkingCopy(path, text, "'" + GrasshopperRuns.NickName(component) + "'");
                if (from != null)
                    Follow(path, component, panel, text);
                // Stopped: a Panel's script edited right there, at the line (miniVivisection.GH's
                // ScriptPanels.EditAtLine) -- the Script Editor when the Panel can't show it.
                if (record != null && panel != null && EditPanelAtLine(panel, record))
                {
                    UserLog.Info("'{0}': stopped at line {1} -- the Panel '{2}' is open for editing at that line (click elsewhere to keep your changes, Esc to drop them; right-click the Panel > Edit the script in the Script Editor for more).",
                        GrasshopperRuns.NickName(component), record.Line, NickName(panel));
                    return;
                }
                RhinoApp.RunScript("_-ScriptEditor _Open \"" + path + "\"", false);
                WhenOpen(path, (window, code) =>
                {
                    PatchRun(window); // Run and Debug there: in Grasshopper
                    WatchWindow(window); // closed (hidden): what it has open, saved
                    if (rewritten)
                        ShowText(code, text); // the Panel's text now, not an older tab's
                    GiveParams(component, code); // the component's inputs and outputs: the editor knows them
                    if (record != null)
                        StoppedRuns.ShowOverlay(window, code, record);
                    then?.Invoke(window, code);
                });
                UserLog.Info(from == null
                    ? "'{0}': its script input is text from another component -- a copy is open in the Script Editor; edits there can't go back ({1})."
                    : "'{0}': its script input is open in the Script Editor ({1}) -- saving it, or closing the Script Editor, {2}, and the component recomputes.",
                    GrasshopperRuns.NickName(component), path, panel != null ? "writes it back into the Panel '" + NickName(panel) + "'" : "saves the file");
            }
            catch (Exception ex)
            {
                UserLog.Warning("'{0}': its script input couldn't be opened: {1}", GrasshopperRuns.NickName(component), Log.Describe(ex));
            }
        }

        // The .gha's in-place editor for a script Panel at the stopped line, the explanation over it
        // (StoppedRuns.PanelOverlay) -- by reflection (the .rhp doesn't reference the .gha).
        private static bool EditPanelAtLine(object panel, StoppedRuns.Record record)
        {
            try
            {
                var edit = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection.GH")
                    ?.GetType("MiniVivisection.GH.ScriptPanels")
                    ?.GetMethod("EditAtLine", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var (title, lines) = StoppedRuns.PanelOverlay(record);
                return edit?.Invoke(null, new object[] { panel, record.Line, title, lines }) as bool? == true;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "the Panel edited at the line: {0}", Log.Describe(ex));
                return false;
            }
        }

        /// <summary>A Panel's script -- a script Panel double-clicked, or its menu (miniVivisection.GH's
        /// ScriptPanels, by reflection) -- in the Script Editor as a working file, the same one as
        /// when the Panel feeds a script input; saving writes it back into the Panel. Python or C#
        /// by <paramref name="extension"/> (".py", ".cs"). UI thread.</summary>
        internal static void OpenPanel(object panel, string extension)
        {
            // A Panel feeding a script component's script input: the component's script -- run in
            // Grasshopper, with its inputs (RunPrefix), not as a Rhino script without them
            if (extension != ".md" && FedComponent(panel) is object component)
            {
                Open(component);
                return;
            }
            try
            {
                var text = panel.GetType().GetProperty("UserText")?.GetValue(panel) as string ?? "";
                var id = (panel.GetType().GetProperty("InstanceGuid")?.GetValue(panel) as Guid?)?.ToString("N").Substring(0, 8) ?? "panel";
                var path = Path.Combine(Folder, Safe(NickName(panel)) + "-" + id + (extension == ".cs" || extension == ".md" ? extension : ".py"));
                var rewritten = WriteWorkingCopy(path, text, "Panel '" + NickName(panel) + "'");
                Follow(path, panel, panel, text); // a Panel on its own: it is its own "component"
                RhinoApp.RunScript("_-ScriptEditor _Open \"" + path + "\"", false);
                WhenOpen(path, (window, code) =>
                {
                    WatchWindow(window);
                    if (rewritten)
                        ShowText(code, text);
                });
                if (extension == ".md")
                    ShowMarkdownSource(path);
                UserLog.Info("Panel '{0}': its {1} is open in the Script Editor ({2}) -- saving it, or closing the Script Editor, writes it back into the Panel.", NickName(panel),
                    extension == ".md" ? "Markdown" : "script", path);
            }
            catch (Exception ex)
            {
                UserLog.Warning("Panel '{0}': its script couldn't be opened: {1}", NickName(panel), Log.Describe(ex));
            }
        }

        /// <summary>The script component whose script input <paramref name="panel"/> feeds; null: none.</summary>
        internal static object FedComponent(object panel)
        {
            var recipients = panel?.GetType().GetProperty("Recipients")?.GetValue(panel) as IEnumerable;
            foreach (var param in recipients?.Cast<object>() ?? Enumerable.Empty<object>())
            {
                var attributes = param.GetType().GetProperty("Attributes")?.GetValue(param);
                var top = attributes?.GetType().GetProperty("GetTopLevel")?.GetValue(attributes);
                var owner = top?.GetType().GetProperty("DocObject")?.GetValue(top);
                if (owner != null && UsesInputScript(owner) && ReferenceEquals(InputParam(owner), param))
                    return owner;
            }
            return null;
        }

        // --- the Script Editor's Run and Debug on a script input's file ----------------------------

        private static Harmony s_runHarmony;

        /// <summary>The Script Editor's Run and Debug (CodeRunCommand, CodeRunDebugCommand) on a
        /// script input's working file: the component computes it in Grasshopper -- its inputs
        /// (x, y, ghenv) given, as Grasshopper runs it -- the editor's text taken into the Panel
        /// first; Debug in the debugger (DebugOnce). Run as a Rhino script, it would have none of
        /// them. Patched once, when such a script is opened.</summary>
        private static void PatchRun(object editor)
        {
            if (s_runHarmony != null || editor == null)
                return;
            try
            {
                var harmony = new Harmony("minivivisection.inputscriptrun");
                foreach (var name in new[] { "CodeRunCommand", "CodeRunDebugCommand" })
                {
                    var type = editor.GetType().Assembly.GetType("RhinoCodeEditor.Editor.Commands." + name, throwOnError: true);
                    var execute = type.GetMethod("ExecuteWith", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                  ?? throw new MissingMethodException(type.FullName, "ExecuteWith");
                    harmony.Patch(execute, prefix: new HarmonyMethod(typeof(GrasshopperInputScripts), name == "CodeRunCommand" ? nameof(RunPrefix) : nameof(DebugPrefix)));
                }
                s_runHarmony = harmony;
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Breakpoints, "Grasshopper: Run of a script input in Grasshopper: {0}", Log.Describe(ex));
            }
        }

        private static bool RunPrefix(object[] __args, ref System.Threading.Tasks.Task __result) => !InGrasshopper(__args, debug: false, ref __result);

        private static bool DebugPrefix(object[] __args, ref System.Threading.Tasks.Task __result) => !InGrasshopper(__args, debug: true, ref __result);

        // The editor's code a script input's file: taken into the Panel, the component computed (in the debugger)
        private static bool InGrasshopper(object[] args, bool debug, ref System.Threading.Tasks.Task result)
        {
            try
            {
                var window = args.Length > 0 ? args[0] as Window : null;
                var code = window == null ? null : new ScriptEditorAccess(window).Code;
                var path = code?.Uri?.IsFile == true ? code.Uri.LocalPath : null;
                Followed followed = null;
                lock (s_followed)
                {
                    if (path == null || !s_followed.TryGetValue(path, out followed))
                        return false;
                }
                if (!followed.Component.TryGetTarget(out var component) || !UsesInputScript(component) || GrasshopperRuns.DocumentOf(component) == null)
                    return false; // a Panel's script on its own: a Rhino script
                // The editor's text, as Grasshopper will run it: into the file and the Panel
                var text = (string)code.Text;
                if (text != null && Normalize(File.Exists(path) ? File.ReadAllText(path) : "") != Normalize(text))
                {
                    File.WriteAllText(path, text);
                    Apply(path, followed);
                }
                if (debug)
                    DebugOnce(component);
                else
                    Expire(component);
                UserLog.Info("'{0}': {1} in Grasshopper, with its inputs.", GrasshopperRuns.NickName(component), debug ? "debugged" : "computed");
                result = System.Threading.Tasks.Task.CompletedTask;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Breakpoints, "Grasshopper: Run of a script input: {0}", Log.Describe(ex));
                return false;
            }
        }

        /// <summary>The Script Editor shows a Markdown file rendered -- a preview, which can't be edited;
        /// its "Show Markdown Editor" (RCE.Codes.Editors.ShowEditContext) shows the source. Done for
        /// the Panel's file once the editor has it open (it opens asynchronously; up to 10 s).</summary>
        private static void ShowMarkdownSource(string path)
        {
            var timer = new UITimer { Interval = 0.2 };
            var tries = 0;
            timer.Elapsed += (s, e) =>
            {
                if (++tries > 50)
                {
                    timer.Stop();
                    Log.Debug(LogArea.Tabs, "the Panel's Markdown: no editor showed {0}", path);
                    return;
                }
                try
                {
                    foreach (var window in Application.Instance.Windows.Where(w => w.GetType().FullName == "RhinoCodeEditor.Editor.RCE"))
                    {
                        if (!string.Equals(new ScriptEditorAccess(window).ScriptPath, path, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var codes = window.GetType().GetProperty("Codes", Any)?.GetValue(window);
                        var editors = codes?.GetType().GetProperty("Editors", Any)?.GetValue(codes);
                        var show = editors?.GetType().GetMethod("ShowEditContext", Any, null, Type.EmptyTypes, null);
                        if (show == null)
                            continue;
                        show.Invoke(editors, null);
                        timer.Stop();
                        Log.Info(LogArea.Tabs, "the Panel's Markdown: its source shown for editing ({0})", path);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    timer.Stop();
                    Log.Debug(LogArea.Tabs, "the Panel's Markdown, its source: {0}", Log.Describe(ex));
                }
            };
            timer.Start();
        }

        /// <summary>The file to open, the Panel it comes from (null: a file of its own, or a
        /// copy), and where it comes from (null: a copy that can't go back).</summary>
        /// <summary>Where the component's input script is, without writing anything: the file
        /// to open, the text Grasshopper runs (null: a file of its own -- the file is the text),
        /// the Panel it comes from, and where it comes from (null: a copy that can't go back; a
        /// null path: nothing to open, From says why).</summary>
        private static (string Path, string Text, object Panel, string From) Locate(object component)
        {
            var input = InputParam(component) ?? throw new InvalidOperationException("no script input");
            var sources = (input.GetType().GetProperty("Sources")?.GetValue(input) as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
            var source = sources.FirstOrDefault();
            if (source == null)
            {
                // Nothing connected: the input's own data -- a file set on it (internalized).
                var own = ScriptFileOf(input);
                return own != null ? (own, null, null, "the file") : (null, null, null, "nothing connected, and no file set on it -- it holds " + Describe(input));
            }
            var panel = source.GetType().FullName == "Grasshopper.Kernel.Special.GH_Panel" ? source : null;
            var text = panel != null ? source.GetType().GetProperty("UserText")?.GetValue(source) as string : FirstText(source);
            if (text == null)
                return (null, null, null, "no text from " + NickName(source));
            // The input's "Treat input as path": the text names a file (as Grasshopper resolved it
            // -- relative to the definition, too); otherwise it is the script, also when it
            // looks like a path.
            if (input.GetType().GetProperty("TreatInputAsPath", Any)?.GetValue(input) as bool? == true)
            {
                var file = ScriptFileOf(input);
                return file != null ? (file, null, null, "the file") : (null, null, null, "its path doesn't name a file: " + text.Trim());
            }
            var owner = panel ?? component;
            var id = (owner.GetType().GetProperty("InstanceGuid")?.GetValue(owner) as Guid?)?.ToString("N").Substring(0, 8) ?? "script";
            // The file in the component's language (as the Panel's menu names it: one file per Panel)
            var extension = component.GetType().Name.IndexOf("CSharp", StringComparison.OrdinalIgnoreCase) >= 0 ? ".cs" : ".py";
            return (Path.Combine(Folder, Safe(NickName(owner)) + "-" + id + extension), text, panel, panel != null ? "the Panel" : null);
        }

        /// <summary>The working copy of a Panel's text: written only when it differs from what the
        /// file holds -- a file written anew makes the Script Editor read it again, dropping changes
        /// not saved in its tab. When the editor has such changes and the text is a new one (the
        /// Panel changed meanwhile), they are kept in a file of their own first, and the user told.
        /// Whether the file was written. <paramref name="whose"/> names it in the message.</summary>
        private static bool WriteWorkingCopy(string path, string text, string whose)
        {
            Directory.CreateDirectory(Folder);
            var onDisk = File.Exists(path) ? File.ReadAllText(path) : null;
            if (onDisk != null && Normalize(onDisk) == Normalize(text))
                return false; // as it is: an editor showing it keeps its tab, with what isn't saved yet
            if (onDisk != null)
            {
                foreach (var (_, code) in OpenCodes().Where(c => Shows(c.Code, path)).ToList())
                {
                    var shown = (string)code.Text ?? "";
                    if (Normalize(shown) == Normalize(onDisk) || Normalize(shown) == Normalize(text))
                        continue;
                    var kept = Path.Combine(Folder, Path.GetFileNameWithoutExtension(path) + UnsavedMark + DateTime.Now.ToString("yyyyMMdd-HHmmss") + Path.GetExtension(path));
                    File.WriteAllText(kept, shown);
                    UserLog.Warning("{0} changed while the Script Editor had changes to its script that weren't saved: the editor shows its text now; your changes are kept in {1}.", whose, kept);
                }
            }
            File.WriteAllText(path, text);
            return true;
        }

        // The name part of a file keeping an editor's changes that weren't saved (WriteWorkingCopy); kept at Shutdown
        private const string UnsavedMark = ".unsaved-";

        /// <summary>The editor's tab shows <paramref name="text"/> (the editor reads a file written
        /// anew by itself; this makes sure of it, as for a tab it kept from before).</summary>
        private static void ShowText(Rhino.Runtime.Code.Code code, string text)
        {
            try
            {
                if (code != null && !code.Text.IsReadOnly && Normalize((string)code.Text) != Normalize(text))
                {
                    code.Text.Set(text);
                    Log.Info(LogArea.Breakpoints, "Grasshopper script input: the editor's tab showed older text; it shows {0} now", code.Uri?.LocalPath);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper script input, the editor's text: {0}", Log.Describe(ex));
            }
        }

        // --- the Script Editor closed: its scripts saved --------------------------------------------

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> s_watchedWindows =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();

        /// <summary>Every script open in a Script Editor window (each tab, not only the active one).</summary>
        private static List<(Window Window, Rhino.Runtime.Code.Code Code)> OpenCodes()
        {
            var found = new List<(Window, Rhino.Runtime.Code.Code)>();
            foreach (var window in Application.Instance.Windows.Where(w => w.GetType().FullName == "RhinoCodeEditor.Editor.RCE"))
                found.AddRange(CodesOf(window).Select(code => (window, code)));
            return found;
        }

        private static List<Rhino.Runtime.Code.Code> CodesOf(object window)
        {
            try
            {
                var state = window.GetType().GetProperty("ActiveState", Any)?.GetValue(window);
                var codes = state?.GetType().GetProperty("Codes", Any)?.GetValue(state) as IEnumerable;
                return codes?.Cast<object>().Select(c => c?.GetType().GetProperty("Code", Any)?.GetValue(c) as Rhino.Runtime.Code.Code)
                    .Where(c => c != null).ToList() ?? new List<Rhino.Runtime.Code.Code>();
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "the Script Editor's open scripts: {0}", Log.Describe(ex));
                return new List<Rhino.Runtime.Code.Code>();
            }
        }

        private static bool Shows(Rhino.Runtime.Code.Code code, string path) =>
            code?.Uri != null && code.Uri.IsFile && string.Equals(code.Uri.LocalPath, path, StringComparison.OrdinalIgnoreCase);

        /// <summary>A Script Editor window showing a script input or a Panel's script: when it is
        /// closed -- Grasshopper's editors only hide then (HideOnClose), keeping their tabs and what
        /// isn't saved -- its followed scripts are saved, as Ctrl+S would: back into the Panel, or
        /// the file, and the component recomputes (Follow's watcher). Once per window.</summary>
        private static void WatchWindow(Window window)
        {
            if (window == null || s_watchedWindows.TryGetValue(window, out _))
                return;
            s_watchedWindows.Add(window, null);
            try
            {
                window.Closing += (s, e) => SaveFollowed(window);
                var hidden = window.GetType().GetEvent("HideOnClosed", Any);
                if (hidden != null)
                {
                    var handler = typeof(GrasshopperInputScripts).GetMethod(nameof(OnHidden), BindingFlags.NonPublic | BindingFlags.Static);
                    hidden.AddEventHandler(window, Delegate.CreateDelegate(hidden.EventHandlerType, handler));
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Breakpoints, "Grasshopper script inputs: the Script Editor's closing not followed: {0}", Log.Describe(ex));
            }
        }

        private static void OnHidden(object sender, EventArgs e) => SaveFollowed(sender as Window);

        /// <summary>The window's scripts that are followed (a script input's file, a Panel's working
        /// copy) and differ from their file: written to it. UI thread.</summary>
        private static void SaveFollowed(Window window)
        {
            if (window == null)
                return;
            foreach (var code in CodesOf(window))
            {
                try
                {
                    var path = code.Uri?.IsFile == true ? code.Uri.LocalPath : null;
                    lock (s_followed)
                    {
                        if (path == null || !s_followed.ContainsKey(path))
                            continue;
                    }
                    var shown = (string)code.Text;
                    if (shown == null || (File.Exists(path) && Normalize(File.ReadAllText(path)) == Normalize(shown)))
                        continue;
                    File.WriteAllText(path, shown); // Follow's watcher takes it: the Panel, or the component recomputed
                    Log.Info(LogArea.Breakpoints, "the Script Editor closed with changes not saved to {0}: saved", path);
                }
                catch (Exception ex)
                {
                    Log.Warning(LogArea.Breakpoints, "the Script Editor closed: {0} not saved: {1}", code.Uri, Log.Describe(ex));
                }
            }
        }

        // --- debugging the input script ---------------------------------------------------------

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> s_debugOnce =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();
        private static readonly object FirstLine = new object(); // s_debugOnce's value: break at the first line

        private static readonly HashSet<string> s_reportedStale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The menu's "Debug the script input": opened, then recomputed in the debugger,
        /// pausing at its first line unless it has breakpoints (UI thread).</summary>
        public static void DebugOnce(object component)
        {
            Open(component, null, (window, code) =>
            {
                if (!code.HasDebugControls)
                {
                    UserLog.Warning("'{0}': the Script Editor has no debugger for its script input.", GrasshopperRuns.NickName(component));
                    return;
                }
                // Without breakpoints: a break at its first line -- asked for in the run itself
                // (TryDebugRun), after the debugger's reset at its start, which would drop it
                var breakpoints = code.DebugControls.Breakpoints.WhereReferences(code).Count();
                s_debugOnce.AddOrUpdate(component, breakpoints == 0 ? FirstLine : null);
                Log.Info(LogArea.Breakpoints, "Grasshopper: '{0}' -- debug its script input once ({1} breakpoint(s) in {2})", GrasshopperRuns.NickName(component), breakpoints, code.Uri);
                Expire(component);
            });
        }

        /// <summary>
        /// A run of a script-input component, in the debugger -- under Always debug, or once
        /// from the menu -- when its script is open in a Script Editor: Grasshopper runs the
        /// input's script plainly (a RunContext of its own) on a code object nobody shows; this
        /// runs the Script Editor's code for the same file instead, with a debug context from the
        /// component's script context (PrepareContext(code, Debug)), as Grasshopper's own run does
        /// otherwise (ghenv, ExecCode). Its breakpoints pause, Ctrl+Alt+B pauses, steps work.
        /// Only when the file holds what Grasshopper would run (a Panel's working copy as the
        /// Panel says). False: run as Grasshopper does.
        /// </summary>
        internal static bool TryDebugRun(object component, object da)
        {
            if (!UsesInputScript(component))
                return false;
            // From the menu ("Debug the script input"), or every run under Always debug. (The
            // runs failed at a docstring line once: the Script Editor's code had no parameters,
            // so the script's first line found no input -- given now, as Grasshopper gives them.)
            var once = s_debugOnce.TryGetValue(component, out var onceHow);
            if (!once && MiniVivisectionSettings.GrasshopperScripts != "Always debug")
                return false;
            var (path, text, _, _) = Locate(component);
            if (path == null)
                return Skip("no file for its script input");
            var (_, code) = EditorCodeFor(path);
            if (code == null || !code.HasDebugControls)
                return Skip(code == null ? "its script isn't open in a Script Editor: " + path : "the Script Editor's code has no debugger");
            if (text != null)
            {
                string onDisk;
                try
                {
                    onDisk = File.ReadAllText(path);
                }
                catch (Exception)
                {
                    return false;
                }
                if (Normalize(onDisk) != Normalize(text))
                {
                    if (s_reportedStale.Add(path))
                        UserLog.Info("'{0}': its script input changed in the Panel since it was opened -- runs plainly; open it again (right-click > " + VNames.Menu("Edit the script input in the Script Editor") + ") to debug it.",
                            GrasshopperRuns.NickName(component));
                    return false;
                }
                s_reportedStale.Remove(path);
            }
            var context = GrasshopperRuns.ContextOf(component);
            if (context == null)
                return Skip("no script context");
            if (context.GetType().GetProperty("IsDebugStopped", Any | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as bool? == true)
                return Skip("Stop was pressed"); // the rest of the solution runs plainly
            var prepare = context.GetType().GetMethod("PrepareContext", new[] { typeof(Rhino.Runtime.Code.Code), typeof(Rhino.Runtime.Code.Execution.BuildKind) });
            var run = prepare?.Invoke(context, new object[] { code, Rhino.Runtime.Code.Execution.BuildKind.Debug });
            var envType = ProxyEnvType();
            var exec = FindMethod(component.GetType(), "ExecCode", 3);
            if (run == null || envType == null || exec == null)
                return Skip(string.Format("Grasshopper changed? (context {0}, ghenv {1}, ExecCode {2})", run != null, envType != null, exec != null));
            var proxy = context.GetType().GetProperty("ProxyDocument", Any | BindingFlags.FlattenHierarchy)?.GetValue(context);
            var env = Activator.CreateInstance(envType, proxy, da);
            var inputs = run.GetType().GetProperty("Inputs")?.GetValue(run);
            var set = inputs?.GetType().GetMethods().FirstOrDefault(m => m.Name == "Set" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(string));
            if (set == null)
                return Skip("no way to set ghenv");
            set.Invoke(inputs, new object[] { "ghenv", env, false, false });
            Log.Info(LogArea.Breakpoints, "Grasshopper: '{0}' -- its script input in the debugger ({1})", GrasshopperRuns.NickName(component), path);
            if (once)
                s_debugOnce.Remove(component);
            // Grasshopper's own debug run (ScriptContext.TryExecCode, Debug) runs inside a debug
            // group made with the definition's document -- ghdoc and the rest of Grasshopper's
            // script scope; without it the run failed: "name 'ghdoc' is not defined" (at line 4
            // of Grasshopper's own preamble, not the script's).
            var createGroup = FindMethod(context.GetType(), "CreateDebugGroup", 1);
            var endExec = FindMethod(context.GetType(), "EndExec", 2);
            if (!(run is DebugContext debugContext) || createGroup == null || endExec == null)
                return Skip("no debug group for Grasshopper's document");
            // Built first, as Grasshopper builds (ScriptContext.TryBuildCode): with the component's
            // libraries (its library input), then for this context -- a group on code not built
            // for it failed in CPythonCode.BeginRunGroup.
            try
            {
                var shared = component.GetType().GetInterfaces().Select(i => i.GetProperty("SharedLibraries")).FirstOrDefault(p => p != null)?.GetValue(component) as IEnumerable;
                var libraries = shared?.Cast<Rhino.Runtime.Code.Languages.ILanguageLibrary>().ToArray();
                if (libraries != null && libraries.Length > 0)
                    code.Libraries.Set(libraries);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper script input's libraries: {0}", Log.Describe(ex));
            }
            // The script's parameters -- the inputs, ghdoc and ghenv, the outputs -- as
            // Grasshopper gives its own code object for an input script (PrepareParams, in
            // BeforeSolveInstance): the Script Editor's code for the file has none, and without
            // them the script's first line finds no input ("name 'x' is not defined").
            var prepareParams = FindMethod(component.GetType(), "PrepareParams", 2);
            if (prepareParams != null)
            {
                try
                {
                    var sets = new object[2];
                    prepareParams.Invoke(component, sets);
                    SetParams(code.Inputs, sets[0]);
                    SetParams(code.Outputs, sets[1]);
                }
                catch (Exception ex)
                {
                    return Skip("its script's parameters: " + Log.Describe(ex));
                }
            }
            if (!code.TryBuild(debugContext, out Rhino.Runtime.Code.Diagnostics.Diagnosis diagnosis))
                return Skip("its script doesn't build: " + diagnosis);
            if (!(createGroup.Invoke(context, new object[] { code }) is DebugGroup group))
                return Skip("no debug group for Grasshopper's document");
            // As Grasshopper's own debug run (ScriptContext.TryBeginExec, BuildKind.Debug): the
            // group begun under an identity of its own, the run's context begun and ended by
            // code.Debug inside ExecCode, the group ended by EndExec. A group begun with the run's
            // context ended that context a second time, and RhinoCode's tracker refused ("Can not
            // end a context that was not started by this thread").
            Rhino.Runtime.Code.Execution.RunGroup execGroup;
            try
            {
                execGroup = code.DebugWith(group, new Rhino.Runtime.Code.Execution.ContextIdentity("miniVivisection: " + GrasshopperRuns.NickName(component)));
                code.DebugControls.Reset();
                if (once && ReferenceEquals(onceHow, FirstLine))
                    Threads.RequestBreak(code.DebugControls, 0); // the menu's run without breakpoints: its first line
            }
            catch (Exception ex)
            {
                return Skip("the debugger wouldn't begin: " + ex.Message);
            }
            try
            {
                DebuggingControls = code.DebugControls;
                exec.Invoke(component, new[] { da, code, run });
                var errors = component.GetType().GetMethod("RuntimeMessages")?.Invoke(component, new object[] { Enum.Parse(component.GetType().Assembly.GetType("Grasshopper.Kernel.GH_RuntimeMessageLevel") ??
                    AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Grasshopper").GetType("Grasshopper.Kernel.GH_RuntimeMessageLevel"), "Error") }) as IEnumerable;
                var first = errors?.Cast<object>().FirstOrDefault();
                if (first != null)
                    Log.Warning(LogArea.Breakpoints, "Grasshopper: '{0}' -- its script input in the debugger failed: {1}", GrasshopperRuns.NickName(component), first);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                Log.Warning(LogArea.Breakpoints, "Grasshopper: '{0}' -- its script input in the debugger: {1}", GrasshopperRuns.NickName(component), ex.InnerException);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Breakpoints, "Grasshopper: '{0}' -- its script input in the debugger: {1}", GrasshopperRuns.NickName(component), ex);
                throw;
            }
            finally
            {
                DebuggingControls = null;
                try
                {
                    endExec.Invoke(context, new object[] { execGroup, run }); // the streams and the group
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Breakpoints, "Grasshopper script input's debug run, ending it: {0}", Log.Describe(ex));
                }
            }
            return true;
        }

        /// <summary>The debugger of the script-input run going on now (<see cref="TryDebugRun"/>), else
        /// null: Ctrl+Alt+B pauses that run; any other run of the component it stops
        /// (GrasshopperRuns.RunsPlainly).</summary>
        internal static IDebugControls DebuggingControls { get; private set; }

        private static readonly HashSet<string> s_skipReasons = new HashSet<string>();

        private static bool Skip(string why)
        {
            lock (s_skipReasons)
            {
                if (s_skipReasons.Add(why))
                    Log.Info(LogArea.Breakpoints, "Grasshopper script input: not in the debugger -- {0}", why);
            }
            return false;
        }

        private static string Normalize(string text) => (text ?? "").Replace("\r\n", "\n").TrimEnd();

        private static Type s_proxyEnv;

        private static Type ProxyEnvType()
        {
            if (s_proxyEnv != null)
                return s_proxyEnv;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("RhinoCode", StringComparison.Ordinal)))
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                var found = types.FirstOrDefault(t => t.Name == "ProxyScriptEnv" && t.GetConstructors().Any(c => c.GetParameters().Length == 2));
                if (found != null)
                    return s_proxyEnv = found;
            }
            return null;
        }

        /// <summary>The component's parameters -- its inputs, ghdoc and ghenv, its outputs -- given to the
        /// editor's code for its script (as Grasshopper gives its own: PrepareParams), so the editor
        /// knows them as variables. False when they can't be.</summary>
        private static bool GiveParams(object component, Rhino.Runtime.Code.Code code)
        {
            var prepareParams = code == null ? null : FindMethod(component.GetType(), "PrepareParams", 2);
            if (prepareParams == null)
                return false;
            try
            {
                var sets = new object[2];
                prepareParams.Invoke(component, sets);
                SetParams(code.Inputs, sets[0]);
                SetParams(code.Outputs, sets[1]);
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper: the script input's parameters for the editor: {0}", Log.Describe(ex));
                return false;
            }
        }

        // code.Inputs.Set(parameters) / code.Outputs.Set(parameters): the set's Set that takes
        // Grasshopper's ScriptParamSet (an IEnumerable of parameters)
        private static void SetParams(object paramSet, object parameters)
        {
            if (paramSet == null || parameters == null)
                return;
            var set = paramSet.GetType().GetMethods().FirstOrDefault(m => m.Name == "Set" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(parameters))
                ?? throw new MissingMethodException(paramSet.GetType().FullName, "Set(" + parameters.GetType().Name + ")");
            set.Invoke(paramSet, new[] { parameters });
        }

        private static MethodInfo FindMethod(Type type, string name, int parameters)
        {
            for (var each = type; each != null; each = each.BaseType)
            {
                var found = each.GetMethods(Any | BindingFlags.DeclaredOnly).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameters);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>The Script Editor window showing <paramref name="path"/>, and its code (UI thread).</summary>
        private static (Window Window, Rhino.Runtime.Code.Code Code) EditorCodeFor(string path)
        {
            foreach (var window in Application.Instance.Windows)
            {
                Rhino.Runtime.Code.Code code;
                try
                {
                    code = new ScriptEditorAccess(window).Code;
                }
                catch (Exception)
                {
                    continue;
                }
                if (code?.Uri != null && code.Uri.IsFile && string.Equals(code.Uri.LocalPath, path, StringComparison.OrdinalIgnoreCase))
                    return (window, code);
            }
            return (null, null);
        }

        /// <summary>The file of the script in the input's data -- each item a script whose code
        /// knows where it comes from (Grasshopper1Script.TryGetCode: its Uri); null when it isn't
        /// a file.</summary>
        private static string ScriptFileOf(object input)
        {
            try
            {
                var data = input.GetType().GetProperty("VolatileData")?.GetValue(input);
                var all = data?.GetType().GetMethod("AllData")?.Invoke(data, new object[] { true }) as IEnumerable;
                var goo = all?.Cast<object>().FirstOrDefault();
                if (goo == null)
                {
                    data = input.GetType().GetProperty("PersistentData")?.GetValue(input);
                    all = data?.GetType().GetMethod("AllData")?.Invoke(data, new object[] { true }) as IEnumerable;
                    goo = all?.Cast<object>().FirstOrDefault();
                }
                var script = goo?.GetType().GetProperty("Value")?.GetValue(goo);
                // The script's own address (Template.Uri): the file it was read from -- what
                // Grasshopper shows as "Script (path)".
                if (script?.GetType().GetProperty("Uri")?.GetValue(script) is Uri own && own.IsFile && File.Exists(own.LocalPath))
                    return own.LocalPath;
                var tryGetCode = script?.GetType().GetMethods().FirstOrDefault(m => m.Name == "TryGetCode" && m.GetParameters().Length == 1);
                if (tryGetCode != null)
                {
                    var args = new object[] { null };
                    if ((bool)tryGetCode.Invoke(script, args) && args[0] is Code code && code.Uri != null && code.Uri.IsFile && File.Exists(code.Uri.LocalPath))
                        return code.Uri.LocalPath;
                }
                // A plain path as the item's text
                var text = (script as string ?? goo?.ToString() ?? "").Trim().Trim('"');
                return text.Length > 0 && text.IndexOfAny(new[] { '\n', '\r' }) < 0 && Path.IsPathRooted(text) && File.Exists(text) ? text : null;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper script input's file: {0}", Log.Describe(ex));
                return null;
            }
        }

        /// <summary>What the input holds, for the message when no file is found: each item's type,
        /// its text, and its script's address.</summary>
        private static string Describe(object input)
        {
            try
            {
                var items = new List<string>();
                foreach (var name in new[] { "VolatileData", "PersistentData" })
                {
                    var data = input.GetType().GetProperty(name)?.GetValue(input);
                    if (!(data?.GetType().GetMethod("AllData")?.Invoke(data, new object[] { true }) is IEnumerable all))
                        continue;
                    foreach (var goo in all.Cast<object>().Take(3))
                    {
                        var script = goo?.GetType().GetProperty("Value")?.GetValue(goo);
                        var uri = script?.GetType().GetProperty("Uri")?.GetValue(script);
                        items.Add(string.Format("{0}: {1} ({2}{3})", name, goo, script?.GetType().Name ?? "no value", uri != null ? ", " + uri : ""));
                    }
                }
                return items.Count == 0 ? "no data" : string.Join("; ", items);
            }
            catch (Exception ex)
            {
                return Log.Describe(ex);
            }
        }

        private static object InputParam(object component, string name = "script")
        {
            var parameters = component.GetType().GetProperty("Params")?.GetValue(component);
            var inputs = (parameters?.GetType().GetProperty("Input")?.GetValue(parameters) as IEnumerable)?.Cast<object>();
            return inputs?.FirstOrDefault(p => p.GetType().GetProperty("Name")?.GetValue(p) as string == name);
        }

        // --- the component's library input -------------------------------------------------------

        private const int MostLibraryFiles = 20;

        // A folder not walked for a library's files: hidden, a virtual environment, a cache
        private static bool Skipped(string folderName) =>
            folderName.StartsWith(".", StringComparison.Ordinal) || folderName == "__pycache__" || folderName == "site-packages" ||
            folderName == "node_modules" || string.Equals(folderName, "venv", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether the component has a "library" input with something in it (the menu
        /// offers its files then; which files is found out when asked for -- it can take a moment).</summary>
        internal static bool HasLibrary(object component)
        {
            try
            {
                var input = InputParam(component, "library");
                if (input == null)
                    return false;
                return new[] { "VolatileData", "PersistentData" }.Any(name =>
                    input.GetType().GetProperty(name)?.GetValue(input) is object data && data.GetType().GetProperty("DataCount")?.GetValue(data) is int count && count > 0);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The files of the component's "library" input (a folder of modules, Grasshopper1ScriptLibrary)
        /// in its language (.py, or .cs for C#): as the library lists them, else what its folder holds; at most
        /// <see cref="MostLibraryFiles"/>.</summary>
        internal static List<string> LibraryFiles(object component)
        {
            var files = new List<string>();
            try
            {
                var input = InputParam(component, "library");
                if (input == null)
                    return files;
                var extension = component.GetType().Name.IndexOf("CSharp", StringComparison.OrdinalIgnoreCase) >= 0 ? ".cs" : ".py";
                bool Wanted(string path) => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path) &&
                    !path.Split(Path.DirectorySeparatorChar).Any(Skipped);
                // The folder's files, walking its subfolders -- not hidden ones, nor a virtual
                // environment or a cache (a project folder holds thousands of files there) --,
                // the first MostLibraryFiles only, and for half a second at most: this runs on
                // the UI thread when a menu opens
                IEnumerable<string> Walk(string root)
                {
                    var found = 0;
                    var started = Environment.TickCount64;
                    var folders = new Stack<string>();
                    folders.Push(root);
                    while (folders.Count > 0 && found < MostLibraryFiles && Environment.TickCount64 - started < 500)
                    {
                        var folder = folders.Pop();
                        IEnumerable<string> files, subfolders;
                        try
                        {
                            files = Directory.EnumerateFiles(folder, "*" + extension).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                            subfolders = Directory.EnumerateDirectories(folder).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                        }
                        catch (Exception)
                        {
                            continue; // no access
                        }
                        foreach (var file in files)
                        {
                            if (found >= MostLibraryFiles)
                                break;
                            found++;
                            yield return file;
                        }
                        foreach (var sub in subfolders)
                        {
                            if (!Skipped(Path.GetFileName(sub)))
                                folders.Push(sub);
                        }
                    }
                }
                foreach (var name in new[] { "VolatileData", "PersistentData" })
                {
                    var data = input.GetType().GetProperty(name)?.GetValue(input);
                    if (!(data?.GetType().GetMethod("AllData")?.Invoke(data, new object[] { true }) is IEnumerable all))
                        continue;
                    foreach (var goo in all.Cast<object>())
                    {
                        // Not cast yet (a path waiting for the solution): the text it holds
                        var library = goo?.GetType().GetProperty("Value")?.GetValue(goo) ??
                            goo?.GetType().GetProperty("CastSource", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(goo) as string;
                        if (library == null)
                            continue;
                        var listed = new List<string>();
                        if (library is string text)
                        {
                            // a folder given as text (not cast to a library yet)
                            var path = text.Trim().Trim('"');
                            if (Directory.Exists(path))
                                listed.AddRange(Walk(path).Where(Wanted));
                        }
                        else if (library is IEnumerable items)
                        {
                            foreach (var item in items)
                            {
                                if (item?.GetType().GetProperty("Uri")?.GetValue(item) is Uri uri && uri.IsFile && Wanted(uri.LocalPath))
                                    listed.Add(uri.LocalPath);
                            }
                        }
                        if (listed.Count == 0 && library.GetType().GetProperty("Base")?.GetValue(library) is Uri folder && folder.IsFile && Directory.Exists(folder.LocalPath))
                            listed.AddRange(Walk(folder.LocalPath).Where(Wanted));
                        files.AddRange(listed);
                    }
                    if (files.Count > 0)
                        break; // the solved data; the input's own only when there's none
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper library input's files: {0}", Log.Describe(ex));
            }
            return files.Distinct(StringComparer.OrdinalIgnoreCase).Take(MostLibraryFiles).ToList();
        }

        /// <summary>A double-click on a component running its script input (miniVivisection.GH, by
        /// reflection -- Grasshopper's own does nothing then): its library's files and its script in
        /// the Script Editor, the script opened last so its tab is in front. UI thread.</summary>
        internal static void OpenWithLibraries(object component)
        {
            var libraries = LibraryFiles(component);
            foreach (var file in libraries)
                RhinoApp.RunScript("_-ScriptEditor _Open \"" + file + "\"", false);
            if (libraries.Count > 0)
                UserLog.Info("'{0}': its library's {1} file(s) are open in the Script Editor{2}.", GrasshopperRuns.NickName(component), libraries.Count,
                    libraries.Count >= MostLibraryFiles ? " (the first " + MostLibraryFiles + ")" : "");
            Open(component); // its script, in front
        }

        /// <summary>"Export Script" of a component running its script input (its menu): the script --
        /// the Panel's text, the file's, or text from elsewhere -- saved where the user chooses.</summary>
        internal static void Export(object component)
        {
            try
            {
                var (path, text, _, from) = Locate(component);
                if (path == null)
                {
                    UserLog.Warning("'{0}': its script input has no script to export ({1}).", GrasshopperRuns.NickName(component), from);
                    return;
                }
                var content = text ?? File.ReadAllText(path);
                var csharp = component.GetType().Name.IndexOf("CSharp", StringComparison.OrdinalIgnoreCase) >= 0;
                var extension = csharp ? ".cs" : ".py";
                var dialog = new SaveFileDialog
                {
                    Title = "Export the script of '" + GrasshopperRuns.NickName(component) + "'",
                    FileName = Safe(GrasshopperRuns.NickName(component)) + extension,
                };
                dialog.Filters.Add(new FileFilter(csharp ? "C# script" : "Python script", "*" + extension));
                dialog.Filters.Add(new FileFilter("All files", "*.*"));
                if (dialog.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) != DialogResult.Ok || string.IsNullOrEmpty(dialog.FileName))
                    return;
                File.WriteAllText(dialog.FileName, content);
                UserLog.Info("'{0}': its script exported to {1}.", GrasshopperRuns.NickName(component), dialog.FileName);
            }
            catch (Exception ex)
            {
                UserLog.Warning("'{0}': its script couldn't be exported: {1}", GrasshopperRuns.NickName(component), Log.Describe(ex));
            }
        }

        /// <summary>The library's files in the Script Editor (the component's menu), for any script component.</summary>
        internal static void OpenLibraries(object component)
        {
            var libraries = LibraryFiles(component);
            foreach (var file in libraries)
                RhinoApp.RunScript("_-ScriptEditor _Open \"" + file + "\"", false);
            UserLog.Info(libraries.Count == 0
                ? "'{0}': its library input has no {2} files to open."
                : "'{0}': its library's {1} file(s) are open in the Script Editor.", GrasshopperRuns.NickName(component), libraries.Count,
                component.GetType().Name.IndexOf("CSharp", StringComparison.OrdinalIgnoreCase) >= 0 ? "C#" : "Python");
        }

        // The first text a source has: a string parameter's data.
        private static string FirstText(object source)
        {
            var data = source.GetType().GetProperty("VolatileData")?.GetValue(source);
            var all = data?.GetType().GetMethod("AllData")?.Invoke(data, new object[] { true }) as IEnumerable;
            var goo = all?.Cast<object>().FirstOrDefault();
            var value = goo?.GetType().GetProperty("Value")?.GetValue(goo);
            return value as string ?? goo?.ToString();
        }

        private static string NickName(object documentObject) => documentObject?.GetType().GetProperty("NickName")?.GetValue(documentObject) as string ?? "script";

        private static string Safe(string name) =>
            new string((string.IsNullOrWhiteSpace(name) ? "script" : name).Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());

        /// <summary>At the plug-in's end: the files (the user's scripts, in %TEMP%) and their
        /// watchers go.</summary>
        internal static void Shutdown()
        {
            lock (s_followed)
            {
                foreach (var followed in s_followed.Values)
                    followed.Watcher?.Dispose();
                s_followed.Clear();
            }
            try
            {
                // The working copies go; files keeping changes that weren't saved (WriteWorkingCopy) stay
                foreach (var file in Directory.Exists(Folder) ? Directory.GetFiles(Folder) : Array.Empty<string>())
                {
                    if (Path.GetFileName(file).IndexOf(UnsavedMark, StringComparison.OrdinalIgnoreCase) < 0)
                        File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper script inputs' working copies: {0}", Log.Describe(ex));
            }
        }

        // Saves of the opened file, followed: back into the Panel, or the component recomputed.
        private static void Follow(string path, object component, object panel, string written)
        {
            written = written?.Replace("\r\n", "\n");
            lock (s_followed)
            {
                if (s_followed.TryGetValue(path, out var known))
                {
                    known.Component = new WeakReference<object>(component);
                    known.Panel = panel == null ? null : new WeakReference<object>(panel);
                    known.Written = written;
                    return;
                }
                var watcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                };
                var followed = new Followed
                {
                    Component = new WeakReference<object>(component),
                    Panel = panel == null ? null : new WeakReference<object>(panel),
                    Watcher = watcher,
                    Written = written,
                };
                FileSystemEventHandler saved = (s, e) => Saved(path, followed);
                watcher.Changed += saved;
                watcher.Created += saved;
                watcher.Renamed += (s, e) => Saved(path, followed); // an editor saving through a temporary file
                watcher.EnableRaisingEvents = true;
                s_followed[path] = followed;
            }
        }

        // A save comes as several events: taken once, a moment after the last.
        private static void Saved(string path, Followed followed)
        {
            followed.Pending = DateTime.UtcNow;
            System.Threading.Tasks.Task.Delay(300).ContinueWith(_ =>
            {
                if (DateTime.UtcNow - followed.Pending < TimeSpan.FromMilliseconds(280))
                    return; // a later event takes it
                RhinoApp.InvokeOnUiThread(new Action(() => Apply(path, followed)));
            });
        }

        private static void Apply(string path, Followed followed)
        {
            try
            {
                if (!followed.Component.TryGetTarget(out var component) || GrasshopperRuns.DocumentOf(component) == null)
                {
                    // The component is gone (deleted, or its definition closed and opened again): the
                    // file is no one's; said, and the watcher goes
                    lock (s_followed)
                    {
                        s_followed.Remove(path);
                    }
                    followed.Watcher?.Dispose();
                    UserLog.Warning("{0} saved, but the Panel or component it came from is gone -- not taken over. Open the script again from its menu.", Path.GetFileName(path));
                    return;
                }
                string text = null;
                for (var attempt = 0; attempt < 5 && text == null; attempt++)
                {
                    try
                    {
                        text = File.ReadAllText(path);
                    }
                    catch (IOException)
                    {
                        System.Threading.Thread.Sleep(50); // the editor still writing
                    }
                }
                if (text == null)
                    return;
                if (followed.Panel != null && followed.Panel.TryGetTarget(out var panel))
                {
                    var userText = panel.GetType().GetProperty("UserText");
                    var before = userText?.GetValue(panel) as string ?? "";
                    // The Panel's own line breaks: an editor saves the file with its own.
                    text = text.Replace("\r\n", "\n");
                    if (before.Contains("\r\n"))
                        text = text.Replace("\n", "\r\n");
                    if (before == text)
                        return;
                    // The Panel changed since the file was made from it (edited in place, undone,
                    // another save): the file's text would take that away
                    if (followed.Written != null && before.Replace("\r\n", "\n") != followed.Written)
                    {
                        UserLog.Warning("Panel '{0}' changed since its script was opened in the Script Editor: the save isn't taken over. Open it again from the Panel's menu to edit its text as it is now.", NickName(panel));
                        return;
                    }
                    // As Grasshopper's own editing of a Panel does: an undo step, an autosave
                    panel.GetType().GetMethod("RecordUndoEvent", new[] { typeof(string) })?.Invoke(panel, new object[] { "Edit Panel" });
                    panel.GetType().GetMethod("TriggerAutoSave", Type.EmptyTypes)?.Invoke(panel, null);
                    var set = panel.GetType().GetMethod("SetUserText", new[] { typeof(string) });
                    if (set != null)
                        set.Invoke(panel, new object[] { text });
                    else
                        userText?.SetValue(panel, text);
                    followed.Written = text.Replace("\r\n", "\n");
                    Expire(panel);
                    if (ReferenceEquals(component, panel))
                        UserLog.Info("Panel '{0}': the script saved back into it.", NickName(panel));
                    else
                        UserLog.Info("'{0}': the script saved back into the Panel '{1}' -- the component recomputes.", GrasshopperRuns.NickName(component), NickName(panel));
                }
                else if (followed.Panel == null)
                {
                    Reload(component); // RhinoCode keeps a file's code once read
                    Expire(component);
                    UserLog.Info("'{0}': {1} saved -- the component recomputes.", GrasshopperRuns.NickName(component), Path.GetFileName(path));
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Breakpoints, "Grasshopper script input saved: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The input's scripts read again from their files (Code.TryLoad), and the input
        /// expired: a recompute runs what was saved, not RhinoCode's copy of the file.</summary>
        private static void Reload(object component)
        {
            try
            {
                var input = InputParam(component);
                foreach (var name in new[] { "VolatileData", "PersistentData" })
                {
                    var data = input?.GetType().GetProperty(name)?.GetValue(input);
                    if (!(data?.GetType().GetMethod("AllData")?.Invoke(data, new object[] { true }) is IEnumerable all))
                        continue;
                    foreach (var goo in all.Cast<object>())
                    {
                        var script = goo?.GetType().GetProperty("Value")?.GetValue(goo);
                        var tryGetCode = script?.GetType().GetMethods().FirstOrDefault(m => m.Name == "TryGetCode" && m.GetParameters().Length == 1);
                        var args = new object[] { null };
                        if (tryGetCode != null && (bool)tryGetCode.Invoke(script, args) && args[0] is Code code)
                            code.GetType().GetMethod("TryLoad", Type.EmptyTypes)?.Invoke(code, null);
                    }
                }
                if (input != null)
                    Expire(input);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper script input reloaded: {0}", Log.Describe(ex));
            }
        }

        private static void Expire(object documentObject) =>
            documentObject.GetType().GetMethod("ExpireSolution", new[] { typeof(bool) })?.Invoke(documentObject, new object[] { true });

        /// <summary><paramref name="then"/> once a Script Editor shows <paramref name="path"/> (up to 15 s).</summary>
        private static void WhenOpen(string path, Action<Window, Rhino.Runtime.Code.Code> then)
        {
            var tries = 0;
            var timer = new UITimer { Interval = 0.3 };
            timer.Elapsed += (s, e) =>
            {
                try
                {
                    foreach (var window in Application.Instance.Windows)
                    {
                        Rhino.Runtime.Code.Code code;
                        try
                        {
                            code = new ScriptEditorAccess(window).Code;
                        }
                        catch (Exception)
                        {
                            continue; // not a Script Editor
                        }
                        if (code?.Uri != null && code.Uri.IsFile && string.Equals(code.Uri.LocalPath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            timer.Stop();
                            then(window, code);
                            return;
                        }
                    }
                    if (++tries > 50)
                        timer.Stop();
                }
                catch (Exception ex)
                {
                    timer.Stop();
                    Log.Debug(LogArea.Breakpoints, "Grasshopper script input in the editor: {0}", Log.Describe(ex));
                }
            };
            timer.Start();
        }
    }
}
