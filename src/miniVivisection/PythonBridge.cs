using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Rhino.Runtime.Code.Execution;

namespace MiniVivisection
{
    /// <summary>
    /// The paused script's Python, reached through RhinoCode's pythonnet
    /// (<c>McNeel.Python39.Runtime</c>, loaded by RhinoCode -- reflection only, no
    /// compile-time reference).
    ///
    /// Only on the paused thread: it holds the GIL (the debugger pauses inside
    /// RhinoCode's Python trace function), so taking it again is immediate. Any
    /// other thread would wait for the GIL until the script continues.
    /// </summary>
    internal static class PythonBridge
    {
        private const string RuntimeAssemblyName = "McNeel.Python39.Runtime";
        private const int MaxItems = 10000;

        // The helpers (python/minivivisection_helpers.py, embedded): run in the paused thread's
        // Python. The debugger's trace function (RhinoCode's trace_begin.py:
        // __pynet_tracefunc__(frame, event, arg)) is the innermost Python frame while
        // paused; its `frame` is the paused frame.
        private static string s_helperSource;

        private static string HelperSource
        {
            get
            {
                if (s_helperSource == null)
                {
                    using (var stream = typeof(PythonBridge).Assembly.GetManifestResourceStream("minivivisection_helpers.py")
                        ?? throw new InvalidOperationException("the plug-in's embedded minivivisection_helpers.py is missing"))
                    using (var reader = new System.IO.StreamReader(stream))
                        s_helperSource = reader.ReadToEnd();
                }
                return s_helperSource;
            }
        }

        private static Type s_py;
        private static Type s_engine;
        private static object s_helpers;
        private static Type s_pyObject;
        private static MethodInfo s_gil;
        private static MethodInfo s_import;
        private static MethodInfo s_asManaged;
        private static MethodInfo s_fromManaged;
        private static MethodInfo s_length;
        private static MethodInfo s_getItem;

        /// <summary>Whether <paramref name="value"/> is a Python object pythonnet didn't
        /// convert (a list, a dict, an instance of a Python class, ...).</summary>
        public static bool IsPython(object value)
        {
            if (value == null || !Init())
                return false;
            return s_pyObject.IsInstanceOfType(value);
        }

