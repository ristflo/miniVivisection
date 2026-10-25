using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace MiniVivisection
{
    /// <summary>
    /// The version as the user sees it -- "0.2.0 (build 57), 2026-10-09 14:05 AST": major.minor.
    /// patch and build number from Version.props, and the build's time (recorded in UTC)
    /// in local time with the time zone's abbreviation (AST, CET, CEST...), else its offset
    /// (UTC+3). Shared with the Grasshopper component (linked into its project): each reads
    /// its own assembly.
    /// </summary>
    internal static class VersionInfo
    {
        private static readonly Assembly Self = typeof(VersionInfo).Assembly;

        private static string Metadata(string key) =>
            Self.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

        /// <summary>major.minor.patch.</summary>
        public static string Version => Self.GetName().Version?.ToString(3) ?? "";

        public static int Build => int.TryParse(Metadata("BuildNumber"), out var build) ? build : 0;

        /// <summary>When it was built, UTC; null if unknown.</summary>
        public static DateTime? BuiltUtc =>
            DateTime.TryParse(Metadata("BuildTimeUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
                ? utc
                : (DateTime?)null;

        /// <summary>The build time in local time, e.g. "2026-10-09 14:05 AST".</summary>
        public static string BuiltLocal => BuiltUtc is DateTime utc ? LocalTime(utc) : "an unknown time";

        /// <summary>"0.2.0 (build 57), 2026-10-09 14:05 AST": in logs, messages, tooltips.</summary>
        public static string Full => Version + (Build > 0 ? " (build " + Build + ")" : "") + ", " + BuiltLocal;

        /// <summary>"0.2.0 · 2026-10-09 14:05 AST": where it's short (the editor's top bar).</summary>
        public static string Short => Version + " · " + BuiltLocal;

        /// <summary>A UTC time as local time with the zone: "2026-10-09 14:05 CEST".</summary>
        public static string LocalTime(DateTime utc)
        {
            var zone = TimeZoneInfo.Local;
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
            return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " " + ZoneName(zone, local);
        }

        // Windows zone ids -> the usual abbreviations (standard, daylight saving).
        private static readonly Dictionary<string, (string Standard, string Daylight)> Abbreviations =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["Arab Standard Time"] = ("AST", "ADT"),
                ["Arabic Standard Time"] = ("AST", "ADT"),
                ["Arabian Standard Time"] = ("GST", "GST"),
                ["UTC"] = ("UTC", "UTC"),
                ["GMT Standard Time"] = ("GMT", "BST"),
                ["Greenwich Standard Time"] = ("GMT", "GMT"),
                ["W. Europe Standard Time"] = ("CET", "CEST"),
                ["Central Europe Standard Time"] = ("CET", "CEST"),
                ["Central European Standard Time"] = ("CET", "CEST"),
                ["Romance Standard Time"] = ("CET", "CEST"),
                ["E. Europe Standard Time"] = ("EET", "EEST"),
                ["FLE Standard Time"] = ("EET", "EEST"),
                ["GTB Standard Time"] = ("EET", "EEST"),
                ["Egypt Standard Time"] = ("EET", "EEST"),
                ["Israel Standard Time"] = ("IST", "IDT"),
                ["Turkey Standard Time"] = ("TRT", "TRT"),
                ["Russian Standard Time"] = ("MSK", "MSK"),
                ["India Standard Time"] = ("IST", "IST"),
                ["China Standard Time"] = ("CST", "CST"),
                ["Tokyo Standard Time"] = ("JST", "JST"),
                ["Korea Standard Time"] = ("KST", "KST"),
                ["AUS Eastern Standard Time"] = ("AEST", "AEDT"),
                ["W. Australia Standard Time"] = ("AWST", "AWST"),
                ["Atlantic Standard Time"] = ("AST", "ADT"),
                ["Eastern Standard Time"] = ("EST", "EDT"),
                ["Central Standard Time"] = ("CST", "CDT"),
                ["Mountain Standard Time"] = ("MST", "MDT"),
                ["Pacific Standard Time"] = ("PST", "PDT"),
                ["Alaskan Standard Time"] = ("AKST", "AKDT"),
                ["Hawaiian Standard Time"] = ("HST", "HST"),
            };

        /// <summary>The zone's abbreviation at <paramref name="local"/>, else "UTC+3", "UTC-4:30".</summary>
        public static string ZoneName(TimeZoneInfo zone, DateTime local)
        {
            var daylight = zone.IsDaylightSavingTime(local);
            if (Abbreviations.TryGetValue(zone.Id, out var names))
                return daylight ? names.Daylight : names.Standard;
            var offset = zone.GetUtcOffset(local);
            if (offset == TimeSpan.Zero)
                return "UTC";
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            offset = offset.Duration();
            return "UTC" + sign + offset.Hours + (offset.Minutes != 0 ? ":" + offset.Minutes.ToString("00") : "");
        }
    }
}
