using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Checks that record a failure and carry on, so one run reports every broken check. Each
    /// prints what was measured against what was expected; <c>what</c> names the quantity, with its
    /// unit: "client: lowest brake pipe pressure (bar)".
    /// </summary>
    public static class Expect
    {
        public static bool That(bool ok, string what, string detail = null) => Checks.Record(ok, what, detail ?? (ok ? "yes" : "no"), false);
        public static bool Zero(double actual, string what) => Checks.Record(actual == 0, what, $"{Checks.N(actual)}, expected 0", false);
        public static bool AtLeast(double actual, double min, string what) => Checks.Record(actual >= min, what, $"{Checks.N(actual)}, expected at least {Checks.N(min)}", false);
        public static bool AtMost(double actual, double max, string what) => Checks.Record(actual <= max, what, $"{Checks.N(actual)}, expected at most {Checks.N(max)}", false);
        public static bool Equal<T>(T expected, T actual, string what) => Checks.Record(EqualityComparer<T>.Default.Equals(expected, actual), what, $"{actual}, expected {expected}", false);
        public static bool Empty<T>(IEnumerable<T> items, string what)
        {
            List<T> list = items?.ToList() ?? new List<T>();
            return Checks.Record(list.Count == 0, what, list.Count == 0 ? "none" : string.Join(", ", list), false);
        }
    }

    /// <summary>The same checks, stopping the run on a failure: for what the rest depends on.</summary>
    public static class Require
    {
        public static void That(bool ok, string what, string detail = null) => Checks.Record(ok, what, detail ?? (ok ? "yes" : "no"), true);
        public static void Zero(double actual, string what) => Checks.Record(actual == 0, what, $"{Checks.N(actual)}, expected 0", true);
        public static void AtLeast(double actual, double min, string what) => Checks.Record(actual >= min, what, $"{Checks.N(actual)}, expected at least {Checks.N(min)}", true);
        public static void AtMost(double actual, double max, string what) => Checks.Record(actual <= max, what, $"{Checks.N(actual)}, expected at most {Checks.N(max)}", true);
        public static void Equal<T>(T expected, T actual, string what) => Checks.Record(EqualityComparer<T>.Default.Equals(expected, actual), what, $"{actual}, expected {expected}", true);
    }

    /// <summary>Lines in the run's output that inform without judging: numbers worth seeing, snaps
    /// that are expected under the condition being tested.</summary>
    public static class Report
    {
        public static void Note(string note) => ScenarioRun.Current.Out.Note(note);
    }

    internal static class Checks
    {
        public static bool Record(bool ok, string what, string detail, bool require)
        {
            ScenarioRun run = ScenarioRun.Current;
            run.Out.Check(ok, what, detail);
            if (!ok)
            {
                run.Failures.Add($"{what}: {detail}");
                if (require) throw new RequireFailed($"{what}: {detail}");
            }
            return ok;
        }

        public static string N(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
    }

    internal sealed class RequireFailed(string message) : Exception(message);
}
