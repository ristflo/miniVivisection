using System;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// Continue and Step over finish the line they start from. RhinoCode's tracer -- like
    /// Python's own pdb -- reports a "line" event for every round of code that runs within
    /// one line: each item of a comprehension (<c>[r.Line for r in rungs]</c> runs in a frame
    /// of its own, <c>&lt;listcomp&gt;</c>, on the same line), a generator expression or
    /// lambda there, and each round of a one-line loop (<c>for p in pts: s += p.X</c>). A
    /// breakpoint on such a line pauses again for every item, and so does Step over once
    /// inside. Here, after a pause at a line, neither pauses at that same line again until
    /// the script has left it: in the paused frame, or in an inline frame (a name like
    /// <c>&lt;listcomp&gt;</c>) one level or more below it. Functions called from the line
    /// still pause at their breakpoints; Step into still goes in; Break now always pauses.
    /// A breakpoint with a condition, hit count or log message is left alone: it decides
    /// each time (a condition on the comprehension's variable needs that).
    /// Options: "Stop once per line". Per thread: NextStep.ShouldPause runs on the traced thread.
    /// </summary>
    internal static class OncePerLine
    {
        private sealed class Hold
        {
            public ContextIdentity Context;
            public Uri Uri;
            public int Line;
            public int Depth;
        }

        [ThreadStatic]
        private static Hold t_hold;

        /// <summary>For every line event: whether a pause here would stop again on the line
        /// paused at last; the hold ends once the script is on another line at its depth.</summary>
        internal static bool Holds(ExecFrame frame, DebugThreadEventStack stack)
        {
            var hold = t_hold;
            if (hold == null || stack == null)
                return false;
            var depth = stack?.EventDepth ?? 0;
            if (!Equals(frame.ContextId, hold.Context))
            {
                t_hold = null; // another run
                return false;
            }
            var sameLine = frame.Reference?.Uri == hold.Uri && frame.Reference.Position.LineNumber == hold.Line;
            if (sameLine && (depth == hold.Depth || (depth > hold.Depth && IsInline(frame))))
                return true;
            if (depth <= hold.Depth)
                t_hold = null; // on to another line (or back in the caller)
            return false;
        }

        /// <summary>A pause at <paramref name="frame"/>'s line: held from now on.</summary>
        internal static void Paused(ExecFrame frame, DebugThreadEventStack stack)
        {
            if (frame?.Reference?.Uri == null || stack == null)
                return;
            t_hold = new Hold
            {
                Context = frame.ContextId,
                Uri = frame.Reference.Uri,
                Line = frame.Reference.Position.LineNumber,
                Depth = stack?.EventDepth ?? 0,
            };
        }

        /// <summary>Whether the step kind is one this applies to: Continue and Step over.</summary>
        internal static bool Applies(NextStepKind kind) =>
            kind == NextStepKind.PauseOnNextBreakpoint || kind == NextStepKind.PauseOnSameStackLevel;

        /// <summary>Comprehensions, generator expressions, lambdas: Python names their frames &lt;...&gt;.</summary>
        private static bool IsInline(ExecFrame frame) => frame.FrameId?.Name?.StartsWith("<", StringComparison.Ordinal) == true;

        /// <summary>For the checks: RhinoCode's own behaviour when false.</summary>
        internal static bool Enabled { get; set; } = true;

        /// <summary>For the checks: no hold on this thread.</summary>
        internal static void Reset() => t_hold = null;
    }
}
