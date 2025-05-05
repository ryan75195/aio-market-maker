using AngleSharp.Dom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AIOMarketMaker.Api.Utils
{
    public static class StringParsing
    {
        private const string DefaultSelector = ".x-item-sold-date";

        // Regex to capture: weekday, day, month, hour, minute, AM/PM
        private static readonly Regex SoldDateRegex = new Regex(
            @"(?:sold on|bidding ended on)\s+" +
            @"(?<weekday>\w{3}),\s+" +
            @"(?<day>\d{1,2})\s+" +
            @"(?<month>\w+)\s+" +
            @"at\s+(?<hour>\d{1,2}):(?<minute>\d{2})\s+" +
            @"(?<ampm>AM|PM)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        /// <summary>
        /// Parses a string like "This listing sold on Sun, 4 May at 11:21 AM."
        /// into a local DateTime (assumes current year, rolls back year if in future).
        /// </summary>
        public static DateTime? ParseEndDate(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return null;

            var match = SoldDateRegex.Match(rawText);
            if (!match.Success)
                return null;

            // Extract components
            if (!int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
                !int.TryParse(match.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) ||
                !int.TryParse(match.Groups["minute"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
            {
                return null;
            }
            var monthName = match.Groups["month"].Value;
            if (!DateTime.TryParseExact(monthName, "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthDt))
                return null;

            // Adjust hour for AM/PM
            var ampm = match.Groups["ampm"].Value;
            if (ampm.Equals("PM", StringComparison.OrdinalIgnoreCase) && hour < 12)
                hour += 12;
            else if (ampm.Equals("AM", StringComparison.OrdinalIgnoreCase) && hour == 12)
                hour = 0;

            // Build a Local DateTime first
            var now = DateTime.Now;
            var localDate = new DateTime(now.Year, monthDt.Month, day, hour, minute, 0, DateTimeKind.Local);

            // If it parsed into the future, assume last year
            if (localDate > now)
                localDate = localDate.AddYears(-1);

            // Convert to UTC
            var utcDate = localDate.ToUniversalTime();
            return utcDate;
        }
    }
}
