using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>
/// A classic five-field cron expression — minute, hour, day of month, month, day of week —
/// for the scheduled action somebody already knows how to write as <c>0 4 * * 0</c> and
/// would rather not translate into ticks and time boxes.
///
/// Only the parts every cron agrees on: numbers, <c>*</c>, ranges <c>1-5</c>, lists
/// <c>1,15</c>, steps <c>*/15</c> and <c>8-18/2</c>, and three-letter names for months and
/// days. Nothing vendor-specific (<c>L</c>, <c>W</c>, <c>#</c>, seconds, years, <c>@daily</c>):
/// an expression that means something different on the next machine is worse than one
/// refused here with a sentence saying why.
///
/// Days follow cron's own odd rule, because anybody pasting one in expects it: when both the
/// day of the month and the day of the week are written out (neither starts with <c>*</c>), a
/// day matching <em>either</em> counts. <c>0 4 1 * 1</c> is the 1st of the month and every
/// Monday, not Mondays that are the 1st.
///
/// Matching is on the wall clock, in the zone the caller passes; turning a matching
/// wall-clock time into an instant — and the nights the clocks change — is
/// <see cref="ActionSchedule"/>'s job, done the same way for every kind of schedule.
/// </summary>
public sealed class CronExpression
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _daysOfWeek = new bool[7];
    private bool _anyDayOfMonth;
    private bool _anyDayOfWeek;

    /// <summary>The expression as written, tidied to single spaces.</summary>
    public string Text { get; private init; } = "";

    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private CronExpression()
    {
    }

    /// <summary>The minutes past the hour it fires at, in order.</summary>
    public IReadOnlyList<int> Minutes => [.. Enumerable.Range(0, 60).Where(m => _minutes[m])];

    /// <summary>The hours it fires in, in order.</summary>
    public IReadOnlyList<int> Hours => [.. Enumerable.Range(0, 24).Where(h => _hours[h])];

    /// <summary>Every wall-clock time of day it fires at, in order.</summary>
    public IReadOnlyList<TimeOnly> Times =>
        [.. Hours.SelectMany(h => Minutes.Select(m => new TimeOnly(h, m)))];

    /// <summary>Whether it fires at all on <paramref name="date"/>.</summary>
    public bool Matches(DateOnly date)
    {
        if (!_months[date.Month])
            return false;
        var dom = _daysOfMonth[date.Day];
        var dow = _daysOfWeek[(int)date.DayOfWeek];
        // Exactly Vixie cron's test: a field written starting with * (including */2) makes
        // the two fields AND together; only when both are written out do they OR.
        return _anyDayOfMonth || _anyDayOfWeek ? dom && dow : dom || dow;
    }

    /// <summary>
    /// Reads an expression, or says in a sentence why it cannot. Null with a problem for
    /// anything malformed; never throws.
    /// </summary>
    public static CronExpression? Parse(string? text, out string? problem)
    {
        problem = null;
        var fields = (text ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            problem = fields.Length == 0
                ? "Write a cron expression, like 0 4 * * 0 for Sundays at 04:00."
                : $"A cron expression has five parts — minute, hour, day of month, month, day of week — and this has {fields.Length}.";
            return null;
        }

        var cron = new CronExpression { Text = string.Join(' ', fields) };
        if (!Field(fields[0], 0, 59, null, cron._minutes, "minute", out problem, out _)
            || !Field(fields[1], 0, 23, null, cron._hours, "hour", out problem, out _)
            || !Field(fields[2], 1, 31, null, cron._daysOfMonth, "day of the month", out problem, out cron._anyDayOfMonth)
            || !Field(fields[3], 1, 12, MonthNames, cron._months, "month", out problem, out _)
            || !DayOfWeekField(fields[4], cron, out problem))
        {
            return null;
        }
        return cron;
    }

    /// <summary>Days of the week accept 0–7, where 7 is Sunday again, as every cron allows.</summary>
    private static bool DayOfWeekField(string text, CronExpression cron, out string? problem)
    {
        var eight = new bool[8];
        if (!Field(text, 0, 7, DayNames, eight, "day of the week", out problem, out cron._anyDayOfWeek))
            return false;
        for (var d = 0; d < 7; d++)
            cron._daysOfWeek[d] = eight[d] || (d == 0 && eight[7]);
        return true;
    }

    private static bool Field(string text, int min, int max, string[]? names, bool[] into, string what,
        out string? problem, out bool any)
    {
        problem = null;
        any = text.StartsWith('*');
        foreach (var part in text.Split(','))
        {
            if (part.Length == 0)
            {
                problem = $"The {what} part has an empty item in its list.";
                return false;
            }

            var (range, stepText) = part.Split('/') switch
            {
                [var r] => (r, null),
                [var r, var s] => (r, s),
                _ => ("", ""),
            };
            if (range.Length == 0)
            {
                problem = $"“{part}” in the {what} part is not something cron understands.";
                return false;
            }

            var step = 1;
            if (stepText is not null && (!int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1))
            {
                problem = $"“/{stepText}” in the {what} part is not a step — write a whole number, like */15.";
                return false;
            }

            int from, to;
            if (range == "*")
            {
                (from, to) = (min, max);
            }
            else if (range.Split('-') is [var a, var b])
            {
                if (!Value(a, min, max, names, what, out from, out problem) || !Value(b, min, max, names, what, out to, out problem))
                    return false;
                if (to < from)
                {
                    problem = $"“{range}” in the {what} part runs backwards.";
                    return false;
                }
            }
            else
            {
                if (!Value(range, min, max, names, what, out from, out problem))
                    return false;
                // "5/15" is "from 5, every 15" — cron's shorthand for 5-max/15.
                to = stepText is null ? from : max;
            }

            for (var v = from; v <= to; v += step)
                into[v] = true;
        }
        return true;
    }

    private static bool Value(string text, int min, int max, string[]? names, string what, out int value, out string? problem)
    {
        problem = null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            if (value >= min && value <= max)
                return true;
            problem = $"{value} is outside the {what} part's range of {min}–{max}.";
            return false;
        }

        var index = names is null ? -1 : Array.IndexOf(names, text.ToLowerInvariant());
        if (index >= 0)
        {
            // Month names count from 1 (the month field's minimum); day names from Sunday as 0.
            value = index + min;
            return true;
        }

        problem = $"“{text}” in the {what} part is not a number{(names is null ? "" : " or a three-letter name")}.";
        return false;
    }

    // ---- in words -----------------------------------------------------------------

    /// <summary>
    /// The expression said back in words — "Sundays at 04:00", "Every day, every 15 minutes",
    /// "On the 1st of the month at 03:30" — so what was typed can be checked against what
    /// was meant before it runs at the wrong time for a month.
    /// </summary>
    public string Describe()
    {
        var days = DaysText();
        var times = TimesText();
        return times.StartsWith("at ", StringComparison.Ordinal)
            ? $"{days} {times}"
            : $"{days}, {times}";
    }

    private string DaysText()
    {
        var weekdays = MuteWindow.Week.Where(d => _daysOfWeek[(int)d]).ToList();
        var dates = Enumerable.Range(1, 31).Where(d => _daysOfMonth[d]).Select(Ordinal).ToList();
        var everyDate = dates.Count == 31;
        var everyWeekday = weekdays.Count == 7;
        var text = (everyDate, everyWeekday) switch
        {
            (true, true) => "Every day",
            (true, false) => MuteWindow.DaysText(weekdays),
            (false, true) => $"On the {Join(dates)} of the month",
            // The same rule Matches follows: written out, either will do; starting with *, both.
            _ when _anyDayOfMonth || _anyDayOfWeek =>
                $"On the {Join(dates)} of the month, when it is {MuteWindow.DaysText(weekdays).TrimEnd('s')}",
            _ => $"On the {Join(dates)} of the month, and on {MuteWindow.DaysText(weekdays)}",
        };
        if (Enumerable.Range(1, 12).Any(m => !_months[m]))
        {
            var months = Enumerable.Range(1, 12).Where(m => _months[m])
                .Select(m => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(m)).ToList();
            text += $", in {Join(months)}";
        }
        return text;
    }

    private string TimesText()
    {
        var minutes = Minutes;
        var hours = Hours;

        // A handful of exact times reads best as the times themselves.
        if (minutes.Count * hours.Count <= 4)
            return "at " + Join([.. Times.Select(t => t.ToString("HH\\:mm", CultureInfo.InvariantCulture))]);

        var minuteText = minutes.Count == 60 ? "every minute"
            : Every(minutes, 60) is { } step ? $"every {step} minutes"
            : minutes is [0] ? "every hour on the hour"
            : minutes.Count == 1 ? $"every hour at {minutes[0]} past"
            : $"every hour at {Join([.. minutes.Select(m => m.ToString(CultureInfo.InvariantCulture))])} minutes past";

        if (hours.Count == 24)
            return minuteText;

        if (minutes is [0] && Every(hours, 24) is { } hourStep)
            return $"every {hourStep} hours on the hour";

        var hoursText = hours.Count > 1 && hours[^1] - hours[0] == hours.Count - 1
            ? $"between {hours[0]:00}:00 and {hours[^1]:00}:59"
            : $"in the {Join([.. hours.Select(h => $"{h:00}:00")])} hour{(hours.Count == 1 ? "" : "s")}";
        return $"{minuteText}, {hoursText}";
    }

    /// <summary>The step when the values are 0, n, 2n… to the end of their range; null otherwise.</summary>
    private static int? Every(IReadOnlyList<int> values, int range)
    {
        if (values.Count < 2 || values[0] != 0)
            return null;
        var step = values[1] - values[0];
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] - values[i - 1] != step)
                return null;
        }
        return values[^1] + step >= range ? step : null;
    }

    /// <summary>"1st", "2nd", "23rd", "11th".</summary>
    public static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch
        {
            1 => $"{n}st",
            2 => $"{n}nd",
            3 => $"{n}rd",
            _ => $"{n}th",
        },
    };

    /// <summary>"a", "a and b", "a, b and c".</summary>
    public static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };
}
