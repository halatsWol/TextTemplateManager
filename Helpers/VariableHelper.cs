using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MarflowSoftware.Helpers
{
    public static class VariableHelper
    {
        // Formatted placeholders: $[DATE(fmt)], $[TIME(fmt)], $[DATETIME(fmt)] — all format the
        // current date/time with a moment/Clippings-style token string, e.g. $[DATE(YYYY-MM-DD)]
        // or $[TIME(HH:mm)]. (Clippings exports use this syntax.)
        private static readonly Regex FormatToken = new(
            @"\$\[(?:DATE|TIME|DATETIME)\(([^)]*)\)\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string ProcessVariables(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            var now = DateTime.Now;

            // Formatted date/time placeholders first (they carry a "(...)" argument).
            input = FormatToken.Replace(input, m =>
            {
                string net = ToNetFormat(m.Groups[1].Value);
                try { return now.ToString(net, CultureInfo.InvariantCulture); }
                catch { return m.Value; } // bad format string -> leave the placeholder untouched
            });

            // Simple fixed-format placeholders.
            input = input.Replace("$[DATE]", now.ToString("dd.MM.yyyy"));
            input = input.Replace("$[TIME]", now.ToString("HH:mm"));
            input = input.Replace("$[YEAR]", now.Year.ToString());

            return input;
        }

        // Converts moment/Clippings tokens to a .NET custom date/time format. Month (MM), hour
        // (HH/hh), minute (mm) and second (ss) are already identical in .NET; only year and
        // day-of-month differ in case (Y->y, D->d). Separators like '-', '.', ':' pass through.
        private static string ToNetFormat(string moment) => moment.Replace('Y', 'y').Replace('D', 'd');
    }
}
