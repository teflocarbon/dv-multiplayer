using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Waiting in the orchestrator: read a value until it's what's wanted, with a deadline. The
    /// reads' step lines aren't printed, only one line for the wait; a timeout stops the run with
    /// the last value read. Inside a game, use <see cref="Wait.Until"/>.
    /// </summary>
    public static class Until
    {
        /// <example><c>float v = await Until.Value(() =&gt; pair.Host.Run(() =&gt; Cars.Speed(loco)), v =&gt; v &lt; 0.05f, 30, "the loco stopped (m/s)");</c></example>
        public static async Task<T> Value<T>(Func<Task<T>> read, Func<T, bool> done, int seconds, string what, double everySeconds = 1)
        {
            ScenarioRun run = ScenarioRun.Current;
            Stopwatch clock = Stopwatch.StartNew();
            int reads = 0;
            T value;
            using (run.Out.Mute())
            {
                while (true)
                {
                    value = await read();
                    reads++;
                    if (done(value) || clock.Elapsed.TotalSeconds >= seconds) break;
                    await Task.Delay(TimeSpan.FromSeconds(everySeconds));
                }
            }
            bool ok = done(value);
            run.Out.Line("until", what, ok ? "ok" : "FAIL", clock.Elapsed, new[] { $"{reads} read(s), last {value}" });
            if (!ok) Require.That(false, what, $"still {value} after {seconds} s");
            return value;
        }

        /// <summary>The same for a yes/no condition.</summary>
        public static Task True(Func<Task<bool>> condition, int seconds, string what, double everySeconds = 1) =>
            Value(condition, v => v, seconds, what, everySeconds);
    }
}
