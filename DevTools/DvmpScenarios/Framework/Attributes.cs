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
