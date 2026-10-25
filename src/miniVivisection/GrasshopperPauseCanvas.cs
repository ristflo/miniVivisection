using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MiniVivisection
{
    /// <summary>
    /// Grasshopper's canvas while one of its script components is paused (a breakpoint, Break
    /// now, a runtime error under Always debug): the pause is inside the solution, and the
    /// canvas ignores the mouse while its document solves (GH_Canvas.Canvas_MouseMove returns
    /// at once when SolutionState is Process) -- no panning, dragging, hovering.
    ///
    /// - The canvas's mouse handling sees the paused solution as finished: a transpiler on
    ///   Canvas_MouseMove (GH_Canvas, not generic) asks <see cref="StateForCanvas"/> instead of
    ///   SolutionState. Only there: the rest of Grasshopper still sees it solving. Recomputes
    ///   asked for meanwhile (a slider, a wire) wait for the solution's end (GrasshopperPauseGuard).
    /// - A new solution can't start inside the paused one: GH_Document.NewSolution is held back
    ///   (done when the solution ends, with the guard's replay). Grasshopper's own check is
    ///   SolutionState, and adding objects resets it: AddObject and MergeDocument (a paste, the
    ///   search box, an Alt-drag copy) set it to PreProcess -- put back to Process after them.
    /// - Deleting objects waits too: refused while paused (GH_Document.RemoveObject/RemoveObjects),
    ///   as the solution may still need them -- the paused component itself, say. Except when
    ///   the definition is being closed: then its objects go, and the script is told.
    /// </summary>
    internal static class GrasshopperPauseCanvas
    {
        private static Harmony s_harmony;
        private static string s_error;
        private static bool s_listening;
        private static MethodInfo s_solutionState;
        private static Func<object, int> s_stateOf;
        private static FieldInfo s_state;    // GH_Document._state
        private static object s_process;     // GH_ProcessStep.Process
        private static int s_finished;       // GH_ProcessStep.PostProcess
        private static PropertyInfo s_context; // GH_Document.Context
        private static bool s_reportedDelete, s_reportedSolution;

        /// <summary>For the health line (EditorHooks): null when installed or Grasshopper isn't loaded yet.</summary>
        internal static string HookProblem => s_error;

        /// <summary>For the live test: deletions refused while paused.</summary>
        internal static int Refused { get; private set; }

        /// <summary>For the live test: new solutions held back while paused.</summary>
        internal static int HeldSolutions { get; private set; }

        public static void Start()
        {
            if (!s_listening)
            {
                s_listening = true;
                AppDomain.CurrentDomain.AssemblyLoad += (sender, e) =>
                {
                    if (e.LoadedAssembly.GetName().Name == "Grasshopper")
                        TryInstall(e.LoadedAssembly);
                };
            }
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Grasshopper");
            if (loaded != null)
                TryInstall(loaded);
        }

        private static void TryInstall(Assembly grasshopper)
        {
            if (s_harmony != null || s_error != null)
                return;
            try
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var document = grasshopper.GetType("Grasshopper.Kernel.GH_Document", true);
                var canvas = grasshopper.GetType("Grasshopper.GUI.Canvas.GH_Canvas", true);
                var step = grasshopper.GetType("Grasshopper.Kernel.GH_ProcessStep", true);
                s_solutionState = document.GetProperty("SolutionState")?.GetGetMethod() ?? throw new MissingMethodException(document.FullName, "SolutionState");
                s_finished = (int)Enum.Parse(step, "PostProcess");
                s_process = Enum.Parse(step, "Process");
                s_state = document.GetField("_state", any) ?? throw new MissingFieldException(document.FullName, "_state");
                s_context = document.GetProperty("Context") ?? throw new MissingMemberException(document.FullName, "Context");
                var target = Expression.Parameter(typeof(object));
                s_stateOf = Expression.Lambda<Func<object, int>>(
                    Expression.Convert(Expression.Property(Expression.Convert(target, document), "SolutionState"), typeof(int)), target).Compile();
                var mouseMove = canvas.GetMethod("Canvas_MouseMove", any) ?? throw new MissingMethodException(canvas.FullName, "Canvas_MouseMove");
                var removes = document.GetMethods(any).Where(m => m.Name == "RemoveObject" || m.Name == "RemoveObjects").ToList();
                if (removes.Count == 0)
                    throw new MissingMethodException(document.FullName, "RemoveObject");
                // Every target first, then the patches: a missing one leaves none applied.
                MethodInfo One(string name, int parameters) => document.GetMethods(any).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameters)
                    ?? throw new MissingMethodException(document.FullName, name + "/" + parameters);
                var adders = new[] { One("AddObject", 3), One("MergeDocument", 3) };
                var newSolution = One("NewSolution", 2);
                var harmony = new Harmony("minivivisection.grasshopperpausecanvas");
                try
                {
                    harmony.Patch(mouseMove, transpiler: new HarmonyMethod(typeof(GrasshopperPauseCanvas), nameof(AskStateForCanvas)));
                    foreach (var remove in removes)
                        harmony.Patch(remove, prefix: new HarmonyMethod(typeof(GrasshopperPauseCanvas),
                            remove.ReturnType == typeof(int) ? nameof(RemoveObjectsPrefix) : nameof(RemoveObjectPrefix)));
                    foreach (var adder in adders)
                        harmony.Patch(adder, prefix: new HarmonyMethod(typeof(GrasshopperPauseCanvas), nameof(KeepStatePrefix)),
                            postfix: new HarmonyMethod(typeof(GrasshopperPauseCanvas), nameof(KeepStatePostfix)));
                    harmony.Patch(newSolution, prefix: new HarmonyMethod(typeof(GrasshopperPauseCanvas), nameof(NewSolutionPrefix)));
                    // The transpiler found what it looks for? Else the canvas stays blocked, and it must say so.
                    if (s_stateAsksReplaced == 0)
                        throw new MissingMethodException(canvas.FullName, "SolutionState in Canvas_MouseMove (Grasshopper changed?)");
                }
                catch
                {
                    harmony.UnpatchAll(harmony.Id); // all or none
                    throw;
                }
                s_harmony = harmony;
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                s_error = ex.Message;
                UserLog.Warning("Grasshopper's canvas stays as it is while a script is paused (no panning or dragging): {0}", ex.Message);
            }
        }

        // Canvas_MouseMove: "document.SolutionState" becomes "StateForCanvas(document)".
        private static IEnumerable<CodeInstruction> AskStateForCanvas(IEnumerable<CodeInstruction> instructions)
        {
            var ask = typeof(GrasshopperPauseCanvas).GetMethod(nameof(StateForCanvas), BindingFlags.NonPublic | BindingFlags.Static);
            var replaced = 0;
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && instruction.operand is MethodInfo called && called == s_solutionState)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = ask;
                    replaced++;
                }
                yield return instruction;
            }
            s_stateAsksReplaced = replaced;
        }

        private static int s_stateAsksReplaced; // the transpiler: calls replaced

        /// <summary>The document's SolutionState for the canvas's mouse: finished while one of its
        /// script components is paused.</summary>
        private static int StateForCanvas(object document)
        {
            try
            {
                if (IsPausedIn(document))
                    return s_finished;
            }
            catch (Exception)
            {
                // as it is
            }
            return s_stateOf(document);
        }

        /// <summary>Whether a script component of <paramref name="document"/> holds its solution
        /// without the UI thread now: paused, or computing on a thread of its own (# async:true,
        /// GrasshopperRuns.SolvingOffUiThread) -- the canvas works meanwhile (UI thread).</summary>
        internal static bool IsPausedIn(object document) =>
            document != null && ReferenceEquals(GrasshopperRuns.RunningDocument, document) &&
            (GrasshopperPauseGuard.AnyPaused() || GrasshopperRuns.SolvingOffUiThread);

        /// <summary>Whether <paramref name="document"/>'s solution is going on in one of its script
        /// components now -- paused, on its own thread, or pumping messages (RhinoApp.Wait): nothing
        /// may start a new solution of it meanwhile.</summary>
        internal static bool HoldsSolution(object document) =>
            document != null && !GrasshopperPauseGuard.Replaying && ReferenceEquals(GrasshopperRuns.RunningDocument, document);

        // AddObject/MergeDocument: the solution's state kept (they set PreProcess) while a script
        // of the document is in its solution.
        private static void KeepStatePrefix(object __instance, out bool __state)
        {
            __state = false;
            try
            {
                __state = HoldsSolution(__instance) && s_stateOf(__instance) == (int)s_process;
            }
            catch (Exception)
            {
                // not kept
            }
        }

        private static void KeepStatePostfix(object __instance, bool __state)
        {
            if (!__state)
                return;
            try
            {
                if (s_stateOf(__instance) != (int)s_process)
                {
                    s_state.SetValue(__instance, s_process);
                    Log.Info(LogArea.Breakpoints, "pause guard: objects added while a script of the definition is in its solution -- the solution's state kept");
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "pause guard, keeping the state: {0}", Log.Describe(ex));
            }
        }

        // NewSolution(expireAllObjects, mode): held back while a script of the document is in its
        // solution (done when it ends, with the pause guard's replay).
        private static bool NewSolutionPrefix(object __instance, bool __0)
        {
            try
            {
                if (!HoldsSolution(__instance))
                    return true;
                HeldSolutions++;
                GrasshopperPauseGuard.HoldNewSolution(__instance, __0);
                if (!s_reportedSolution)
                {
                    s_reportedSolution = true;
                    UserLog.Info("Grasshopper: a new solution was asked for while a script is in this definition's solution -- done when it ends.");
                }
                Log.Info(LogArea.Breakpoints, "held back NewSolution({0}) during a script's solution", __0);
                return false;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "pause guard, NewSolution: {0}", Log.Describe(ex));
                return true;
            }
        }

        private static bool RemoveObjectPrefix(object __instance, ref bool __result)
        {
            if (!Refuse(__instance))
                return true;
            __result = false;
            return false;
        }

        private static bool RemoveObjectsPrefix(object __instance, ref int __result)
        {
            if (!Refuse(__instance))
                return true;
            __result = 0;
            return false;
        }

        private static bool Refuse(object document)
        {
            bool paused;
            try
            {
                paused = IsPausedIn(document);
                if (paused && s_context.GetValue(document)?.ToString() == "Close")
                {
                    // The definition closed under its paused script: its objects go (the
                    // document is being disposed); the script's run ends with Continue or Stop.
                    UserLog.Warning("Grasshopper: the definition was closed while a script of it is paused or computes -- Continue or Stop the script; it has no definition any more.");
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
            if (!paused)
            {
                s_reportedDelete = false;
                s_reportedSolution = false;
                return false;
            }
            Refused++;
            if (!s_reportedDelete)
            {
                s_reportedDelete = true;
                UserLog.Warning("Grasshopper: deleting isn't possible while a script of this definition is paused or computes (async:true) -- its solution may still need the objects. Continue or Stop it first, or let it finish.");
            }
            return true;
        }
    }
}
