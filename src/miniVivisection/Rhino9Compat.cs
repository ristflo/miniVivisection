#if RHINO9
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Rhino.Runtime.Code.Execution;

namespace MiniVivisection
{
    // Rhino 9's RhinoCode replaced Rhino 8's debugger values: ExecVariable is now ExecValue
    // (and ExecSlot, a named value), and DebugExpression/IDebugExpressionResult are gone --
    // a frame lists its slots (ExecFrame.GetSlots) and looks up a parsed ExecExpression
    // (ExecFrame.TryEval). The shared code keeps using Rhino 8's names; in the Rhino 9 build
    // these small stand-ins give them, on top of Rhino 9's types. Only what the plug-in uses.

    /// <summary>Rhino 8's ExecVariableIdentity: a value's name and type, as shown.</summary>
    internal sealed class ExecVariableIdentity
    {
        public ExecVariableIdentity(string identifier, string typeIdentifier)
        {
            Identifier = identifier ?? "";
            TypeIdentifier = typeIdentifier ?? "";
        }

        public string Identifier { get; }

        public string TypeIdentifier { get; }
    }

    /// <summary>Rhino 8's ExecVariable: a named value of the paused frame (or a member of
    /// one), with its text, type and members -- here a Rhino 9 <see cref="ExecValue"/>.</summary>
    internal sealed class ExecVariable
    {
        /// <summary>A value the plug-in evaluated itself (Python, IronPython), as a variable.</summary>
        public ExecVariable(string id, object value)
            : this(id, new ExecValue(ExecExpression.FromMember(id ?? ""), value))
        {
        }

        public ExecVariable(string id, ExecValue value)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            Id = new ExecVariableIdentity(id, SafeType(value));
        }

        /// <summary>Rhino 9's value behind this variable.</summary>
        public ExecValue Value { get; }

        public ExecVariableIdentity Id { get; }

        public bool CanExpand => Value.CanExpand;

        /// <summary>The variable for one of Rhino 9's values: a slot's name (Python's
        /// members are ".x", shown as "x" -- the callers add the dot), else its expression.</summary>
        public static ExecVariable Of(ExecValue value)
        {
            if (value == null)
                return null;
            var name = value is ExecSlot slot && slot.Id != null ? slot.Id.Identifier : value.Expression.ExpressionString;
            if (name != null && name.StartsWith(".", StringComparison.Ordinal))
                name = name.Substring(1);
            return new ExecVariable(name, value);
        }

        public bool TryGetValue(out object value) => Value.TryGetValue(out value);

        public bool TryGetValue<T>(out T value) => Value.TryGetValue(out value);

        public string GetValueRepr() => Value.ValueString;

        /// <summary>The members, as Rhino 9 lists them.</summary>
        public IEnumerable<ExecVariable> Expand() => Value.Expand().Select(Of).Where(v => v != null);

        private static string SafeType(ExecValue value)
        {
            try
            {
                return value.GetTypeRepr(false);
            }
            catch (Exception)
            {
                return value.TypeIdentifier;
            }
        }
    }

    /// <summary>Rhino 8's DebugExpression: the text of a watch expression.</summary>
    internal readonly struct DebugExpression
    {
        public DebugExpression(string expression)
        {
            ExpressionString = expression ?? "";
        }

        public string ExpressionString { get; }
    }

    /// <summary>Rhino 8's IDebugExpressionResult: what a frame found for an expression.</summary>
    internal interface IDebugExpressionResult
    {
        object Result { get; }
    }

    internal sealed class DebugExpressionResult : IDebugExpressionResult
    {
        public DebugExpressionResult(object result)
        {
            Result = result;
        }

        public object Result { get; }
    }

    /// <summary>Rhino 8's ExecFrame.Evaluate, on Rhino 9's frames.</summary>
    internal static class ExecFrameCompat
    {
        /// <summary>The frame's variables (Rhino 8: <c>frame.Evaluate()</c>).</summary>
        public static IEnumerable<IDebugExpressionResult> Evaluate(this ExecFrame frame)
        {
            if (frame == null || !frame.TryGetSlots(out var slots) || slots == null)
                return Enumerable.Empty<IDebugExpressionResult>();
            return slots.Select(s => (IDebugExpressionResult)new DebugExpressionResult(ExecVariable.Of(s))).ToList();
        }

        /// <summary><paramref name="expression"/> looked up in the frame: a variable and its
        /// members and items (<c>pts[0].X</c>), as Rhino 9 finds them -- no calls, no
        /// operators (Rhino 9's <see cref="ExecFrame.Parse"/> doesn't parse yet).</summary>
        public static bool Evaluate(this ExecFrame frame, DebugExpression expression, out IDebugExpressionResult result)
        {
            result = null;
            if (frame == null || !TryParse(frame, expression.ExpressionString, out var parsed))
                return false;
            if (!frame.TryEval(parsed, out var value) || value == null || value.Kind == ExecValueKind.Missing || value.Kind == ExecValueKind.Errored)
                return false;
            result = new DebugExpressionResult(ExecVariable.Of(value));
            return true;
        }

        private static readonly Regex s_root = new Regex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        private static readonly Regex s_step = new Regex(@"\G\s*(?:\.\s*([A-Za-z_][A-Za-z0-9_]*)|(\[[^\[\]]+\]))", RegexOptions.Compiled);

        /// <summary>A name with <c>.member</c> and <c>[key]</c> steps, as Rhino 9's slots name
        /// them; the frame's own parser first, should a later Rhino 9 have one.</summary>
        internal static bool TryParse(ExecFrame frame, string text, out ExecExpression parsed)
        {
            parsed = ExecExpression.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            try
            {
                var native = frame.Parse(text);
                if (native.Tokens != null && native.Tokens.Length > 0)
                {
                    parsed = native;
                    return true;
                }
            }
            catch (Exception)
            {
                // ours, below
            }
            var root = s_root.Match(text);
            if (!root.Success)
                return false;
            parsed = ExecExpression.FromMember(root.Groups[1].Value);
            var at = root.Length;
            while (at < text.Length)
            {
                var step = s_step.Match(text, at);
                if (!step.Success)
                    return text.Substring(at).Trim().Length == 0;
                parsed = step.Groups[1].Success ? parsed.EvalGet("." + step.Groups[1].Value) : parsed.EvalGetKey(step.Groups[2].Value.Replace("\"", "'"));
                at = step.Index + step.Length;
            }
            return true;
        }
    }
}
#endif
