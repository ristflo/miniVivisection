using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Rhino;

namespace MiniVivisection
{
    /// <summary>
    /// A script component paused in the debugger is paused inside a Grasshopper solution; with
    /// FreePause, Rhino and Grasshopper stay usable meanwhile. But Grasshopper forbids expiring
    /// an object during a solution: anything that does it then -- a save of the script, a Run
    /// press, a slider, an upstream change, a V Edit -- ends in Grasshopper's "Grasshopper
    /// breakpoint: the ... object expired during a solution" dialog. So while a script is paused
    /// and Grasshopper is solving, GH_DocumentObject.ExpireSolution (every override in
    /// Grasshopper) is held back: logged with who asked (V Log, the detailed log), and done once
    /// the solution has ended (Rhino's first idle moment after it). Not while nothing is paused:
    /// Grasshopper's own rule stands then.
    /// </summary>
    internal static class GrasshopperPauseGuard
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Harmony s_harmony;
        private static bool s_listening;
        private static string s_error;

        /// <summary>For the health line (null: in place, or Grasshopper not loaded yet).</summary>
        internal static string HookProblem => s_error ?? (s_harmony != null && s_patched == 0 ? "no ExpireSolution method found" : null);

        private static int s_patched;
        // Held back: the object, and whether any of its calls asked to recompute.
        private static readonly List<(object Item, bool Recompute)> s_held = new List<(object, bool)>();
        // Held back: a document's NewSolution, and whether it was to expire every object.
        private static readonly List<(object Document, bool ExpireAll)> s_solutions = new List<(object, bool)>();
        private static bool s_idleQueued;
        private static bool s_replaying;
        private static int s_heldVersion; // counts objects held back: GrasshopperPausedData computes again when it grew

        /// <summary>How many objects were held back so far (not counting those of <see cref="Quietly"/>).</summary>
        internal static int HeldVersion => s_heldVersion;

        /// <summary>Set while GrasshopperPausedData computes what changed: what it holds back doesn't count.</summary>
        internal static bool Quietly;

        /// <summary>The objects held back now.</summary>
        internal static object[] HeldItems()
        {
            lock (s_held)
                return s_held.Select(h => h.Item).ToArray();
        }

        /// <summary>Whether a script is paused in the debugger now (ScriptEditorHook).</summary>
        public static Func<bool> AnyPaused { get; set; } = () => false;

        /// <summary>Whether the held-back expiries are being done now (nothing is held then).</summary>
        internal static bool Replaying => s_replaying;

