using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace MiniVivisection
{
    /// <summary>
    /// Wires connected or disconnected in a Grasshopper definition: the solution they start
    /// doesn't pause at a script component's runtime error (Always debug). Ctrl+dragging an input
    /// away recomputes at once; the component misses its input and fails, and the debugger would
    /// pause there -- its editor coming up in the middle of the wiring. With this the component
    /// shows its error, and the wiring goes on.
    ///
    /// At each solution's start (GH_Document.SolutionStart, followed for every definition a
    /// script component runs in) the definition's wiring -- every input and what feeds it -- is
    /// compared with the last solution's. Only under Always debug: only then can an error pause.
    /// </summary>
    internal static class Rewiring
    {
        private sealed class Wiring
        {
            public string Last;
            public bool Changed;   // this solution came after a change of the wiring
            public bool Reported;  // said in V Log for this solution
        }

        private static readonly ConditionalWeakTable<object, Wiring> s_documents = new ConditionalWeakTable<object, Wiring>();

        /// <summary>A script component of <paramref name="document"/> runs: its solutions are
        /// followed from now on (UI thread).</summary>
        public static void Follow(object document)
        {
            if (document == null || s_documents.TryGetValue(document, out _))
                return;
            var wiring = new Wiring();
            s_documents.Add(document, wiring);
            try
            {
                wiring.Last = WiringOf(document); // the start: nothing changed yet
                var start = document.GetType().GetEvent("SolutionStart");
                var weak = new WeakReference(document);
                var handler = Delegate.CreateDelegate(start.EventHandlerType, new Starter(weak), typeof(Starter).GetMethod(nameof(Starter.OnStart)));
                start.AddEventHandler(document, handler);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper wiring: {0}", Log.Describe(ex));
            }
        }

        private sealed class Starter
        {
            private readonly WeakReference _document;

            public Starter(WeakReference document) => _document = document;

            public void OnStart(object sender, EventArgs e)
            {
                try
                {
                    var document = _document.Target;
                    if (document == null || !s_documents.TryGetValue(document, out var wiring))
                        return;
                    wiring.Changed = false;
                    wiring.Reported = false;
                    if (!MiniVivisectionSettings.GrasshopperAlwaysDebug)
                    {
                        wiring.Last = null; // not followed now: compared again from the next one on
                        return;
                    }
                    var now = WiringOf(document);
                    wiring.Changed = wiring.Last != null && now != wiring.Last;
                    wiring.Last = now;
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Breakpoints, "Grasshopper wiring: {0}", Log.Describe(ex));
                }
            }
        }

        /// <summary>Every input of the definition and what feeds it (the objects' ids): changes
        /// with a connect or disconnect, not with the data.</summary>
        internal static string WiringOf(object document)
        {
            var text = new StringBuilder();
            if (!(document.GetType().GetProperty("Objects")?.GetValue(document) is IEnumerable objects))
                return "";
            foreach (var each in objects)
            {
                var parameters = each.GetType().GetProperty("Params")?.GetValue(each);
                if (parameters?.GetType().GetProperty("Input")?.GetValue(parameters) is IEnumerable inputs)
                {
                    foreach (var input in inputs)
                        Append(text, input);
                }
                else if (each.GetType().GetProperty("Sources") != null)
                {
                    Append(text, each); // a parameter on its own (Panel, Curve...)
                }
            }
            return text.ToString();
        }

        private static void Append(StringBuilder text, object parameter)
        {
            text.Append(parameter.GetType().GetProperty("InstanceGuid")?.GetValue(parameter)).Append(':');
            if (parameter.GetType().GetProperty("Sources")?.GetValue(parameter) is IEnumerable sources)
            {
                foreach (var source in sources)
                    text.Append(source.GetType().GetProperty("InstanceGuid")?.GetValue(source)).Append(',');
            }
            text.Append(';');
        }

        /// <summary>The solution running now came after wires were connected or disconnected: a
        /// runtime error in it isn't paused at. Said once per solution, in V Log. UI thread.</summary>
        public static bool SkipErrorPause()
        {
            var document = GrasshopperRuns.RunningDocument;
            if (document == null || !s_documents.TryGetValue(document, out var wiring) || !wiring.Changed)
                return false;
            if (!wiring.Reported)
            {
                wiring.Reported = true;
                UserLog.Info("Grasshopper: a runtime error right after wires were connected or disconnected -- not paused, so you can go on wiring; " +
                             "the component shows the error. Recompute it to debug the error.");
            }
            return true;
        }
    }
}
