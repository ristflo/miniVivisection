using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Eto.Forms;
using HarmonyLib;
using Rhino;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Languages;
using Rhino.Runtime.Code.Execution.Debugging;
using Code = Rhino.Runtime.Code.Code;

namespace MiniVivisection
{
    /// <summary>
    /// Grasshopper's script components (RhinoCodePluginGH), followed run by run. A component
    /// runs once per item or branch of its inputs (GH_Component.RunCount: 1, 2, ...); for each
    /// run the paths and indices of its inputs are known (Grasshopper's GH_StructureIterator, the
    /// IGH_DataAccess it passes: its current branch and item per input, private fields read by
    /// reflection -- ParameterTargetPath/Index are about the outputs). So:
    /// - **Failing runs** (IScriptObject.AddError with RhinoCode's ExecuteException: its line)
    ///   are kept per component: a remark on it names them -- branch, item, line -- and its
    ///   right-click menu copies their paths ({0;3}, or {0;3}[2] for an item) for a Panel, to
    ///   feed Tree Branch or V Edit's Index and isolate that data; or debugs that run.
    /// - **Ctrl+Alt+B with the component's editor closed** (no debugger: a plain run): the
    ///   running Python 3 script is stopped as RunPythonScript's are (StoppedRuns, Python's
    ///   lock from another thread, KeyboardInterrupt), its remaining runs skipped and the
    ///   solution aborted; then its editor opens at the line with a one-shot breakpoint for that
    ///   run only (condition ghenv.Component.RunCount == N) and the overlay explaining it.
    /// - **Debug this component** (menu): its editor opens, the component recomputes in the
    ///   debugger and pauses at its first line (or at its breakpoints).
    /// Hooks on BaseScriptComponent&lt;ScriptParam, ScriptLibraryParam&gt; (Harmony), patched
    /// when RhinoCodePluginGH loads; Grasshopper itself by reflection.
    /// </summary>
    internal static class GrasshopperRuns
    {
        private const string PluginAssembly = "RhinoCodePluginGH";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Harmony s_harmony;
        private static bool s_listening;
        private static string s_error;

        /// <summary>For the health line (null: in place, or Grasshopper not loaded yet).</summary>
        internal static string HookProblem => s_error;

        /// <summary>One run of a component: which data it had.</summary>
        internal sealed class RunInfo
        {
            public int Run;       // RunCount, 1-based
            public string Branch; // the main input's path: {0;3}
            public string Item;   // with its index, for an item input: {0;3}[2]
            public string Inputs; // every input: "x {0;3}[2], y {0}"
        }

        internal sealed class Failure
        {
            public RunInfo Run;
            public int Line;
            public string Message;
        }

        private sealed class State
        {
            public readonly List<Failure> Failures = new List<Failure>();
            public readonly HashSet<int> FailedRuns = new HashSet<int>();
            public readonly Dictionary<string, int> RunsPerData = new Dictionary<string, int>(); // item or branch -> runs with it
            public RunInfo Current;
            public bool Solved; // SolveInstance ran in this solution (a required input may be empty)
            public volatile bool Stopping;
            public volatile StoppedRuns.Where Stopped;
            public StoppedRuns.StopAttempt Attempt;
            public RunInfo StoppedRun;
            public bool HotkeyHeld; // this solution counted as a run for the hotkey
            public int BuildKindCalls; // GrasshopperDebug.Calls when its solution began
        }

        private static readonly ConditionalWeakTable<object, State> s_states = new ConditionalWeakTable<object, State>();
        private static volatile object s_running; // the component in SolveInstance now (UI thread)

        /// <summary>The definition the running script component is in (UI thread), or null.</summary>
        internal static object RunningDocument => s_running is object component ? DocumentOf(component) : null;

        /// <summary>Whether a script component is in a run now.</summary>
        internal static bool Solving => s_running != null;

        /// <summary>The script component whose run goes on (or is paused) now; null: none.</summary>
        internal static object RunningComponent => s_running;
        private static long s_solvingThread;      // that thread, as Python names it: the stop's target

        public static void Start()
        {
            Threads.InGrasshopperRun = () => Solving;
            if (!s_listening)
            {
                s_listening = true;
                AppDomain.CurrentDomain.AssemblyLoad += (sender, e) =>
                {
                    if (e.LoadedAssembly.GetName().Name == PluginAssembly)
                        TryInstall(e.LoadedAssembly);
                };
            }
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == PluginAssembly);
            if (loaded != null)
                TryInstall(loaded);
        }