        /// <summary>A document's NewSolution asked for during a script's solution of it
        /// (GrasshopperPauseCanvas): done when that solution ends.</summary>
        internal static void HoldNewSolution(object document, bool expireAll)
        {
            lock (s_held)
            {
                var at = s_solutions.FindIndex(s => ReferenceEquals(s.Document, document));
                if (at < 0)
                    s_solutions.Add((document, expireAll));
                else if (expireAll)
                    s_solutions[at] = (document, true);
            }
            QueueReplay();
        }

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
            if (s_harmony != null)
                return;
            try
            {
                var harmony = new Harmony("minivivisection.grasshopperpauseguard");
                var prefix = new HarmonyMethod(typeof(GrasshopperPauseGuard), nameof(ExpireSolutionPrefix));
                var patched = 0;
                var names = new List<string>();
                var goo = grasshopper.GetType("Grasshopper.Kernel.Types.IGH_Goo");
                foreach (var declared in grasshopper.GetTypes())
                {
                    var type = declared;
                    if (type.IsGenericTypeDefinition)
                    {
                        // GH_Param<T> and the like: patched through one closed type (the code
                        // is shared by every reference type), if it can be closed with IGH_Goo.
                        if (goo == null || type.GetGenericArguments().Length != 1)
                            continue;
                        try
                        {
                            type = type.MakeGenericType(goo);
                        }
                        catch (ArgumentException)
                        {
                            continue;
                        }
                    }
                    var method = type.GetMethod("ExpireSolution", Any | BindingFlags.DeclaredOnly, null, new[] { typeof(bool) }, null);
                    if (method == null || method.IsAbstract || method.IsStatic)
                        continue;
                    try
                    {
                        harmony.Patch(method, prefix: prefix);
                        patched++;
                        names.Add(type.Name);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(LogArea.Breakpoints, "pause guard: {0}.ExpireSolution not patched: {1}", type.Name, ex.Message);
                    }
                }
                s_harmony = harmony;
                s_patched = patched;
                Log.Info(LogArea.Breakpoints, "Grasshopper: expiring during a paused solution is held back ({0} ExpireSolution methods: {1})", patched, string.Join(", ", names));
            }
            catch (Exception ex)
            {
                s_error = ex.Message;
                Log.Warning(LogArea.Breakpoints, "Grasshopper pause guard: {0}", Log.Describe(ex));
            }
        }

        // ExpireSolution(recompute) on a Grasshopper object (__0: overrides name it differently).
        private static bool ExpireSolutionPrefix(object __instance, bool __0)
        {
            try
            {
                // A script component's outputs, before a new run clears them: its last good data
                // (computed from while it is paused: GrasshopperPausedData)
                // -- not the running (paused) one's: its outputs are its unfinished run's
                if (__instance.GetType().FullName?.StartsWith("RhinoCodePluginGH.Components.", StringComparison.Ordinal) == true &&
                    !ReferenceEquals(__instance, GrasshopperRuns.RunningComponent))
                    GrasshopperBridge.RememberOutputs(__instance);
                // A pause -- or a script component's run going on: a script pumping messages
                // itself (RhinoApp.Wait, lesson 16) lets a wire be dragged off meanwhile, and the
                // object expired during the solution brought up Grasshopper's error dialog.
                if (s_replaying || !(AnyPaused() || GrasshopperRuns.Solving))
                    return true;
                var document = DocumentOf(__instance);
                if (!IsSolving(document))
                {
                    Log.Trace(LogArea.Breakpoints, "pause guard: {0}.ExpireSolution({1}) while paused, its document not solving", __instance.GetType().Name, __0);
                    return true;
                }
                bool first;
                lock (s_held)
                {
                    var at = s_held.FindIndex(h => ReferenceEquals(h.Item, __instance));
                    first = at < 0;
                    if (first)
                    {
                        s_held.Add((__instance, __0));
                        if (!Quietly)
                            s_heldVersion++;
                    }
                    else if (__0)
                        s_held[at] = (__instance, true);
                }
                if (!first)
                    return false; // held already: said once
                var name = __instance.GetType().GetProperty("NickName")?.GetValue(__instance) ?? __instance.GetType().Name;
                var caller = string.Join(" <- ", new System.Diagnostics.StackTrace(1, false).GetFrames()
                    .Select(f => f.GetMethod())
                    .Where(m => m != null && m.DeclaringType != null && !m.DeclaringType.FullName.StartsWith("System.", StringComparison.Ordinal) && !m.DeclaringType.FullName.StartsWith("HarmonyLib", StringComparison.Ordinal))
                    .Take(6)
                    .Select(m => m.DeclaringType.Name + "." + m.Name));
                UserLog.Info("Grasshopper: '{0}' asked to recompute while a script is paused in its solution -- done when the solution ends.", name);
                Log.Info(LogArea.Breakpoints, "held back ExpireSolution of '{0}' during a paused solution; asked by {1}", name, caller);
                QueueReplay();
                return false;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "pause guard: {0}", Log.Describe(ex));
                return true;
            }
        }

        private static bool IsSolving(object document)
        {
            var state = document?.GetType().GetProperty("SolutionState", BindingFlags.Public | BindingFlags.Instance)?.GetValue(document)?.ToString();
            return state == "Process";
        }

        private static void QueueReplay()
        {
            if (s_idleQueued)
                return;
            s_idleQueued = true;
            RhinoApp.Idle += Replay;
        }

        // The solution over (nothing paused, not solving): the held-back expiries, now -- each
        // object expired without recomputing, then one new solution per document that asked
        // for one (an object's recompute, or a NewSolution held back). The guard is off only
        // around the expiries: a pause in that new solution is guarded again.
        private static void Replay(object sender, EventArgs e)
        {
            List<(object Item, bool Recompute)> held;
            List<(object Document, bool ExpireAll)> solutions;
            lock (s_held)
            {
                if (AnyPaused() || GrasshopperRuns.Solving)
                    return; // still paused, or the run goes on: next idle
                if (s_held.Any(h => IsSolving(DocumentOf(h.Item))) || s_solutions.Any(s => IsSolving(s.Document)))
                    return; // its solution still runs (a nested message loop after Continue): next idle
                held = s_held.ToList();
                s_held.Clear();
                solutions = s_solutions.ToList();
                s_solutions.Clear();
            }
            RhinoApp.Idle -= Replay;
            s_idleQueued = false;
            var documents = new List<(object Document, bool ExpireAll, bool Modified)>();
            void Wants(object document, bool expireAll, bool modified)
            {
                if (document == null)
                    return;
                var at = documents.FindIndex(d => ReferenceEquals(d.Document, document));
                if (at < 0)
                    documents.Add((document, expireAll, modified));
                else
                    documents[at] = (document, documents[at].ExpireAll || expireAll, documents[at].Modified || modified);
            }
            s_replaying = true;
            try
            {
                // Inputs computed from a paused component's last data meanwhile (GrasshopperPausedData): new data now
                try
                {
                    foreach (var document in GrasshopperBridge.ExpireAfterPause())
                        Wants(document, false, false);
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Breakpoints, "pause guard, the inputs computed while paused: {0}", Log.Describe(ex));
                }
                foreach (var (item, recompute) in held)
                {
                    try
                    {
                        item.GetType().GetMethod("ExpireSolution", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null)?.Invoke(item, new object[] { false });
                        if (recompute)
                            Wants(DocumentOf(item), false, true); // ExpireSolution(true) marks the definition modified
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(LogArea.Breakpoints, "pause guard, expiring afterwards: {0}", Log.Describe(ex));
                    }
                }
                foreach (var (document, expireAll) in solutions)
                    Wants(document, expireAll, false);
            }
            finally
            {
                s_replaying = false;
            }
            foreach (var (document, expireAll, modified) in documents)
            {
                try
                {
                    if (modified)
                        document.GetType().GetMethod("Modified", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(document, null);
                    // GH_Document.NewSolution(expireAllObjects: false): what ExpireSolution(true) does.
                    document.GetType().GetMethod("NewSolution", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null)?.Invoke(document, new object[] { expireAll });
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Breakpoints, "pause guard, the solution afterwards: {0}", Log.Describe(ex));
                }
            }
            Replayed?.Invoke(held.Count + solutions.Count, documents.Count);
        }

        /// <summary>For the live test: after a replay, how many objects and new solutions.</summary>
        internal static event Action<int, int> Replayed;

        private static object DocumentOf(object item) =>
            item.GetType().GetMethod("OnPingDocument", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(item, null);
    }
}
