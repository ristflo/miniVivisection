using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using IronPython.Modules;
using IronPython.Runtime;
using IronPython.Runtime.Exceptions;
using IronPython.Runtime.Operations;
using IronPython.Runtime.Types;
using Rhino.Runtime.Code.Execution;

namespace MiniVivisection
{
    /// <summary>
    /// The paused IronPython 2 script's frame (<c>#! python2</c>, ScriptKinds), through
    /// IronPython's own engine -- Rhino's IronPython.dll, referenced at compile time and never
    /// copied. RhinoCode's IronPython tracer builds every ExecFrame with a closure over the
    /// IronPython frame (a TraceBackFrame); <see cref="FrameOf"/> reaches it by reflection, and
    /// IronPython's eval and exec then run in that frame's globals and locals (the helpers in
    /// python/minivivisection_helpers_ipy.py, compiled once per engine). A function's locals go
    /// through the DLR debugger's scope, so assignments reach the running function. IronPython
    /// has no GIL: any thread may evaluate while the script is paused.
    ///
    /// IronPython's types appear only in the private methods, which JIT when first called --
    /// after an IronPython script ran, which loads the assembly; the public ones take and give
    /// plain objects.
    /// </summary>
    internal static class IronPythonBridge
    {
        private const string AssemblyName = "IronPython";
        private const string FrameTypeName = "IronPython.Runtime.Exceptions.TraceBackFrame";
        private const string TracerAssemblyName = "Rhino.Runtime.Code.Languages.IronPython2";

        private static bool s_loaded;
        private static FieldInfo s_provider; // ExecFrame.m_variableProvider
        private static readonly Dictionary<Type, FieldInfo> s_frameFields = new Dictionary<Type, FieldInfo>(); // the closure's TraceBackFrame field, per closure type
        private static FieldInfo s_scopeCallback; // TraceBackFrame._scopeCallback: the tracer's scope, with the module's context
        private static FieldInfo s_context;       // TraceBackFrame._context: set for frames that aren't traced
        private static readonly ConditionalWeakTable<object, object> s_helpers = new ConditionalWeakTable<object, object>(); // per engine (PythonContext): the helpers' globals
        private static string s_source;
        private static Type s_pyExecVariable;
        private static bool s_pyExecVariableLooked;

        /// <summary>Whether Rhino has IronPython loaded (an IronPython script ran).</summary>
        public static bool IsLoaded
        {
            get
            {
                if (!s_loaded)
                    s_loaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == AssemblyName);
                return s_loaded;
            }
        }

