using System;
using System.Collections.Generic;

namespace Dvmp.Scenarios
{
    /// <summary>Names a scenario for `dvmp scenario run NAME` and gate.sh's run list.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ScenarioAttribute(string name, string description) : Attribute
    {
        public string Name { get; } = name;
        public string Description { get; } = description;
    }

    /// <summary>A method that runs inside a game. Only the orchestrator's Run/Start call it, never
    /// other code: its arguments and result cross between processes as JSON.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class StepAttribute : Attribute { }

    /// <summary>
    /// A [Step] that runs only on a remote client, so it may use the game's NetworkClient (the
    /// analyzer's DVMP0103 allows it here): a remote client keeps its client in stage 3, only the
    /// host loses its own. For what no player action sends, such as a request the vanilla UI would
    /// refuse before it leaves the game. Running it on the host fails the call.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ClientOnlyAttribute : Attribute { }

    public enum Need
    {
        /// <summary>The host is a player (not a dedicated host): host steps act as a player.</summary>
        PlayerHost,
        /// <summary>The host is a dedicated host.</summary>
        DedicatedHost,
        /// <summary>The run restarts its games (Pair.Restart); gate schedules it accordingly.</summary>
        RestartsGames
    }

    /// <summary>What the pair must be for this scenario; a pair that isn't is a harness error
    /// (exit 3), not a failure.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class NeedsAttribute(params Need[] needs) : Attribute
    {
        public IReadOnlyList<Need> Needs { get; } = needs;
    }

    /// <summary>The longest a run may take before it's stopped and failed, in seconds.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BudgetAttribute(int seconds) : Attribute
    {
        public int Seconds { get; } = seconds;
    }

    /// <summary>
    /// A known bug, proved by this scenario: it fails on beta until the fix lands. <c>Id</c> is its row
    /// in docs/research/item-bug-review.md. The PR that fixes it deletes the attribute, and the
    /// scenario is a required pass from then on. The gate and <c>scenario list</c> show it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class KnownBugAttribute(string id) : Attribute
    {
        public string Id { get; } = id;
    }

    /// <summary>A coroutine step's result and notes, filled in inside the game. A step takes it as
    /// its first parameter: <c>IEnumerator Watch(Step&lt;Watched&gt; step, ...)</c>, called as
    /// <c>pair.Host.Run&lt;Watched&gt;(s =&gt; Watch(s, ...))</c>.</summary>
    public sealed class Step<T>
    {
        public T Result { get; set; }
        public List<string> Notes { get; } = new();

        /// <summary>A line printed under the step's line in the run's output.</summary>
        public void Note(string note) => Notes.Add(note);
    }
}