        private static void TryInstall(Assembly assembly)
        {
            if (s_harmony != null)
                return;
            try
            {
                var generic = assembly.GetType("RhinoCodePluginGH.Components.BaseScriptComponent`2", true);
                // The runs are followed in Grasshopper's own code, not in the script component's:
                // the component's members are generic, their code shared by every instantiation,
                // and when .NET recompiles a method that runs often (tiered compilation) the new
                // code of such a method comes without Harmony's patch -- runs would go unfollowed,
                // and Ctrl+Alt+B couldn't stop a component that hangs. GH_Component isn't generic.
                var grasshopper = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Grasshopper");
                var ghComponent = grasshopper.GetType("Grasshopper.Kernel.GH_Component", true);
                var activeObject = grasshopper.GetType("Grasshopper.Kernel.GH_ActiveObject", true);
                const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                // Every target first, then the patches: a missing one leaves none applied.
                s_solveInstance = ghComponent.GetMethod("SolveInstance", declared) ?? throw new MissingMethodException(ghComponent.FullName, "SolveInstance");
                var callers = new[] { "ComputeData", "Solution_Compute_TreeOnlyAccess", "Solution_Compute_TreeAndListAccess", "Solution_Compute_MixedAccess" }
                    .Select(name => ghComponent.GetMethod(name, declared) ?? throw new MissingMethodException(ghComponent.FullName, name)).ToList();
                var computeData = callers[0];
                var addMessage = activeObject.GetMethods(declared).FirstOrDefault(m => m.Name == "AddRuntimeMessage" && m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType == typeof(string)) ?? throw new MissingMethodException(activeObject.FullName, "AddRuntimeMessage(level, string)");
                // The menu too: GH_Component.AppendAdditionalMenuItems calls the component's
                // (generic) AppendAdditionalComponentMenuItems; miniVivisection's items come right after.
                var menu = ghComponent.GetMethod("AppendAdditionalMenuItems", declared) ?? throw new MissingMethodException(ghComponent.FullName, "AppendAdditionalMenuItems");
                s_componentMenu = ghComponent.GetMethod("AppendAdditionalComponentMenuItems", declared) ?? throw new MissingMethodException(ghComponent.FullName, "AppendAdditionalComponentMenuItems");
                s_scriptComponent = generic;
                s_callSolve = MakeSolveCaller(ghComponent, grasshopper.GetType("Grasshopper.Kernel.IGH_DataAccess", true));
                var harmony = new Harmony("minivivisection.grasshopperruns");
                try
                {
                    foreach (var caller in callers)
                        harmony.Patch(caller, transpiler: new HarmonyMethod(typeof(GrasshopperRuns), nameof(CallSolveThroughHook)));
                    harmony.Patch(computeData, prefix: new HarmonyMethod(typeof(GrasshopperRuns), nameof(ComputePrefix)),
                        finalizer: new HarmonyMethod(typeof(GrasshopperRuns), nameof(ComputeFinalizer)));
                    harmony.Patch(addMessage, postfix: new HarmonyMethod(typeof(GrasshopperRuns), nameof(MessagePostfix)));
                    harmony.Patch(menu, transpiler: new HarmonyMethod(typeof(GrasshopperRuns), nameof(AddMenuAfterComponents)));
                    // The transpilers found what they look for? Else none of it works, and it must say so.
                    if (s_solveCallsReplaced < callers.Count)
                        throw new MissingMethodException(ghComponent.FullName, "SolveInstance call in " + (callers.Count - s_solveCallsReplaced) + " solve loop(s) (Grasshopper changed?)");
                    if (!s_menuAdded)
                        throw new MissingMethodException(ghComponent.FullName, "AppendAdditionalComponentMenuItems call in AppendAdditionalMenuItems (Grasshopper changed?)");
                }
                catch
                {
                    harmony.UnpatchAll(harmony.Id); // this id only: never every Harmony patch in Rhino
                    throw;
                }
                s_harmony = harmony;
                // Conditions and logpoints: gh_run, gh_branch, gh_item of the run being evaluated.
                PythonBridge.GrasshopperRun = () =>
                {
                    var running = s_running;
                    var run = running == null ? null : StateOf(running).Current;
                    return run == null ? ((int, string, string)?)null : (run.Run, run.Branch, run.Item);
                };
                Log.Info(LogArea.Breakpoints, "Grasshopper script components followed run by run (failing branches, Ctrl+Alt+B with the editor closed)");
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                s_error = ex.Message;
                UserLog.Warning("Grasshopper script components aren't followed run by run (failing branches, stopping them): {0}", ex.Message);
            }
        }

        private static State StateOf(object component) => s_states.GetValue(component, _ => new State());

        private static Type s_scriptComponent;        // BaseScriptComponent`2
        private static MethodInfo s_componentMenu;     // GH_Component.AppendAdditionalComponentMenuItems(menu)