        /// <summary>The IronPython frame (a TraceBackFrame) behind <paramref name="frame"/>;
        /// null when it isn't an IronPython script's frame.</summary>
        public static object FrameOf(ExecFrame frame)
        {
            if (frame == null || !IsLoaded)
                return null;
            try
            {
                if (s_provider == null)
                    s_provider = typeof(ExecFrame).GetField("m_variableProvider", BindingFlags.NonPublic | BindingFlags.Instance);
                var target = (s_provider?.GetValue(frame) as Delegate)?.Target;
                if (target == null)
                    return null;
                var type = target.GetType();
                FieldInfo field;
                lock (s_frameFields)
                {
                    if (!s_frameFields.TryGetValue(type, out field))
                    {
                        field = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(f => f.FieldType.FullName == FrameTypeName);
                        s_frameFields[type] = field;
                    }
                }
                return field?.GetValue(target);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Python, "IronPython frame: {0}", Log.Describe(ex));
                return null;
            }
        }

        /// <summary><c>eval(expression)</c> in the paused frame. Throws what IronPython raised,
        /// with the Python type in the message (NameError: name 'x' is not defined).</summary>
        public static object Eval(ExecFrame frame, string expression) => Guard(() =>
        {
            var tb = Frame(frame);
            return Call(ContextOf(tb), "mv_eval", tb, expression.Trim());
        });

        /// <summary>Runs <paramref name="source"/> -- statements, like a notebook cell: a final
        /// expression's value is printed -- in the paused frame. Returns the output.</summary>
        public static string Exec(ExecFrame frame, string source) => Guard(() =>
        {
            var tb = Frame(frame);
            return Call(ContextOf(tb), "mv_exec", tb, source) as string ?? "";
        });

        /// <summary>One terminal input, Python-prompt style, in the paused frame: whether more
        /// lines are needed (an unfinished block), and the output.</summary>
        public static (bool More, string Output) Run(ExecFrame frame, string source) => Guard(() =>
        {
            var tb = Frame(frame);
            var result = (PythonTuple)Call(ContextOf(tb), "mv_run", tb, source);
            return (result[0] is bool more && more, result[1] as string ?? "");
        });

        /// <summary><c>target = value</c> in the paused frame -- any assignable target
        /// (<c>crv</c>, <c>curves[0]</c>, <c>node.x</c>); a function's locals included.</summary>
        public static void Assign(ExecFrame frame, string target, object value) => Guard(() =>
        {
            var tb = Frame(frame);
            Call(ContextOf(tb), "mv_assign", tb, target, value);
            return true;
        });

        /// <summary>A breakpoint's condition in the traced frame: whether it holds.
        /// <paramref name="extras"/>: names the condition may use besides the script's
        /// (gh_run, gh_branch, gh_item).</summary>
        public static bool ConditionHolds(ExecFrame frame, string condition, IReadOnlyDictionary<string, object> extras = null) => Guard(() =>
        {
            var tb = Frame(frame);
            return Call(ContextOf(tb), "mv_condition", tb, condition, Extras(extras)) is bool holds && holds;
        });

        /// <summary>A logpoint's message with its {expressions} filled in from the traced frame.</summary>
        public static string Logpoint(ExecFrame frame, string message, IReadOnlyDictionary<string, object> extras = null) => Guard(() =>
        {
            var tb = Frame(frame);
            return Call(ContextOf(tb), "mv_logpoint", tb, message, Extras(extras)) as string ?? "";
        });

        /// <summary>The paused frame -- file, function, first line -- so an edit started there
        /// is written back only there (EditSession); null when it can't be read.</summary>
        public static string FrameKey(ExecFrame frame)
        {
            try
            {
                var tb = Frame(frame);
                return Call(ContextOf(tb), "mv_frame_key", tb) as string;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Edit, "IronPython frame key: {0}", Log.Describe(ex));
                return null;
            }
        }

        /// <summary>The paused frame's variables, for the values at the ends of the lines and
        /// "was" (InlineValues): its key, the code's first line, the paused line, and (name,
        /// text or null, value) -- a .NET value's text is null (HoverText formats it).</summary>
        public static (string Key, int First, int Current, List<(string Name, string Text, object Value)> Items) InlineValues(ExecFrame frame) => Guard(() =>
        {
            var tb = Frame(frame);
            var parts = (PythonTuple)Call(ContextOf(tb), "mv_inline_values", tb);
            var items = new List<(string, string, object)>();
            foreach (var item in ((System.Collections.IEnumerable)parts[3]).Cast<object>())
            {
                var triple = (PythonTuple)item;
                items.Add(((string)triple[0], triple[1] as string, triple[2]));
            }
            return ((string)parts[0], Convert.ToInt32(parts[1]), Convert.ToInt32(parts[2]), items);
        });

        /// <summary>The editor's Watch tab row for <paramref name="expression"/> in the paused
        /// frame: the variable RhinoCode's own tracer makes (value text, type, expandable
        /// members), or one holding the error IronPython raised.</summary>
        public static ExecVariable WatchVariable(ExecFrame frame, string expression)
        {
            try
            {
                return VariableFor(expression, Eval(frame, expression));
            }
            catch (Exception ex)
            {
                return new ExecVariable(expression, ScriptEditorHook.FirstLine(ex));
            }
        }

        /// <summary>The variable RhinoCode's IronPython tracer makes for a value already
        /// evaluated, named <paramref name="name"/> (the V Watch tab's details); a plain
        /// ExecVariable when that type can't be found.</summary>
        public static ExecVariable VariableFor(string name, object value)
        {
            try
            {
                if (!s_pyExecVariableLooked)
                {
                    s_pyExecVariableLooked = true;
                    var tracer = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == TracerAssemblyName)
                        ?.GetType(TracerAssemblyName + ".IronPythonTracer");
                    s_pyExecVariable = tracer?.GetNestedType("PyExecVariable", BindingFlags.Public | BindingFlags.NonPublic);
                    if (s_pyExecVariable == null)
                        Log.Debug(LogArea.Watch, "RhinoCode's PyExecVariable wasn't found: plain variables for IronPython values");
                }
                if (s_pyExecVariable != null)
                {
                    return (ExecVariable)Activator.CreateInstance(s_pyExecVariable, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new object[] { name, value, ExecVariableKind.FrameVariable, ExecVariableAttribute.Empty }, null);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Watch, "IronPython variable {0}: {1}", name, Log.Describe(ex));
            }
            return new ExecVariable(name, value);
        }

        /// <summary>Null when <paramref name="expression"/> is an IronPython expression -- or it
        /// can't be checked (IronPython isn't loaded yet; the first hit reports an error) --,
        /// else what's wrong.</summary>
        public static string SyntaxError(string expression)
        {
            if (!IsLoaded)
                return null;
            try
            {
                return CheckSyntax(expression);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "IronPython syntax check: {0}", Log.Describe(ex));
                return null;
            }
        }

        // --- IronPython's types from here on -----------------------------------------------------

        private static TraceBackFrame Frame(ExecFrame frame) =>
            FrameOf(frame) as TraceBackFrame ?? throw new InvalidOperationException("the paused frame isn't an IronPython script's");

        /// <summary>The context to evaluate in: a traced frame's own is null, but the tracer's
        /// scope holds the module's as "$globalContext" (how the frame finds its globals).</summary>
        private static CodeContext ContextOf(TraceBackFrame frame)
        {
            if (s_scopeCallback == null)
                s_scopeCallback = typeof(TraceBackFrame).GetField("_scopeCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            if (s_context == null)
                s_context = typeof(TraceBackFrame).GetField("_context", BindingFlags.NonPublic | BindingFlags.Instance);
            if (s_scopeCallback?.GetValue(frame) is Func<IDictionary<object, object>> scope)
            {
                try
                {
                    if (scope().TryGetValue("$globalContext", out var found) && found is CodeContext global)
                        return global;
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Python, "IronPython scope: {0}", Log.Describe(ex));
                }
            }
            return s_context?.GetValue(frame) as CodeContext ?? DefaultContext.Default ?? throw new InvalidOperationException("IronPython has no engine yet");
        }

        private static string Source
        {
            get
            {
                if (s_source == null)
                {
                    using (var stream = typeof(IronPythonBridge).Assembly.GetManifestResourceStream("minivivisection_helpers_ipy.py")
                        ?? throw new InvalidOperationException("the plug-in's embedded minivivisection_helpers_ipy.py is missing"))
                    using (var reader = new System.IO.StreamReader(stream))
                        s_source = reader.ReadToEnd();
                }
                return s_source;
            }
        }

        /// <summary>The helpers, compiled once per engine into globals of their own.</summary>
        private static PythonDictionary Helpers(CodeContext context)
        {
            var engine = context.LanguageContext;
            lock (s_helpers)
            {
                if (s_helpers.TryGetValue(engine, out var found))
                    return (PythonDictionary)found;
                var globals = new PythonDictionary { ["__name__"] = "__minivivisection_ipy__" };
                var code = (FunctionCode)Builtin.compile(context, Source, "<minivivisection>", "exec", null, null);
                Builtin.eval(context, code, globals, globals);
                s_helpers.Add(engine, globals);
                Log.Debug(LogArea.Python, "IronPython helpers compiled ({0})", engine.GetType().Name);
                return globals;
            }
        }

        private static object Call(CodeContext context, string function, params object[] args) =>
            PythonCalls.Call(context, Helpers(context)[function], args);

        private static PythonDictionary Extras(IReadOnlyDictionary<string, object> extras)
        {
            if (extras == null || extras.Count == 0)
                return null;
            var dictionary = new PythonDictionary();
            foreach (var pair in extras)
                dictionary[pair.Key] = pair.Value;
            return dictionary;
        }

        private static string CheckSyntax(string expression)
        {
            var context = DefaultContext.Default;
            return context == null ? null : Call(context, "mv_syntax_error", expression) as string;
        }

        private static MethodInfo s_toPython; // PythonExceptions.ToPython(Exception), internal: the Python exception object

        /// <summary>IronPython's exception as Python names it: "NameError: name 'x' is not defined".</summary>
        public static string Describe(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException;
            var message = ScriptEditorHook.FirstLine(ex);
            try
            {
                if (s_toPython == null)
                    s_toPython = typeof(PythonExceptions).GetMethod("ToPython", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(Exception) }, null);
                var python = s_toPython?.Invoke(null, new object[] { ex });
                var name = python == null ? ex.GetType().Name : PythonType.Get__name__(DynamicHelpers.GetPythonType(python));
                return message.StartsWith(name, StringComparison.Ordinal) ? message : name + ": " + message;
            }
            catch (Exception)
            {
                return ex.GetType().Name + ": " + message;
            }
        }

        private static T Guard<T>(Func<T> work)
        {
            try
            {
                return work();
            }
            catch (IronPythonError)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new IronPythonError(Describe(ex), ex);
            }
        }
    }

    /// <summary>What IronPython raised, described as Python does (the type and the message).</summary>
    internal sealed class IronPythonError : Exception
    {
        public IronPythonError(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