        /// <summary><c>eval(expression)</c> in the paused frame -- its globals and locals,
        /// modules and functions included. Throws what Python raised.</summary>
        public static object Eval(string expression)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                return ToClr(helpers["__minivivisection_eval"](expression));
            }
        }

        /// <summary>The code's tooltip for <paramref name="expression"/> at
        /// <paramref name="line"/> (0: the paused frame): [what (plain words), type name,
        /// value text, the value]. A builtin's type name is null; a .NET value's text is
        /// null (described by the plug-in). Throws when it has no value there.</summary>
        public static List<object> HoverInfo(string expression, int line, string filename)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                object result = helpers["__minivivisection_hover_info"](expression, line, filename);
                return Items(result) ?? throw new InvalidOperationException("no hover info");
            }
        }

        /// <summary>The paused frame's variables, for inline values and "was" (ValueHistory):
        /// its key, first line, the paused line, and (name, text or null, value) -- a
        /// .NET value's text is null (formatted by HoverText). Paused thread.</summary>
        public static (string Key, int First, int Current, List<(string Name, string Text, object Value)> Items) InlineValues()
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                object result = helpers["__minivivisection_inline_values"]();
                List<object> AsList(object o) => Items(o) ?? (o as System.Collections.IEnumerable)?.Cast<object>().ToList();
                var parts = AsList(result) ?? throw new InvalidOperationException("no inline values");
                var items = new List<(string, string, object)>();
                foreach (var item in (AsList(parts[3]) ?? new List<object>()))
                {
                    var triple = AsList(item);
                    if (triple != null && triple.Count == 3 && triple[0] is string name)
                        items.Add((name, triple[1] as string, triple[2]));
                }
                return ((string)parts[0], Convert.ToInt32(parts[1]), Convert.ToInt32(parts[2]), items);
            }
        }

        /// <summary><paramref name="expression"/> as written at <paramref name="line"/> of
        /// the script (the hover preview): names resolve as Python would there -- the
        /// locals of the frame running that code, else globals; a local of code that
        /// isn't running raises. <paramref name="filename"/>: the hovered file when it
        /// isn't the paused frame's. On the paused thread.</summary>
        public static object EvalAt(string expression, int line, string filename)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                return ToClr(helpers["__minivivisection_eval_at"](expression, line, filename));
            }
        }

        /// <summary>Whether <paramref name="expression"/> (a dotted name, as written at
        /// <paramref name="line"/>) is a class -- a call of it is a constructor. On the paused thread.</summary>
        public static bool IsClassAt(string expression, int line, string filename)
        {
            if (!Init())
                return false;
            using (Gil())
            {
                dynamic helpers = Helpers();
                return ToClr(helpers["__minivivisection_is_class_at"](expression, line, filename)) is bool yes && yes;
            }
        }

        /// <summary>The Watch tab's row for <paramref name="expression"/> in the paused
        /// frame: the <see cref="ExecVariable"/> RhinoCode's own trace script makes for
        /// its value -- value text, type, expandable children -- or for the error Python
        /// raised. Null outside a paused Python script.</summary>
        public static ExecVariable WatchVariable(string expression)
        {
            if (!Init())
                return null;
            using (Gil())
            {
                dynamic helpers = Helpers();
                return ToClr(helpers["__minivivisection_watch_var"](expression)) as ExecVariable;
            }
        }

        /// <summary>Null when <paramref name="expression"/> is a Python expression (or
        /// Python isn't loaded: it's checked when it runs), else what's wrong.</summary>
        public static string SyntaxError(string expression)
        {
            if (!Init())
                return null;
            using (Gil())
            {
                dynamic helpers = Helpers();
                return ToClr(helpers["__minivivisection_syntax_error"](expression)) as string;
            }
        }

        /// <summary>Whether this thread is inside RhinoCode's Python trace callback (a
        /// Python script's breakpoint is being decided).</summary>
        public static bool InTraceCallback()
        {
            if (!Init())
                return false;
            using (Gil())
            {
                dynamic helpers = Helpers();
                return ToClr(helpers["__minivivisection_in_trace"]()) is bool inTrace && inTrace;
            }
        }

        /// <summary>A breakpoint's condition, in the traced frame: whether it holds.
        /// Throws what Python raised.</summary>
        /// <summary>The Grasshopper run being evaluated now -- a script component's run number,
        /// branch and item (GrasshopperRuns) -- or null: conditions and logpoints get them as
        /// gh_run, gh_branch and gh_item.</summary>
        public static Func<(int Run, string Branch, string Item)?> GrasshopperRun { get; set; }

        private static void SetGrasshopperRun(dynamic helpers)
        {
            var run = GrasshopperRun?.Invoke();
            helpers["__minivivisection_set_gh"](run?.Run ?? -1, run?.Branch, run?.Item);
        }

        public static bool ConditionHolds(string expression)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                SetGrasshopperRun(helpers);
                return ToClr(helpers["__minivivisection_condition"](expression)) is bool holds && holds;
            }
        }

        /// <summary>A logpoint: the message with its {expressions} filled in from the
        /// traced frame, printed to the script's stdout (the editor's Terminal). Returns
        /// the line.</summary>
        public static string Logpoint(string message, bool threadTag = false)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                SetGrasshopperRun(helpers);
                return ToClr(helpers["__minivivisection_logpoint"](message, threadTag)) as string ?? "";
            }
        }

        /// <summary>A line in the script's Terminal (its stdout). On the paused or traced thread.</summary>
        public static void Print(string text)
        {
            if (!Init())
                return;
            using (Gil())
            {
                dynamic helpers = Helpers();
                helpers["__minivivisection_print"](text);
            }
        }

        /// <summary>The calling Python thread: its label ("main", "script" or its name), its
        /// ident, and how many Python threads run code.</summary>
        public static (string Label, long Ident, int Count) ThreadInfo()
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                var parts = AsList(helpers["__minivivisection_thread_info"]()) ?? throw new InvalidOperationException("no thread info");
                return ((string)parts[0], Convert.ToInt64(parts[1]), Convert.ToInt32(parts[2]));
            }
        }

        /// <summary>Every Python thread that runs code, with its state and stack
        /// (ThreadsSnapshot). <paramref name="pausedHere"/>: on the paused thread; else the
        /// calling thread is left out. Takes the GIL: from another thread only while no
        /// thread is paused (a paused thread holds it).</summary>
        public static List<ThreadRow> Threads(bool pausedHere)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                var rows = new List<ThreadRow>();
                foreach (var row in AsList(helpers["__minivivisection_threads"](pausedHere)) ?? new List<object>())
                {
                    var parts = AsList(row);
                    if (parts == null || parts.Count < 6)
                        continue;
                    var frames = new List<ThreadFrame>();
                    foreach (var frame in AsList(parts[5]) ?? new List<object>())
                    {
                        var f = AsList(frame);
                        if (f != null && f.Count >= 4)
                            frames.Add(new ThreadFrame(f[0] as string ?? "", Convert.ToInt32(f[1]), f[2] as string ?? "", f[3] is bool own && own));
                    }
                    rows.Add(new ThreadRow(Convert.ToInt64(parts[0]), parts[1] as string ?? "", parts[2] is bool main && main,
                        parts[3] is bool daemon && daemon, parts[4] as string ?? "", frames));
                }
                return rows;
            }
        }

        /// <summary>Where a script running without the debugger is now (the thread with
        /// <paramref name="path"/> on its stack), and with <paramref name="stop"/> a
        /// KeyboardInterrupt raised there; null: no thread runs it. Takes the GIL from this
        /// thread: the running script hands it over between bytecodes (StoppedRuns).</summary>
        public static StoppedRuns.Where StopScript(string path, bool stop) => StopScript(path, stop, 0, null, null);

        /// <summary>As above, in thread <paramref name="ident"/> only when it isn't 0 (the run's
        /// own thread, when known). With the GIL held -- it may come long after the caller gave
        /// up -- <paramref name="stillWanted"/> is asked first (false: nothing is done), and
        /// <paramref name="found"/> gets where the script is before the interrupt is raised:
        /// the run's end can't come before its record.</summary>
        public static StoppedRuns.Where StopScript(string path, bool stop, long ident, Func<bool> stillWanted, Action<StoppedRuns.Where> found)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                if (stillWanted != null && !stillWanted())
                    return null;
                dynamic helpers = Helpers();
                var row = AsList(helpers["__minivivisection_stop_script"](path, false, ident));
                if (row == null || row.Count < 6)
                    return null;
                var where = new StoppedRuns.Where
                {
                    Ident = Convert.ToInt64(row[0]),
                    File = row[1] as string ?? "",
                    Line = Convert.ToInt32(row[2]),
                    Function = row[3] as string ?? "",
                };
                foreach (var frame in AsList(row[4]) ?? new List<object>())
                {
                    var f = AsList(frame);
                    if (f != null && f.Count >= 3)
                        where.Stack.Add(new StoppedRuns.StackLine { File = f[0] as string ?? "", Line = Convert.ToInt32(f[1]), Function = f[2] as string ?? "" });
                }
                foreach (var value in AsList(row[5]) ?? new List<object>())
                {
                    var v = AsList(value);
                    if (v != null && v.Count >= 2)
                        where.Values.Add(new[] { v[0] as string ?? "", v[1] as string ?? "" });
                }
                if (!stop)
                    return where;
                found?.Invoke(where);
                return (bool)helpers["__minivivisection_interrupt"](where.Ident) ? where : null;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>This thread as Python names it (threading.get_ident(): Windows' thread id).</summary>
        public static long CurrentThreadIdent() => GetCurrentThreadId();

        /// <summary><paramref name="expression"/> in each thread's innermost frame of the
        /// script, read only (watches "in every thread"). On the paused thread.</summary>
        public static List<(string Label, long Ident, bool Here, bool Ok, object Value)> EvalInThreads(string expression)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                var results = new List<(string, long, bool, bool, object)>();
                foreach (var row in AsList(helpers["__minivivisection_eval_in_threads"](expression)) ?? new List<object>())
                {
                    var parts = AsList(row);
                    if (parts != null && parts.Count >= 5)
                        results.Add((parts[0] as string ?? "", Convert.ToInt64(parts[1]), parts[2] is bool here && here, parts[3] is bool ok && ok, parts[4]));
                }
                return results;
            }
        }

        // A Python list, or what pythonnet made of it.
        private static List<object> AsList(object value) =>
            Items(value) ?? (value is string ? null : (value as System.Collections.IEnumerable)?.Cast<object>().ToList());

        /// <summary><c>eval(expression)</c> in the Python tab's own namespace (not the
        /// paused frame). Throws what Python raised.</summary>
        public static object EvalInSession(string expression)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                return ToClr(helpers["__minivivisection_eval_session"](expression));
            }
        }

        /// <summary>How <paramref name="value"/> can be edited as Rhino objects
        /// (minivivisection_visualize.edit_mode): "inverse", "raw", "geometry", or
        /// "readonly:" and the reason.</summary>
        public static string EditMode(object value, string interpretation)
        {
            if (!Init())
                return "geometry";
            using (Gil())
            {
                dynamic helpers = PipelineHelpers();
                return ToClr(helpers["__minivivisection_edit_mode"](value, interpretation ?? "auto")) as string ?? "geometry";
            }
        }

        /// <summary>Edited geometry back into <paramref name="expression"/>'s value in the
        /// paused frame -- through its @rhinoviv.inverse, or into raw data's points;
        /// a replacement is assigned. Returns what happened; throws what Python raised.</summary>
        public static string WriteBack(string expression, string interpretation, List<object> geometries)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = PipelineHelpers();
                return ToClr(helpers["__minivivisection_write_back"](expression, interpretation ?? "auto", geometries)) as string ?? "";
            }
        }

        /// <summary>Runs visualizer scripts in the paused frame's context (each in a module
        /// of its own). One line per script: "ok|path|names" or "error|path|message".</summary>
        public static List<string> LoadVisualizerScripts(IEnumerable<string> paths)
        {
            if (!Init())
                return new List<string>();
            using (Gil())
            {
                dynamic helpers = PipelineHelpers();
                object result = helpers["__minivivisection_load_visualizers"](paths.ToList());
                return (Items(result) ?? new List<object>()).OfType<string>().ToList();
            }
        }

        /// <summary>The plug-in's python folder (minivivisection_visualize, rhinoviv), put on the
        /// Python path before the pipeline is imported. Set at load.</summary>
        public static string PythonDirectory { get; set; }

        /// <summary>Puts <paramref name="folder"/> on the running Python's sys.path (scripts'
        /// <c>import rhinoviv</c>), when Python is loaded. False when it isn't.</summary>
        public static bool AddToSysPath(string folder)
        {
            if (!Init())
                return false;
            using (Gil())
            {
                dynamic builtins = s_import.Invoke(null, new object[] { "builtins" });
                var literal = System.Text.Json.JsonSerializer.Serialize(folder); // a JSON string is a Python string
                builtins.exec("import sys\nif " + literal + " not in sys.path: sys.path.append(" + literal + ")", builtins.dict());
                return true;
            }
        }

        private static string s_pythonDirectoryGiven;

        /// <summary>The helpers know the current python folder (the GIL is held).</summary>
        private static dynamic PipelineHelpers()
        {
            dynamic helpers = Helpers();
            if (PythonDirectory != s_pythonDirectoryGiven && !string.IsNullOrEmpty(PythonDirectory))
            {
                dynamic builtins = s_import.Invoke(null, new object[] { "builtins" });
                builtins.exec("__minivivisection_python_dir = " + System.Text.Json.JsonSerializer.Serialize(PythonDirectory), helpers); // a JSON string is a Python string
                s_pythonDirectoryGiven = PythonDirectory;
            }
            return helpers;
        }

        /// <summary>What <paramref name="value"/> is drawn as -- the drawing pipeline
        /// (python/minivivisection_visualize.py): a registered visualizer, geometry, raw data by
        /// <paramref name="interpretation"/> ("auto" or one of its candidates), containers
        /// item by item. <paramref name="items"/> are (kind, value): "geometry" (anything
        /// WatchGeometry draws) or "arrow" (a Line). <paramref name="how"/> is the path
        /// for the tab ("as polyline", "by show_sensors", or empty). Throws what a
        /// visualizer raised; false when the pipeline isn't there.</summary>
        public static bool TryVisualize(object value, string interpretation, int limit, out string how, out List<(string Kind, object Value)> items)
        {
            how = "";
            items = null;
            if (!Init())
                return false;
            using (Gil())
            {
                dynamic helpers = PipelineHelpers();
                object result = helpers["__minivivisection_visualize"](value, interpretation ?? "auto", limit);
                var parts = Items(result);
                if (parts == null || parts.Count < 4)
                    return false;
                how = parts[1] as string ?? "";
                var kinds = Items(parts[2]) ?? new List<object>();
                var values = Items(parts[3]) ?? new List<object>();
                items = kinds.Zip(values, (k, v) => (k as string ?? "geometry", v)).ToList();
                return true;
            }
        }

        /// <summary>The interpretations of <paramref name="value"/>, the default first:
        /// (id, label) -- for the Interpret As menus.</summary>
        public static List<(string Id, string Label)> Candidates(object value)
        {
            if (!Init())
                return new List<(string, string)>();
            using (Gil())
            {
                dynamic helpers = PipelineHelpers();
                object result = helpers["__minivivisection_candidates"](value);
                return (Items(result) ?? new List<object>()).OfType<string>()
                    .Select(c => c.Split(new[] { '|' }, 2)).Select(p => (p[0], p.Length > 1 ? p[1] : p[0])).ToList();
            }
        }

        /// <summary>The variable RhinoCode's trace script makes for <paramref name="value"/>,
        /// already evaluated, named <paramref name="name"/>: value text, type, expandable
        /// members (the V Watch tab's details). Null outside a paused Python script.</summary>
        public static ExecVariable VariableFor(string name, object value)
        {
            if (!Init())
                return null;
            using (Gil())
            {
                dynamic helpers = Helpers();
                return ToClr(helpers["__minivivisection_var_for"](name, value)) as ExecVariable;
            }
        }

        /// <summary>Runs <paramref name="source"/> -- statements, like a notebook cell: a
        /// final expression's value is printed -- in the paused frame (a function's
        /// locals written back) or in the terminal's own namespace. Returns the output.</summary>
        public static string Exec(string source, bool inPausedFrame)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                return ToClr(helpers["__minivivisection_exec"](source, inPausedFrame)) as string ?? "";
            }
        }

        /// <summary><c>target = value</c> in the paused frame -- any assignable target
        /// (<c>crv</c>, <c>curves[0]</c>, <c>node.x</c>); a function's locals are written
        /// back. Throws what Python raised.</summary>
        /// <summary>The paused Python frame (file, function, thread) -- on the paused thread;
        /// null when it can't be read.</summary>
        public static string PausedFrameKey()
        {
            try
            {
                if (!Init())
                    return null;
                using (Gil())
                {
                    dynamic helpers = Helpers();
                    return helpers["__minivivisection_frame_key"]().ToString();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Assign(string target, object value)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                helpers["__minivivisection_assign"](target, value);
            }
        }

        /// <summary>Whether Python is running in this Rhino (some Python script ran).</summary>
        public static bool IsRunning
        {
            get
            {
                if (!Init())
                    return false;
                var initialized = s_engine?.GetProperty("IsInitialized", BindingFlags.Public | BindingFlags.Static);
                return initialized != null && (bool)initialized.GetValue(null);
            }
        }

        /// <summary>One terminal input, Python-prompt style (<c>codeop</c>, <c>'single'</c>
        /// mode: expression results are printed): in the paused frame -- assignments to a
        /// function's locals are written back -- or in the terminal's own namespace.
        /// Returns whether more lines are needed (an unfinished block) and the output.</summary>
        public static (bool More, string Output) Run(string source, bool inPausedFrame)
        {
            if (!Init())
                throw new InvalidOperationException("Python isn't loaded");
            using (Gil())
            {
                dynamic helpers = Helpers();
                dynamic result = helpers["__minivivisection_run"](source, inPausedFrame);
                bool more = (bool)ToClr(result[0]);
                string output = ToClr(result[1]) as string ?? "";
                return (more, output);
            }
        }

        /// <summary>The items of a Python list, tuple or set (up to 10000), as .NET
        /// objects where pythonnet can convert them; null for any other Python value
        /// -- iterating a generator would consume it.</summary>
        public static List<object> Items(object pythonValue)
        {
            if (!IsPython(pythonValue))
                return null;
            using (Gil())
            {
                dynamic helpers = Helpers();
                object items = helpers["__minivivisection_items"](pythonValue, MaxItems);
                if (!IsPython(items) || IsNone(items))
                    return null;
                // PyObject isn't IEnumerable in this pythonnet: by index.
                var result = new List<object>();
                var length = (long)s_length.Invoke(items, null);
                for (var i = 0; i < length; i++)
                    result.Add(ToClr(s_getItem.Invoke(items, new object[] { i })));
                return result;
            }
        }

        private static bool Init()
        {
            if (s_py != null)
                return true;
            var runtime = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == RuntimeAssemblyName);
            if (runtime == null)
                return false;
            var py = runtime.GetType("Python.Runtime.Py");
            var pyObject = runtime.GetType("Python.Runtime.PyObject");
            var gil = py?.GetMethod("GIL", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            var import = py?.GetMethod("Import", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            var asManaged = pyObject?.GetMethod("AsManagedObject", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Type) }, null);
            var fromManaged = pyObject?.GetMethod("FromManagedObject", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(object) }, null);
            var length = pyObject?.GetMethod("Length", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var getItem = pyObject?.GetMethod("GetItem", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            if (gil == null || import == null || asManaged == null || fromManaged == null || length == null || getItem == null)
                return false;
            s_engine = runtime.GetType("Python.Runtime.PythonEngine");
            s_pyObject = pyObject;
            s_gil = gil;
            s_import = import;
            s_asManaged = asManaged;
            s_fromManaged = fromManaged;
            s_length = length;
            s_getItem = getItem;
            s_py = py;
            return true;
        }

        private static IDisposable Gil() => (IDisposable)s_gil.Invoke(null, null);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GilStateCheck();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        private static GilStateCheck s_gilStateCheck;

        /// <summary>Whether this thread holds Python's GIL now (CPython's PyGILState_Check, from the
        /// loaded python DLL) -- a Python script running on it called into .NET, say. Such a thread
        /// can't wait for Python code on another thread: that code waits for the GIL it holds.
        /// False when Python isn't loaded (or the check can't be found).</summary>
        internal static bool ThisThreadHoldsGil()
        {
            try
            {
                if (s_gilStateCheck == null)
                {
                    var runtime = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == RuntimeAssemblyName);
                    if (runtime == null)
                        return false; // no Python yet: no one holds its GIL
                    var dll = runtime.GetType("Python.Runtime.Runtime")?.GetProperty("PythonDLL", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as string;
                    var module = IntPtr.Zero;
                    if (!string.IsNullOrEmpty(dll))
                        NativeLibrary.TryLoad(dll, out module);
                    if (module == IntPtr.Zero)
                        module = GetModuleHandle("python39.dll");
                    if (module == IntPtr.Zero || !NativeLibrary.TryGetExport(module, "PyGILState_Check", out var export))
                        return false;
                    s_gilStateCheck = Marshal.GetDelegateForFunctionPointer<GilStateCheck>(export);
                }
                return s_gilStateCheck() != 0;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Python's GIL on this thread: {0}", Log.Describe(ex));
                return false;
            }
        }

        /// <summary>The namespace with the helpers defined, created once -- it also keeps
        /// the terminal's own namespace (the GIL is held).</summary>
        private static object Helpers()
        {
            if (s_helpers == null)
            {
                dynamic builtins = s_import.Invoke(null, new object[] { "builtins" });
                dynamic ns = builtins.dict();
                builtins.exec(HelperSource, ns);
                // The commands' way back into the plug-in. A dict item must be a PyObject --
                // the indexer doesn't convert .NET objects -- and the dynamic binder goes by
                // the value's static type, hence the cast: Invoke returns object.
                ns["__minivivisection_host"] = (dynamic)s_fromManaged.Invoke(null, new object[] { TerminalHost.Instance });
                s_helpers = ns;
            }
            return s_helpers;
        }

        private static bool IsNone(object pythonValue)
        {
            var isNone = pythonValue.GetType().GetMethod("IsNone", Type.EmptyTypes);
            return isNone != null && (bool)isNone.Invoke(pythonValue, null);
        }

        /// <summary>The .NET object a Python value wraps (a Curve, a Point3d, a str, ...),
        /// or the Python value itself (the GIL is held).</summary>
        private static object ToClr(object value)
        {
            if (!IsPython(value) || IsNone(value))
                return IsPython(value) ? null : value;
            try
            {
                return s_asManaged.Invoke(value, new object[] { typeof(object) });
            }
            catch (TargetInvocationException)
            {
                return value;
            }
        }
    }
}
