using System;
using System.Collections.Generic;
using System.Linq;

namespace MiniVivisection
{
    /// <summary>
    /// V°Claude's / commands for Tab completion: the command, then its argument (/allow on|off,
    /// /undo force). One match: completed (with a space when it takes more); several: completed
    /// as far as they agree, and listed with their help lines when Tab can't go further.
    /// </summary>
    internal static class SlashCommands
    {
        /// <summary>(command, takes more after it, its arguments to complete)</summary>
        private static readonly (string Name, bool More, string[] Arguments)[] Commands =
        {
            ("/replace", true, null), ("/fix", true, null), ("/explain", true, null), ("/debug", true, null), ("/why", true, null),
            ("/pending", false, null), ("/apply", false, null), ("/reject", false, null), ("/undo", true, new[] { "force" }),
            ("/history", false, null), ("/allow", true, new[] { "on", "off" }), ("/run", false, null), ("/deny", false, null),
            ("/new", false, null), ("/clear", false, null), ("/help", false, null),
        };

        public static IEnumerable<string> Names => Commands.Select(c => c.Name);

        /// <summary>The input Tab completes to (the same when it can't go further), and what to
        /// list then (empty: nothing); null input: not a command -- Tab does what it did.</summary>
        public static (string Input, IReadOnlyList<string> Choices) Complete(string input, string help)
        {
            var none = (IReadOnlyList<string>)Array.Empty<string>();
            input = input ?? "";
            if (input.Length == 0)
                return ("/", HelpLines(help, Names));
            if (!input.StartsWith("/") || input.Contains("\n"))
                return (null, none);
            var space = input.IndexOf(' ');
            if (space < 0)
            {
                var typed = input.ToLowerInvariant();
                var matches = Commands.Where(c => c.Name.StartsWith(typed, StringComparison.Ordinal)).ToList();
                if (matches.Count == 0)
                    return (input, none);
                if (matches.Count == 1)
                    return (matches[0].Name + (matches[0].More ? " " : ""), none);
                var common = CommonStart(matches.Select(m => m.Name));
                if (common.Length > typed.Length)
                    return (common, none);
                return (input, HelpLines(help, matches.Select(m => m.Name)));
            }
            // The argument: /allow on|off, /undo force
            var name = input.Substring(0, space).ToLowerInvariant();
            var command = Commands.FirstOrDefault(c => c.Name == name);
            var argument = input.Substring(space + 1);
            if (command.Arguments == null || argument.Contains(" "))
                return (input, none);
            var fits = command.Arguments.Where(a => a.StartsWith(argument.ToLowerInvariant(), StringComparison.Ordinal)).ToList();
            if (fits.Count == 1)
                return (input.Substring(0, space + 1) + fits[0], none);
            if (fits.Count > 1)
            {
                var common = CommonStart(fits);
                if (common.Length > argument.Length)
                    return (input.Substring(0, space + 1) + common, none);
                return (input, new[] { name + " " + string.Join("|", fits) });
            }
            return (input, none);
        }

        // The help lines that name these commands ("/apply, /reject" once).
        private static IReadOnlyList<string> HelpLines(string help, IEnumerable<string> names)
        {
            var wanted = names.ToList();
            var lines = (help ?? "").Split('\n').Where(line =>
            {
                var head = line.TrimStart().Split(new[] { "  " }, StringSplitOptions.None)[0];
                return head.StartsWith("/") && wanted.Any(n => head.Split(new[] { ", ", " " }, StringSplitOptions.RemoveEmptyEntries).Contains(n));
            }).ToList();
            return lines.Count > 0 ? lines : wanted;
        }

        private static string CommonStart(IEnumerable<string> words)
        {
            var list = words.ToList();
            var first = list[0];
            var length = first.Length;
            foreach (var word in list.Skip(1))
            {
                length = Math.Min(length, word.Length);
                for (var i = 0; i < length; i++)
                {
                    if (word[i] != first[i])
                    {
                        length = i;
                        break;
                    }
                }
            }
            return first.Substring(0, length);
        }
    }
}
