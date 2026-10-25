using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MiniVivisection
{
    /// <summary>
    /// Conditions and logpoint expressions for C# scripts: there is no C# evaluator for a
    /// paused frame, so this is a small one over the frame's own variables (names and
    /// values, as the Variables tab has them). It understands literals (numbers,
    /// "strings", 'c', true, false, null), the variables, member access (properties,
    /// fields), indexers (arrays, lists, this[...]), method calls on values and on
    /// <c>Math</c>, unary <c>! -</c>, <c>* / %</c>, <c>+ -</c> (and string +),
    /// <c>&lt; &gt; &lt;= &gt;=</c>, <c>== !=</c>, <c>&amp;&amp; ||</c> -- or Python's <c>and or not</c>,
    /// <c>True False None</c>, for IronPython 2 scripts -- (short-circuit:
    /// <c>crv != null &amp;&amp; crv.IsClosed</c>) and parentheses.
    /// Numbers compare as numbers whatever their types, as in C#. Errors throw
    /// <see cref="ConditionException"/> with what went wrong.
    /// </summary>
    internal sealed class CSharpConditions
    {
        private readonly string _text;
        private readonly IReadOnlyDictionary<string, object> _variables;
        private int _at;
        private int _skipping; // the right side of && / || that doesn't count: parsed only

        private CSharpConditions(string text, IReadOnlyDictionary<string, object> variables)
        {
            _text = text ?? "";
            _variables = variables;
        }

        /// <summary>The value of <paramref name="expression"/>.</summary>
        public static object Evaluate(string expression, IReadOnlyDictionary<string, object> variables)
        {
            var parser = new CSharpConditions(expression, variables);
            var value = parser.Or();
            parser.Space();
            if (parser._at < parser._text.Length)
                throw new ConditionException("unexpected '" + parser._text.Substring(parser._at) + "'");
            return value;
        }

        /// <summary>Whether <paramref name="expression"/> holds: true, a non-zero number,
        /// a non-null object.</summary>
        public static bool IsTrue(string expression, IReadOnlyDictionary<string, object> variables) => Truthy(Evaluate(expression, variables));

        /// <summary>Only parses: null when <paramref name="expression"/> is well-formed,
        /// else what's wrong (unknown names are fine here).</summary>
        public static string SyntaxError(string expression)
        {
            try
            {
                var parser = new CSharpConditions(expression, null);
                parser.Or();
                parser.Space();
                return parser._at < parser._text.Length ? "unexpected '" + parser._text.Substring(parser._at) + "'" : null;
            }
            catch (ConditionException ex)
            {
                return ex.Message;
            }
        }

        public static bool Truthy(object value)
        {
            switch (value)
            {
                case null:
                    return false;
                case bool flag:
                    return flag;
                case string text:
                    return text.Length > 0;
            }
            return IsNumber(value) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0 : true;
        }

        // --- parsing and evaluating at once (null variables: parse only) --------------------------

        private bool Checking => _variables == null || _skipping > 0;

        /// <summary>Python's spelling of an operator (and, or, not), as a whole word.</summary>
        private bool TakeWord(string word)
        {
            Space();
            if (string.CompareOrdinal(_text, _at, word, 0, word.Length) != 0)
                return false;
            var end = _at + word.Length;
            if (end < _text.Length && (char.IsLetterOrDigit(_text[end]) || _text[end] == '_'))
                return false;
            _at = end;
            return true;
        }

        private object Or()
        {
            var left = And();
            while (Take("||") || TakeWord("or"))
            {
                var decided = !Checking && Truthy(left); // short-circuit, as in C#
                if (decided)
                    _skipping++;
                var right = And();
                if (decided)
                    _skipping--;
                left = Checking ? null : (object)(decided || Truthy(right));
            }
            return left;
        }

        private object And()
        {
            var left = Equality();
            while (Take("&&") || TakeWord("and"))
            {
                var decided = !Checking && !Truthy(left); // short-circuit, as in C#
                if (decided)
                    _skipping++;
                var right = Equality();
                if (decided)
                    _skipping--;
                left = Checking ? null : (object)(!decided && Truthy(right));
            }
            return left;
        }

        private object Equality()
        {
            var left = Relational();
            while (true)
            {
                var equal = Take("==");
                if (!equal && !Take("!="))
                    return left;
                var right = Relational(); // parsed in either mode
                left = Checking ? null : (object)(AreEqual(left, right) == equal);
            }
        }

        private object Relational()
        {
            var left = Additive();
            while (true)
            {
                string op = Take("<=") ? "<=" : Take(">=") ? ">=" : Take("<") ? "<" : Take(">") ? ">" : null;
                if (op == null)
                    return left;
                var right = Additive();
                if (Checking)
                    continue;
                var c = Compare(left, right, op);
                left = op == "<" ? c < 0 : op == ">" ? c > 0 : op == "<=" ? c <= 0 : c >= 0;
            }
        }

        private object Additive()
        {
            var left = Multiplicative();
            while (true)
            {
                if (Take("+"))
                {
                    var right = Multiplicative();
                    if (!Checking)
                        left = left is string || right is string ? Text(left) + Text(right) : (object)(Number(left, "+") + Number(right, "+"));
                }
                else if (Take("-"))
                {
                    var right = Multiplicative();
                    if (!Checking)
                        left = Number(left, "-") - Number(right, "-");
                }
                else
                {
                    return left;
                }
            }
        }

        private object Multiplicative()
        {
            var left = Unary();
            while (true)
            {
                string op = Take("*") ? "*" : Take("/") ? "/" : Take("%") ? "%" : null;
                if (op == null)
                    return left;
                var right = Unary();
                if (Checking)
                    continue;
                var a = Number(left, op);
                var b = Number(right, op);
                left = op == "*" ? a * b : op == "/" ? a / b : a % b;
            }
        }

        private object Unary()
        {
            if (Take("!") || TakeWord("not"))
            {
                var value = Unary();
                return Checking ? null : (object)!Truthy(value);
            }
            if (Take("-"))
            {
                var value = Unary();
                return Checking ? null : (object)-Number(value, "-");
            }
            return Postfix();
        }

        private object Postfix()
        {
            var value = Primary(out var name);
            while (true)
            {
                if (Take("."))
                {
                    var member = Identifier() ?? throw new ConditionException("a name after '.'");
                    if (Peek("("))
                    {
                        var arguments = Arguments();
                        value = Checking ? null : Call(value, name, member, arguments);
                    }
                    else
                    {
                        value = Checking ? null : Member(value, name, member);
                    }
                    name = null;
                }
                else if (Take("["))
                {
                    var index = Or();
                    Expect("]");
                    value = Checking ? null : Index(value, index);
                    name = null;
                }
                else
                {
                    return value;
                }
            }
        }

        /// <summary>A primary value; <paramref name="name"/> is set for a bare name that
        /// isn't a variable (a type, like Math, for a static call).</summary>
        private object Primary(out string name)
        {
            name = null;
            Space();
            if (Take("("))
            {
                var value = Or();
                Expect(")");
                return value;
            }
            if (_at < _text.Length && (char.IsDigit(_text[_at]) || _text[_at] == '.' && _at + 1 < _text.Length && char.IsDigit(_text[_at + 1])))
                return NumberLiteral();
            if (_at < _text.Length && (_text[_at] == '"' || _text[_at] == '\''))
                return TextLiteral();
            var identifier = Identifier() ?? throw new ConditionException(_at < _text.Length ? "unexpected '" + _text[_at] + "'" : "an expression");
            switch (identifier)
            {
                case "true":
                case "True":
                    return true;
                case "false":
                case "False":
                    return false;
                case "null":
                case "None":
                    return null;
            }
            if (Checking)
                return null;
            if (_variables.TryGetValue(identifier, out var variable))
                return variable;
            if (identifier == "Math")
            {
                name = identifier;
                return null;
            }
            throw new ConditionException("'" + identifier + "' isn't a variable here");
        }

        private List<object> Arguments()
        {
            Expect("(");
            var arguments = new List<object>();
            if (Take(")"))
                return arguments;
            do
                arguments.Add(Or());
            while (Take(","));
            Expect(")");
            return arguments;
        }

        private object NumberLiteral()
        {
            var start = _at;
            while (_at < _text.Length && (char.IsDigit(_text[_at]) || _text[_at] == '.' || _text[_at] == '_'))
                _at++;
            if (_at < _text.Length && (_text[_at] == 'e' || _text[_at] == 'E'))
            {
                _at++;
                if (_at < _text.Length && (_text[_at] == '+' || _text[_at] == '-'))
                    _at++;
                while (_at < _text.Length && char.IsDigit(_text[_at]))
                    _at++;
            }
            var digits = _text.Substring(start, _at - start).Replace("_", "");
            while (_at < _text.Length && "fFdDmMlLuU".IndexOf(_text[_at]) >= 0)
                _at++; // C# suffixes
            if (!digits.Contains('.') && !digits.Contains('e') && !digits.Contains('E') && long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                return whole >= int.MinValue && whole <= int.MaxValue ? (object)(int)whole : whole;
            if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number;
            throw new ConditionException("a number: " + digits);
        }

        private object TextLiteral()
        {
            var quote = _text[_at++];
            var text = new StringBuilder();
            while (_at < _text.Length && _text[_at] != quote)
            {
                var c = _text[_at++];
                if (c == '\\' && _at < _text.Length)
                {
                    var e = _text[_at++];
                    text.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e);
                }
                else
                {
                    text.Append(c);
                }
            }
            if (_at >= _text.Length)
                throw new ConditionException("an unfinished " + (quote == '"' ? "string" : "character"));
            _at++;
            return quote == '\'' && text.Length == 1 ? (object)text[0] : text.ToString();
        }

        private string Identifier()
        {
            Space();
            if (_at >= _text.Length || !(char.IsLetter(_text[_at]) || _text[_at] == '_' || _text[_at] == '@'))
                return null;
            var start = _at;
            _at++;
            while (_at < _text.Length && (char.IsLetterOrDigit(_text[_at]) || _text[_at] == '_'))
                _at++;
            return _text.Substring(start, _at - start).TrimStart('@');
        }

        private void Space()
        {
            while (_at < _text.Length && char.IsWhiteSpace(_text[_at]))
                _at++;
        }

        private bool Peek(string token)
        {
            Space();
            return string.CompareOrdinal(_text, _at, token, 0, token.Length) == 0;
        }

        private bool Take(string token)
        {
            if (!Peek(token))
                return false;
            // "=" alone isn't "==", "&" isn't "&&"; "<" must not take the "<" of "<=".
            if (token.Length == 1 && _at + 1 < _text.Length && "<>!=".IndexOf(token[0]) >= 0 && _text[_at + 1] == '=')
                return false;
            _at += token.Length;
            return true;
        }

        private void Expect(string token)
        {
            if (!Take(token))
                throw new ConditionException("'" + token + "' expected");
        }

        // --- values ----------------------------------------------------------------------------------

        private static bool IsNumber(object value) =>
            value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint ||
            value is long || value is ulong || value is float || value is double || value is decimal;

        private static double Number(object value, string op)
        {
            if (IsNumber(value))
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (value is char c)
                return c;
            throw new ConditionException(string.Format("'{0}' needs numbers, got {1}", op, Describe(value)));
        }

        private static string Text(object value) => value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);

        private static bool AreEqual(object a, object b)
        {
            if (IsNumber(a) && IsNumber(b))
                return Number(a, "==") == Number(b, "==");
            return Equals(a, b);
        }

        private static int Compare(object a, object b, string op)
        {
            if ((IsNumber(a) || a is char) && (IsNumber(b) || b is char))
                return Number(a, op).CompareTo(Number(b, op));
            if (a is IComparable comparable && b != null && a.GetType() == b.GetType())
                return comparable.CompareTo(b);
            throw new ConditionException(string.Format("'{0}' can't compare {1} and {2}", op, Describe(a), Describe(b)));
        }

        private static string Describe(object value) => value == null ? "null" : value.GetType().Name;

        private static object Member(object target, string typeName, string member)
        {
            if (typeName == "Math")
            {
                var constant = typeof(Math).GetField(member, BindingFlags.Public | BindingFlags.Static);
                return constant != null ? constant.GetValue(null) : throw new ConditionException("Math." + member + " isn't known");
            }
            if (target == null)
                throw new ConditionException("." + member + " of null");
            var type = target.GetType();
            var property = type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.GetIndexParameters().Length == 0)
                return property.GetValue(target);
            var field = type.GetField(member, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
                return field.GetValue(target);
            if (member == "Length" && target is Array array)
                return array.Length;
            throw new ConditionException(type.Name + " has no " + member);
        }

        private static object Call(object target, string typeName, string method, List<object> arguments)
        {
            var type = typeName == "Math" ? typeof(Math) : target?.GetType() ?? throw new ConditionException(method + "() of null");
            var flags = typeName == "Math" ? BindingFlags.Public | BindingFlags.Static : BindingFlags.Public | BindingFlags.Instance;
            foreach (var candidate in type.GetMethods(flags).Where(m => m.Name == method && m.GetParameters().Length == arguments.Count))
            {
                var parameters = candidate.GetParameters();
                var converted = new object[arguments.Count];
                var fits = true;
                for (var i = 0; i < arguments.Count && fits; i++)
                    fits = TryConvert(arguments[i], parameters[i].ParameterType, out converted[i]);
                if (fits)
                    return candidate.Invoke(typeName == "Math" ? null : target, converted);
            }
            throw new ConditionException(string.Format("{0} has no {1}() taking {2} argument(s)", type.Name, method, arguments.Count));
        }

        private static object Index(object target, object index)
        {
            switch (target)
            {
                case null:
                    throw new ConditionException("[...] of null");
                case string text when IsNumber(index):
                    return text[(int)Number(index, "[]")];
                case Array array when IsNumber(index):
                    return array.GetValue((int)Number(index, "[]"));
                case IList list when IsNumber(index):
                    return list[(int)Number(index, "[]")];
                case IDictionary dictionary:
                    return dictionary[index];
            }
            var indexer = target.GetType().GetProperties().FirstOrDefault(p => p.GetIndexParameters().Length == 1);
            if (indexer != null && TryConvert(index, indexer.GetIndexParameters()[0].ParameterType, out var key))
                return indexer.GetValue(target, new[] { key });
            throw new ConditionException(Describe(target) + " can't be indexed");
        }

        private static bool TryConvert(object value, Type type, out object converted)
        {
            converted = value;
            if (value == null)
                return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
            if (type.IsInstanceOfType(value))
                return true;
            try
            {
                if (IsNumber(value) && (type.IsPrimitive || type == typeof(decimal)))
                {
                    converted = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch (Exception)
            {
                // doesn't fit
            }
            return false;
        }
    }

    internal sealed class ConditionException : Exception
    {
        public ConditionException(string message) : base(message)
        {
        }
    }
}