        // GH_Component.AppendAdditionalMenuItems: after "AppendAdditionalComponentMenuItems(menu)",
        // "MenuHook(this, menu)" -- miniVivisection's items, for script components.
        private static IEnumerable<CodeInstruction> AddMenuAfterComponents(IEnumerable<CodeInstruction> instructions)
        {
            var hook = typeof(GrasshopperRuns).GetMethod(nameof(MenuHook), BindingFlags.NonPublic | BindingFlags.Static);
            var added = false;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (!added && instruction.operand is MethodInfo called && called == s_componentMenu)
                {
                    added = true;
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0);
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_1);
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, hook);
                }
            }
            s_menuAdded = added;
        }

        private static bool s_menuAdded;             // the menu transpiler found its place
        private static int s_solveCallsReplaced;     // the solve-loop transpilers: calls replaced

        private static void MenuHook(object component, object menu)
        {
            if (IsScriptComponent(component))
                MenuPostfix(component, menu);
        }
        private static MethodInfo s_solveInstance;     // GH_Component.SolveInstance(IGH_DataAccess)
        private static Action<object, object> s_callSolve;
        private static readonly ConditionalWeakTable<Type, object> s_isScript = new ConditionalWeakTable<Type, object>();

        /// <summary>component.SolveInstance(da): a compiled call (it's protected).</summary>
        private static Action<object, object> MakeSolveCaller(Type ghComponent, Type dataAccess)
        {
            var method = new System.Reflection.Emit.DynamicMethod("miniVivisectionSolveInstance", null, new[] { typeof(object), typeof(object) }, ghComponent, true);
            var il = method.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Castclass, ghComponent);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Castclass, dataAccess);
            il.Emit(System.Reflection.Emit.OpCodes.Callvirt, s_solveInstance);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return (Action<object, object>)method.CreateDelegate(typeof(Action<object, object>));
        }

        // Grasshopper's loops: "callvirt SolveInstance(it)" becomes "call SolveHook(this, it)".
        private static IEnumerable<CodeInstruction> CallSolveThroughHook(IEnumerable<CodeInstruction> instructions)
        {
            var hook = typeof(GrasshopperRuns).GetMethod(nameof(SolveHook), BindingFlags.NonPublic | BindingFlags.Static);
            var replaced = 0;
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == System.Reflection.Emit.OpCodes.Callvirt || instruction.opcode == System.Reflection.Emit.OpCodes.Call) &&
                    instruction.operand is MethodInfo called && called == s_solveInstance)
                {
                    instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                    instruction.operand = hook;
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced > 0)
                s_solveCallsReplaced++;
        }

        internal static bool IsScriptComponent(object component)
        {
            var type = component?.GetType();
            if (type == null || s_scriptComponent == null)
                return false;
            return (bool)s_isScript.GetValue(type, t =>
            {
                for (var each = t; each != null; each = each.BaseType)
                {
                    if (each.IsGenericType && each.GetGenericTypeDefinition() == s_scriptComponent)
                        return true;
                }
                return false;
            });
        }

        /// <summary>Each run of every Grasshopper component: a script component's is followed
        /// (SolvePrefix: false -- stopped -- skips it), then SolveInstance as Grasshopper would.</summary>
        private static void SolveHook(object component, object da)
        {
            if (IsScriptComponent(component))
            {
                if (!SolvePrefix(component, da))
                    return;
                if (GrasshopperInputScripts.TryDebugRun(component, da))
                    return; // its script input, in the debugger (the Script Editor's code for it)
                if (RunsOffUiThread(component))
                {
                    SolveOffUiThread(component, da);
                    return;
                }
            }
            s_callSolve(component, da);
        }

        // --- # async:true in a component: its runs off the UI thread -----------------------------

        private static int s_offUiThread; // runs on their own threads now (one definition's inside another's wait)

        /// <summary>Whether the running script component's run is on a thread of its own now
        /// (# async:true): the UI thread is free -- Rhino and the canvas work, its threads may
        /// pause.</summary>
        internal static bool SolvingOffUiThread => s_offUiThread > 0;

        /// <summary>A Grasshopper component runs on the UI thread, inside the solution, whatever its
        /// script says: while it computes, Rhino and Grasshopper can't redraw or answer (a
        /// component computing for 15 s -- GH 8's Clock -- leaves them unresponsive for 15 s), and
        /// a pause in one of its threads would freeze Rhino (the freeze guard skips it). With
        /// "# async:true" in its script, its runs -- plain or in the debugger -- go to a thread of
        /// their own, as a Script Editor script's would: the UI thread waits with Rhino's message
        /// loop going, so Rhino's views, the canvas (pan, zoom, hover) and the editor answer, and
        /// the script's threads pause and step as in Rhino. Meanwhile the definition holds still
        /// (GrasshopperPauseGuard, GrasshopperPauseCanvas). The script mustn't use Rhino's UI or
        /// document where they need the UI thread -- what async:true means in the Script Editor
        /// too. Option Grasshopper > "async:true runs off the UI thread".</summary>
        private static bool RunsOffUiThread(object component)
        {
            if (!MiniVivisectionSettings.GrasshopperAsync || RhinoApp.InvokeRequired)
                return false;
            try
            {
                var context = ContextOf(component);
                var text = context?.GetType().GetMethod("GetText", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(context, null) as string;
                if (ScriptKinds.AsksForAsync(text) != true)
                    return false;
                // A solution started from Python on the UI thread (a script's NewSolution, say): that
                // Python holds the GIL while it waits for the solution, so the component's script
                // couldn't run on another thread -- both waited for good. On the UI thread, as before.
                if (PythonBridge.ThisThreadHoldsGil())
                {
                    if (!s_reportedGilHeld)
                    {
                        s_reportedGilHeld = true;
                        UserLog.Info("Grasshopper: '{0}' asks for async:true, but its solution was started from a Python script -- it runs on the UI thread this time.", NickName(component));
                    }
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper async run: {0}", Log.Describe(ex));
                return false;
            }
        }

        private static void SolveOffUiThread(object component, object da)
        {
            Exception failed = null;
            var done = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try
                {
                    s_solvingThread = PythonBridge.CurrentThreadIdent(); // Break now's stop: this thread now
                    s_callSolve(component, da);
                }
                catch (Exception ex)
                {
                    failed = ex;
                }
                finally
                {
                    done.Set();
                }
            })
            {
                IsBackground = true,
                Name = "miniVivisection: " + NickName(component),
            };
            thread.SetApartmentState(ApartmentState.STA);
            System.Threading.Interlocked.Increment(ref s_offUiThread);
            try
            {
                if (!s_reportedAsync)
                {
                    s_reportedAsync = true;
                    UserLog.Info("Grasshopper: '{0}' asks for async:true -- its runs go to a thread of their own: Rhino and Grasshopper stay usable while it computes, and its threads can pause.", NickName(component));
                }
                thread.Start();
                while (!done.Wait(15))
                    RhinoApp.Wait(); // Rhino's message loop goes on: the editor, pauses, the canvas
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref s_offUiThread);
            }
            if (failed != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed).Throw();
        }

        private static bool s_reportedAsync;
        private static bool s_reportedGilHeld;

        // Always debug's GetBuildKind postfix is on a generic member too: when a script component
        // ran but the postfix wasn't called, .NET recompiled it without the patch -- put back,
        // from the next solution on.
        private static bool s_reportedRepair;

        /// <summary>For the live test: how often Always debug's patch was put back.</summary>
        internal static int Repairs { get; private set; }

        // The script components in ComputeData now, innermost last: another definition's
        // component can solve inside this one's run (a message loop pumped by the script, by an
        // async:true wait, or by a pause), so one static isn't enough.
        private static readonly List<object> s_computing = new List<object>();

        private static void ComputePrefix(object __instance)
        {
            if (!IsScriptComponent(__instance))
                return;
            lock (s_computing)
                s_computing.Add(__instance);
            StateOf(__instance).BuildKindCalls = GrasshopperDebug.Calls;
            Rewiring.Follow(DocumentOf(__instance));
        }

        // A finalizer, not a postfix: ComputeData can throw (its own code, an iterator), and
        // the run's end must be seen then too (the hotkey's count, the stop record).
        private static Exception ComputeFinalizer(object __instance, Exception __exception)
        {
            bool mine;
            lock (s_computing)
            {
                var at = s_computing.LastIndexOf(__instance);
                mine = at >= 0;
                if (mine)
                    s_computing.RemoveAt(at);
            }
            if (!mine)
                return __exception;
            try
            {
                AfterSolve(__instance); // the component's solution is over
                lock (s_computing)
                {
                    // An outer component's run goes on: it is the running one again
                    if (s_running == null && s_computing.Count > 0)
                        s_running = s_computing[s_computing.Count - 1];
                }
                RepairAlwaysDebug(__instance);
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Breakpoints, "Grasshopper: after a component's solution: {0}", Log.Describe(ex));
            }
            return __exception;
        }

        private static long s_lastRepair;

        // Always debug's patch (GetBuildKind, on generic code .NET may recompile without it):
        // put back when a run that must have called it didn't -- not a Locked component, nor
        // one running its script input (neither gets that far), and not more than once a second.
        private static void RepairAlwaysDebug(object component)
        {
            try
            {
                if (!GrasshopperDebug.IsInstalled || GrasshopperDebug.Calls != StateOf(component).BuildKindCalls)
                    return;
                var runs = (int)(component.GetType().GetProperty("RunCount")?.GetValue(component) ?? 0);
                if (runs <= 0 || component.GetType().GetProperty("Locked")?.GetValue(component) as bool? == true || GrasshopperInputScripts.UsesInputScript(component))
                    return;
                if (Environment.TickCount64 - s_lastRepair < 1000)
                    return;
                s_lastRepair = Environment.TickCount64;
                Repairs++;
                GrasshopperDebug.Repatch();
                if (!s_reportedRepair)
                {
                    s_reportedRepair = true;
                    Log.Info(LogArea.Breakpoints, "Grasshopper: .NET recompiled the script components' code without Always debug's patch -- put back (from the next solution on)");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Breakpoints, "Grasshopper: putting Always debug's patch back: {0}", Log.Describe(ex));
            }
        }



        // --- each run -------------------------------------------------------------------------

        // SolveInstance(IGH_DataAccess da): which data this run has. False: skipped (stopped).
        private static bool SolvePrefix(object __instance, object da)
        {
            try
            {
                var state = StateOf(__instance);
                var iteration = (int)da.GetType().GetProperty("Iteration").GetValue(da);
                state.Solved = true;
                if (iteration == 0)
                {
                    state.Failures.Clear();
                    state.FailedRuns.Clear();
                    state.RunsPerData.Clear();
                    state.Stopping = false;
                    state.Stopped = null;
                    state.StoppedRun = null;
                    if (!state.HotkeyHeld)
                    {
                        state.HotkeyHeld = true;
                        BreakHotkey.RunStarted(); // Ctrl+Alt+B can stop it, editor or not
                    }
                }
                else if (state.Stopping)
                {
                    return false; // stopped: the remaining runs are skipped
                }
                state.Current = Describe(__instance, da);
                s_solvingThread = PythonBridge.CurrentThreadIdent();
                var data = state.Current.Item ?? state.Current.Branch;
                if (data != null)
                    state.RunsPerData[data] = state.RunsPerData.TryGetValue(data, out var runs) ? runs + 1 : 1;
                s_running = __instance;
            }
            catch (Exception ex)
            {
                if (!s_reportedRunProblem)
                {
                    s_reportedRunProblem = true;
                    UserLog.Warning("Grasshopper: a script component's run data can't be read ({0}) -- gh_run/gh_branch/gh_item and failing runs may be missing.", ScriptEditorHook.FirstLine(ex));
                }
                Log.Debug(LogArea.Breakpoints, "Grasshopper run: {0}", Log.Describe(ex));
            }
            return true;
        }

        private static bool s_reportedRunProblem;

        /// <summary>The fields of Grasshopper's GH_StructureIterator (the IGH_DataAccess of a
        /// run) that say which data the run has: per input its data, current branch and item.</summary>
        private sealed class IteratorFields
        {
            public FieldInfo InputData;  // IGH_Structure[]
            public FieldInfo ListIndex;  // int[]: branch index per input
            public FieldInfo ItemIndex;  // int[]: item index per input (item access)
            public FieldInfo ListLength; // int[]: the current branch's length per input
            public FieldInfo Principal;  // int: the principal input, or -1
        }

        private static readonly ConditionalWeakTable<Type, IteratorFields> s_iteratorFields = new ConditionalWeakTable<Type, IteratorFields>();

        private static IteratorFields FieldsOf(Type iterator) => s_iteratorFields.GetValue(iterator, type =>
        {
            const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
            return new IteratorFields
            {
                InputData = type.GetField("m_inputData", instance) ?? throw new MissingFieldException(type.FullName, "m_inputData"),
                ListIndex = type.GetField("m_listIndex", instance) ?? throw new MissingFieldException(type.FullName, "m_listIndex"),
                ItemIndex = type.GetField("m_itemIndex", instance) ?? throw new MissingFieldException(type.FullName, "m_itemIndex"),
                ListLength = type.GetField("m_listLength", instance),
                Principal = type.GetField("m_principalIndex", instance),
            };
        });

        /// <summary>A run's data: RunCount and every input's path (and index, for item access),
        /// as Grasshopper's iterator has them for this run. The run's branch and item come from
        /// the item input with the longest list -- the one that tells the runs apart (longest-list
        /// matching repeats the shorter lists' last item; Grasshopper's own principal input is the
        /// deepest path, which may be such a short list) -- then the deepest path, then the
        /// principal input.</summary>
        private static RunInfo Describe(object component, object da)
        {
            var info = new RunInfo { Run = (int)(component.GetType().GetProperty("RunCount")?.GetValue(component) ?? 0) };
            var parameters = component.GetType().GetProperty("Params")?.GetValue(component);
            var inputs = parameters?.GetType().GetProperty("Input")?.GetValue(parameters) as System.Collections.IList;
            if (inputs == null)
                return info;
            var fields = FieldsOf(da.GetType());
            var data = fields.InputData.GetValue(da) as Array;
            var listIndex = fields.ListIndex.GetValue(da) as int[];
            var itemIndex = fields.ItemIndex.GetValue(da) as int[];
            var principal = fields.Principal?.GetValue(da) as int? ?? -1;
            var listLength = fields.ListLength?.GetValue(da) as int[];
            if (data == null || listIndex == null || itemIndex == null)
                return info;
            var parts = new List<string>();
            var best = (Item: false, Items: -1, Length: -1, Principal: false);
            for (var i = 0; i < inputs.Count && i < data.Length && i < listIndex.Length; i++)
            {
                var param = inputs[i];
                var name = param.GetType().GetProperty("NickName")?.GetValue(param) as string ?? "input " + i;
                var access = param.GetType().GetProperty("Access")?.GetValue(param)?.ToString() ?? "item";
                if (access == "tree")
                {
                    parts.Add(name + " (tree)");
                    continue;
                }
                var structure = data.GetValue(i);
                // IList<GH_Path> (a SortedList's keys: no non-generic IList)
                var paths = (structure?.GetType().GetProperty("Paths")?.GetValue(structure) as System.Collections.IEnumerable)?.Cast<object>().ToList();
                if (paths == null || paths.Count == 0 || listIndex[i] < 0 || listIndex[i] >= paths.Count)
                    continue; // nothing connected
                var path = paths[listIndex[i]];
                var pathText = path?.ToString() ?? "";
                var length = (path?.GetType().GetProperty("Length")?.GetValue(path) as int?) ?? 0;
                var index = access == "item" && i < itemIndex.Length ? itemIndex[i] : (int?)null;
                parts.Add(name + " " + pathText + (index.HasValue ? "[" + index + "]" : ""));
                var items = listLength != null && i < listLength.Length ? listLength[i] : 0;
                var rank = (Item: index.HasValue, Items: items, Length: length, Principal: i == principal);
                if (rank.CompareTo(best) > 0)
                {
                    best = rank;
                    info.Branch = pathText;
                    info.Item = index.HasValue ? pathText + "[" + index + "]" : null;
                }
            }
            info.Inputs = parts.Count > 0 ? string.Join(", ", parts) : null;
            return info;
        }

        // GH_ActiveObject.AddRuntimeMessage(level, text): a script component reports a failing run
        // as an Error, "Error running script: <message> [line:column]" -- or one stopped (Ctrl+Alt+B).
        private static void MessagePostfix(object __instance, object level, string text)
        {
            try
            {
                if (level?.ToString() != "Error" || text == null || !ReferenceEquals(s_running, __instance))
                    return; // not in one of its runs (before the solve, a rebuild): no run to name
                var state = StateOf(__instance);
                if (state.Current == null)
                    return;
                if (text.IndexOf("DebugStop", StringComparison.OrdinalIgnoreCase) >= 0)
                    return; // Stop in the debugger: not a failure of the run
                var at = Regex.Match(text, @"\[(\d+):\d+\]\s*$");
                var line = at.Success ? int.Parse(at.Groups[1].Value) : 0;
                if (state.Stopping && state.StoppedRun == null)
                {
                    state.StoppedRun = state.Current;
                    return;
                }
                if (!state.FailedRuns.Add(state.Current.Run))
                    return;
                var message = (at.Success ? text.Substring(0, at.Index) : text).Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
                const string prefix = "Error running script:";
                if (message.StartsWith(prefix, StringComparison.Ordinal))
                    message = message.Substring(prefix.Length).Trim();
                state.Failures.Add(new Failure { Run = state.Current, Line = line, Message = message });
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper run error: {0}", Log.Describe(ex));
            }
        }

        // After the component's solution (ComputeData): what failed, or where it stopped.
        private static void AfterSolve(object __instance)
        {
            if (ReferenceEquals(s_running, __instance))
                s_running = null;
            var held = StateOf(__instance);
            if (held.HotkeyHeld)
            {
                held.HotkeyHeld = false;
                BreakHotkey.RunEnded(); // only the run this solution started
            }
            try
            {
                var state = StateOf(__instance);
                if (!state.Solved)
                {
                    // No run this time (a required input empty): the last solution's failures are over.
                    state.Failures.Clear();
                    state.FailedRuns.Clear();
                    state.Current = null;
                    return;
                }
                state.Solved = false;
                var name = NickName(__instance);
                // The stopping thread records where the script was right after raising the
                // interrupt; the script can get here first.
                for (var wait = 0; state.Stopping && state.Stopped == null && state.StoppedRun != null && wait < 25; wait++)
                    System.Threading.Thread.Sleep(20);
                if (state.Stopping && state.Stopped != null)
                {
                    var stopped = state.Stopped;
                    var run = state.StoppedRun ?? state.Current;
                    var inputScript = GrasshopperInputScripts.UsesInputScript(__instance);
                    AddRemark(__instance, "miniVivisection: stopped by " + Hotkey() + " in run " + run?.Run + (string.IsNullOrEmpty(run?.Item ?? run?.Branch) ? "" : " (" + (run?.Item ?? run?.Branch) + ")") + " at line " + stopped.Line +
                        (inputScript
                            ? ". Its script opens at that line (a Panel's in the Panel); a script from its input can't run in the debugger yet."
                            : ". Its editor opens at that line; Debug runs it again in the debugger."));
                    var record = new StoppedRuns.Record
                    {
                        Kind = "stopped",
                        Time = DateTime.Now,
                        Title = name,
                        Line = stopped.Line,
                        Function = stopped.Function,
                        Stack = stopped.Stack,
                        Values = stopped.Values,
                        Component = name,
                        RunNumber = run?.Run ?? 0,
                        Branch = run?.Branch,
                        Item = run?.Item,
                        Inputs = run?.Inputs,
                        InputScript = inputScript,
                    };
                    UserLog.Info("Grasshopper: '{0}' stopped in run {1}{2} at line {3}.", name, record.RunNumber,
                        string.IsNullOrEmpty(record.Item ?? record.Branch) ? "" : " (" + (record.Item ?? record.Branch) + ")", record.Line);
                    var component = __instance;
                    RhinoApp.InvokeOnUiThread(new Action(() => OpenAtLine(component, record)));
                    return;
                }
                if (state.Failures.Count == 0)
                    return;
                var first = state.Failures[0];
                var where = first.Run.Item ?? first.Run.Branch;
                var text = state.Failures.Count == 1
                    ? "miniVivisection: run " + first.Run.Run + " failed -- " + (where ?? "its data") + (first.Line > 0 ? ", line " + first.Line : "") +
                      (first.Run.Inputs != null ? " (" + first.Run.Inputs + ")" : "") + "."
                    : "miniVivisection: " + state.Failures.Count + " runs failed -- " +
                      string.Join(", ", state.Failures.Take(6).Select(f => (f.Run.Item ?? f.Run.Branch ?? "run " + f.Run.Run))) + (state.Failures.Count > 6 ? ", ..." : "") + ".";
                // A component running its script input can't debug a run (no such menu item)
                AddRemark(__instance, text + (GrasshopperInputScripts.UsesInputScript(__instance)
                    ? " Right-click: Copy failing branches."
                    : VNames.Text(" Right-click: Copy failing branches, or Debug failing run ") + first.Run.Run + "."));
                Log.Info(LogArea.Breakpoints, "'{0}': {1}", name, text);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper run summary: {0}", Log.Describe(ex));
            }
        }

        private static void AddRemark(object component, string text)
        {
            var levels = component.GetType().Assembly.GetType("Grasshopper.Kernel.GH_RuntimeMessageLevel")
                ?? AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Grasshopper").GetType("Grasshopper.Kernel.GH_RuntimeMessageLevel");
            component.GetType().GetMethod("AddRuntimeMessage", BindingFlags.Public | BindingFlags.Instance)?.Invoke(component, new[] { Enum.Parse(levels, "Remark"), text });
        }

        internal static object DocumentOf(object component) =>
            component.GetType().GetMethod("OnPingDocument", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(component, null);

        internal static string NickName(object component) => component.GetType().GetProperty("NickName")?.GetValue(component) as string ?? "script";

        private static string Hotkey() => MiniVivisectionSettings.BreakHotkey == "Off" ? "Break now" : MiniVivisectionSettings.BreakHotkey;

        // --- which run is paused ----------------------------------------------------------------

        /// <summary>At a pause: the Grasshopper run it's in -- "'Py3' run 4 · {0;3}[2] · x {0;3}[2],
        /// y {0}" -- or null (not in a script component's run).</summary>
        public static string PausedRun()
        {
            var component = s_running;
            if (component == null)
                return null;
            var run = StateOf(component).Current;
            if (run == null)
                return null;
            var parts = new List<string> { "'" + NickName(component) + "' run " + run.Run };
            if ((run.Item ?? run.Branch) != null)
                parts.Add(run.Item ?? run.Branch);
            if (run.Inputs != null)
                parts.Add(run.Inputs);
            return string.Join(" \u00b7 ", parts);
        }

        /// <summary>The run badge in <paramref name="editor"/>'s code view (top, centered): the
        /// paused run, or gone (null).</summary>
        public static void ShowBadge(object editor, string text)
        {
            if (editor == null)
                return;
            RhinoApp.InvokeOnUiThread(new Action(async () =>
            {
                try
                {
                    if (new ScriptEditorAccess(editor).Monaco is WebView web)
                        await HoverPreview.Script(web, BadgeScript(text));
                }
                catch (Exception ex)
                {
                    Log.Trace(LogArea.Breakpoints, "run badge: {0}", Log.Describe(ex));
                }
            }));
        }

        /// <summary>The badge's script: a Monaco overlay widget built from a text node.</summary>
        internal static string BadgeScript(string text) => @"try {
" + CodeViewScripts.FindEditor + @"
var text = " + System.Text.Json.JsonSerializer.Serialize(text) + @";
var old = globalThis.__mvRunBadge;
if (old) { try { editor.removeOverlayWidget(old); } catch (e) {} globalThis.__mvRunBadge = null; }
if (!text) return 'badge removed';
var dark = !!(editor.getDomNode() && editor.getDomNode().closest('.vs-dark, .hc-black'));
var node = document.createElement('div');
node.textContent = 'Grasshopper: ' + text;
node.title = 'The run of the component this pause is in: run number (ghenv.Component.RunCount), its branch or item, every input’s path.';
node.style.cssText = 'margin-top: 6px; padding: 3px 12px; border-radius: 12px; font: 600 12px Segoe UI, sans-serif; white-space: nowrap;' +
  'box-shadow: 0 2px 8px rgba(0,0,0,.25); border: 1px solid ' + (dark ? '#5a4a1a' : '#e3c26a') + ';' +
  (dark ? 'background: #3a3220; color: #f3dfa2;' : 'background: #fff6d8; color: #4a3a08;');
var widget = {
  getId: function () { return 'minivivisection.run'; },
  getDomNode: function () { return node; },
  getPosition: function () { return { preference: monaco.editor.OverlayWidgetPositionPreference.TOP_CENTER }; }
};
editor.addOverlayWidget(widget);
globalThis.__mvRunBadge = widget;
return 'badge shown';
} catch (e) { return 'error: ' + e; }
";

        // --- Ctrl+Alt+B with the editor closed --------------------------------------------------

        /// <summary>Break now's hotkey with no debug run (its own thread): a script component
        /// running now is stopped. Null: none runs; true: stopping it; false: it can't be.</summary>
        public static bool? Stop()
        {
            var component = s_running;
            if (component == null)
                return null;
            if (!MiniVivisectionSettings.GrasshopperStops)
            {
                UserLog.Info("Break now ({0}): '{1}' runs, but Grasshopper script components are on Normal -- Stop and record (the toolbar button) stops it, Always debug pauses it.",
                    Hotkey(), NickName(component));
                return false;
            }
            var state = StateOf(component);
            if (state.Stopping)
                return true;
            var code = CodeOf(component);
            var python3 = component.GetType().Name == "Python3Component" || IsPython3(code);
            if (!python3 || component.GetType().Name == "IronPython2Component")
            {
                UserLog.Info("Break now ({0}): '{1}' isn't Python 3 ({2}) -- open its script editor to pause it in the debugger.", Hotkey(), NickName(component),
                    code?.Language?.Id.ToString() ?? "no code");
                return false;
            }
            state.Stopping = true;
            var attempt = state.Attempt = new StoppedRuns.StopAttempt();
            var thread = s_solvingThread;
            UserLog.Info("Break now ({0}): stopping '{1}' -- its editor then opens at the line.", Hotkey(), NickName(component));
            Task.Run(async () =>
            {
                try
                {
                    var document = component.GetType().GetMethod("OnPingDocument", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(component, null);
                    document?.GetType().GetMethod("RequestAbortSolution")?.Invoke(document, null); // the rest of the solution
                    // The solution's thread only: its innermost code that isn't a library's (the
                    // component's code has no file). Never another script's thread.
                    bool StillWanted() => !attempt.Abandoned && ReferenceEquals(s_running, component);
                    var stopping = Task.Run(() => PythonBridge.StopScript("*", true, thread, StillWanted, where => state.Stopped = where));
                    var first = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
                    if (first != stopping)
                        attempt.Abandoned = true;
                    if (first != stopping || stopping.IsFaulted || stopping.Result == null)
                    {
                        if (first == stopping)
                            state.Stopped = null;
                        state.Stopping = false;
                        UserLog.Warning("Break now: couldn't stop '{0}' ({1}).", NickName(component),
                            first != stopping ? "Python is busy in a long call into native code -- press it again" :
                            stopping.IsFaulted ? ScriptEditorHook.FirstLine(stopping.Exception?.InnerException ?? stopping.Exception) : "no script code running");
                        return;
                    }
                    Log.Info(LogArea.Breakpoints, "'{0}' stopped at line {1} ({2})", NickName(component), state.Stopped.Line, state.Stopped.Function);
                }
                catch (Exception ex)
                {
                    state.Stopping = false;
                    Log.Warning(LogArea.Breakpoints, "stopping a Grasshopper script: {0}", Log.Describe(ex));
                }
            });
            return true;
        }

        /// <summary>Python 3 (CPython through pythonnet: "mcneel.pythonnet.python@3.9.10"), not
        /// IronPython 2 ("...ironpython...@2.7").</summary>
        internal static bool IsPython3(Code code)
        {
            var id = code?.Language?.Id.ToString()?.ToLowerInvariant() ?? "";
            return !id.Contains("ironpython") && (id.Contains("pythonnet") || id.Contains("python@3"));
        }

        // --- the component's editor -----------------------------------------------------------

        /// <summary>The component's ScriptContext: a protected field of BaseScriptComponent
        /// ("Context"), not a property.</summary>
        internal static object ContextOf(object component)
        {
            for (var type = component.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField("Context", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field.GetValue(component);
                var property = type.GetProperty("Context", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (property != null)
                    return property.GetValue(component);
            }
            return null;
        }

        /// <summary>Whether a script component runs now (in SolveInstance) outside the debugger: none
        /// of <paramref name="debugging"/> is its code's -- for one running its script input, the
        /// input's debug run's (GrasshopperInputScripts.DebuggingControls). Any thread.</summary>
        internal static bool RunsPlainly(ICollection<IDebugControls> debugging)
        {
            var component = s_running;
            if (component == null)
                return false;
            try
            {
                var controls = GrasshopperInputScripts.UsesInputScript(component)
                    ? GrasshopperInputScripts.DebuggingControls
                    : CodeOf(component)?.DebugControls;
                return controls == null || !debugging.Contains(controls);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper: is the running component debugged? {0}", Log.Describe(ex));
                return false;
            }
        }

        internal static Code CodeOf(object component)
        {
            var context = ContextOf(component);
            var tryGetCode = context?.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "TryGetCode" && m.GetParameters().Length == 1);
            if (tryGetCode == null)
                return null;
            var args = new object[] { null };
            return (bool)tryGetCode.Invoke(context, args) ? args[0] as Code : null;
        }

        internal static bool HasVisibleEditor(object component)
        {
            var context = ContextOf(component);
            var editor = context?.GetType().GetProperty("ScriptEditor", Any)?.GetValue(context);
            return editor?.GetType().GetProperty("HasVisibleEditor", Any)?.GetValue(editor) as bool? == true;
        }

        internal static void OpenEditor(object component)
        {
            var context = ContextOf(component);
            context?.GetType().GetMethod("OpenScript", Any, null, Type.EmptyTypes, null)?.Invoke(context, null);
        }

        /// <summary>The editor window showing <paramref name="code"/>, once it's there.</summary>
        private static object EditorFor(Code code)
        {
            foreach (var window in Application.Instance.Windows)
            {
                try
                {
                    if (window.Visible && new ScriptEditorAccess(window).Code?.Uri == code.Uri)
                        return window;
                }
                catch (Exception)
                {
                    // not a Script Editor
                }
            }
            return null;
        }

        /// <summary><paramref name="then"/> once the component's editor shows its code and its
        /// debugger is there (up to 15 s).</summary>
        private static void WhenEditorReady(object component, Action<Code, object> then)
        {
            OpenEditor(component);
            var tries = 0;
            var timer = new UITimer { Interval = 0.3 };
            timer.Elapsed += (s, e) =>
            {
                try
                {
                    var code = CodeOf(component);
                    var editor = code == null ? null : EditorFor(code);
                    if (code != null && code.HasDebugControls && editor != null && HasVisibleEditor(component))
                    {
                        timer.Stop();
                        then(code, editor);
                    }
                    else if (++tries > 50)
                    {
                        timer.Stop();
                        UserLog.Warning("'{0}': its script editor didn't open in time.", NickName(component));
                    }
                }
                catch (Exception ex)
                {
                    timer.Stop();
                    Log.Warning(LogArea.Breakpoints, "the component's editor: {0}", Log.Describe(ex));
                }
            };
            timer.Start();
        }

        /// <summary>After a stop: the editor at the line, with the overlay (no breakpoint).</summary>
        private static void OpenAtLine(object component, StoppedRuns.Record record)
        {
            if (GrasshopperInputScripts.UsesInputScript(component))
            {
                GrasshopperInputScripts.Open(component, record); // no editor of its own: the input's script, as a file
                return;
            }
            WhenEditorReady(component, (code, editor) => StoppedRuns.ShowOverlay(editor, code, record));
        }

        /// <summary>The component recomputed in the debugger: paused at <paramref name="line"/>
        /// where <paramref name="condition"/> holds (a one-shot breakpoint), or at its first line.</summary>
        private static void DebugComponent(object component, int line, string condition, StoppedRuns.Record overlay)
        {
            if (DocumentOf(component) == null)
            {
                UserLog.Info("'{0}' isn't in a Grasshopper definition any more.", NickName(component));
                return;
            }
            WhenEditorReady(component, (code, editor) =>
            {
                if (DocumentOf(component) == null)
                    return; // deleted while its editor opened
                if (line > 0)
                    StoppedRuns.AddOneShotBreakpoint(code, line, condition);
                else if (!code.DebugControls.Breakpoints.WhereReferences(code).Any())
                    Threads.RequestBreak(code.DebugControls, 0); // its first line
                if (overlay != null)
                    StoppedRuns.ShowOverlay(editor, code, overlay);
                var scriptObject = component.GetType().GetInterfaces().FirstOrDefault(i => i.Name == "IScriptObject");
                var recompute = scriptObject?.GetMethod("ReCompute", new[] { typeof(BuildKind) });
                recompute?.Invoke(component, new object[] { BuildKind.Debug });
                UserLog.Info("'{0}' recomputes in the debugger{1}.", NickName(component),
                    line > 0 ? " -- it pauses at line " + line + (condition != null ? " where " + condition : "") : "");
            });
        }

        // --- the component's menu -----------------------------------------------------------------

        /// <summary>RhinoCode's "Open Script Editor" and "Export Script", grayed out for a
        /// component running its script input: hidden, and items that work in their places -- its
        /// script and its library's files in the Script Editor (as its double-click), and its
        /// script saved to a file. WinForms by reflection (the .rhp doesn't reference it).</summary>
        private static void ReplaceGrayedItems(object menu, object component, MethodInfo appendItem)
        {
            try
            {
                if (!(menu.GetType().GetProperty("Items")?.GetValue(menu) is IList items))
                    return;
                var replacements = new (string Text, string Tip, Action Click)[]
                {
                    ("Open Script Editor", "Its script (from the Panel, or the file) and its library's files in the Script Editor -- the script in front.",
                        () => GrasshopperInputScripts.OpenWithLibraries(component)),
                    ("Export Script", "Its script, saved to a file of your choice.", () => GrasshopperInputScripts.Export(component)),
                };
                foreach (var (text, tip, click) in replacements)
                {
                    var original = items.Cast<object>().FirstOrDefault(i =>
                        (i.GetType().GetProperty("Text")?.GetValue(i) as string)?.Replace("&", "") == text);
                    if (original == null)
                        continue;
                    var index = items.IndexOf(original);
                    var label = original.GetType().GetProperty("Text").GetValue(original);
                    var replacement = appendItem.Invoke(null, new[] { menu, label, (EventHandler)((s, e) => click()), (object)true });
                    items.Remove(replacement);
                    items.Insert(index, replacement);
                    replacement.GetType().GetProperty("Font")?.SetValue(replacement, original.GetType().GetProperty("Font")?.GetValue(original));
                    replacement.GetType().GetProperty("ToolTipText")?.SetValue(replacement, tip);
                    original.GetType().GetProperty("Visible")?.SetValue(original, false);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "the component's menu: Open Script Editor, Export Script: {0}", Log.Describe(ex));
            }
        }

        // AppendAdditionalComponentMenuItems(menu): miniVivisection's items.
        private static void MenuPostfix(object __instance, object menu)
        {
            try
            {
                if (!MiniVivisectionSettings.PluginOn)
                    return;
                var documentObject = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Grasshopper").GetType("Grasshopper.Kernel.GH_DocumentObject");
                var separator = documentObject.GetMethods(BindingFlags.Public | BindingFlags.Static).First(m => m.Name == "Menu_AppendSeparator");
                var item = documentObject.GetMethods(BindingFlags.Public | BindingFlags.Static).First(m => m.Name == "Menu_AppendItem" && m.GetParameters().Length == 4 &&
                    m.GetParameters()[2].ParameterType == typeof(EventHandler) && m.GetParameters()[3].ParameterType == typeof(bool));
                // Every item "V° ..." (the chosen mark), as all of the plug-in's Grasshopper menu items
                void Add(string text, Action click, bool enabled = true) =>
                    item.Invoke(null, new[] { menu, VNames.Menu(text), (EventHandler)((s, e) => click()), (object)enabled });
                separator.Invoke(null, new[] { menu });
                var component = __instance;
                if (GrasshopperInputScripts.UsesInputScript(component))
                {
                    ReplaceGrayedItems(menu, component, item);
                    Add("Edit the script input in the Script Editor", () => GrasshopperInputScripts.Open(component));
                    // Its script in the debugger once: opened, then recomputed, pausing at its
                    // breakpoints or its first line (GrasshopperInputScripts.TryDebugRun)
                    Add("Debug the script input", () => GrasshopperInputScripts.DebugOnce(component));
                }
                else
                    Add("Debug this component", () => DebugComponent(component, 0, null, null));
                if (GrasshopperInputScripts.HasLibrary(component))
                    Add("Open the library's files in the Script Editor", () => GrasshopperInputScripts.OpenLibraries(component));
                var failures = StateOf(component).Failures.ToList();
                if (failures.Count == 0)
                    return;
                var first = failures[0];
                // Two ways to find that run again: by its data (its tree path) -- only when no
                // other run had the same -- or by its number. Not for a script input: that
                // script can't run in the debugger (DebugOnce).
                if (!GrasshopperInputScripts.UsesInputScript(component))
                {
                    var state = StateOf(component);
                    var firstData = first.Run.Item ?? first.Run.Branch;
                    var byData = firstData != null && state.RunsPerData.TryGetValue(firstData, out var sameData) && sameData == 1 ? DataCondition(first.Run) : null;
                    var byNumber = "gh_run == " + first.Run.Run;
                    var at = first.Line > 0 ? ", line " + first.Line : "";
                    if (byData != null)
                        Add("Debug failing run " + first.Run.Run + " -- data " + (first.Run.Item ?? first.Run.Branch) + at,
                            () => DebugComponent(component, first.Line, byData, FailureRecord(component, first, byData)), first.Line > 0);
                    Add("Debug failing run " + first.Run.Run + " -- run number" + at,
                        () => DebugComponent(component, first.Line, byNumber, FailureRecord(component, first, byNumber)), first.Line > 0);
                }
                var branches = failures.Select(f => f.Run.Branch).Where(b => b != null).Distinct().ToList();
                var items = failures.Select(f => f.Run.Item).Where(i => i != null).Distinct().ToList();
                Add("Copy failing branches (" + branches.Count + ")", () => Copy(branches, "branch paths"), branches.Count > 0);
                Add("Copy failing items (" + items.Count + ")", () => Copy(items, "items"), items.Count > 0);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper menu: {0}", Log.Describe(ex));
            }
        }

        /// <summary>A condition finding a run again by its data: gh_item == "{0;3}[2]" for an item
        /// input, gh_branch == "{0;3}" else; null without a path.</summary>
        internal static string DataCondition(RunInfo run) =>
            run.Item != null ? "gh_item == \"" + run.Item + "\"" : run.Branch != null ? "gh_branch == \"" + run.Branch + "\"" : null;

        private static StoppedRuns.Record FailureRecord(object component, Failure failure, string condition) => new StoppedRuns.Record
        {
            BreakpointSet = true, // Debug failing run N sets one, for that run
            Condition = condition,
            Kind = "error",
            Time = DateTime.Now,
            Title = NickName(component),
            Line = failure.Line,
            Error = failure.Message,
            Component = NickName(component),
            RunNumber = failure.Run.Run,
            Branch = failure.Run.Branch,
            Item = failure.Run.Item,
            Inputs = failure.Run.Inputs,
        };

        /// <summary>One path per line: a Panel turns it into a list of paths.</summary>
        private static void Copy(List<string> lines, string what)
        {
            Clipboard.Instance.Text = string.Join(Environment.NewLine, lines);
            UserLog.Info("Copied {0} failing {1}: {2} -- paste into a Panel, and feed Tree Branch's Path (or V Edit's Index).", lines.Count, what, string.Join(", ", lines.Take(8)));
        }
    }
}
