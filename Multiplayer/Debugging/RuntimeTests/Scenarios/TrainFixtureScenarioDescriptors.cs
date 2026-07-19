#if DEBUG
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal static class TrainFixtureScenarioDescriptors
{
    public static RuntimeTestDescriptorDto Create(string id, string name, bool readOnly = false,
        int timeoutMilliseconds = 90000) => new()
    {
        TestId = id,
        DisplayName = name,
        Category = "Train Fixtures",
        Fidelity = readOnly ? "GameplayMethod+TrackIdentity+LocationStreaming" :
            "GameplayMethods+HostAuthority+AtomicTrainReplication",
        MutationKind = RuntimeTestMutationKind.IsolatedMutation,
        RequiredCapabilities = readOnly
            ? new[] { "train-track-catalog", "player-teleport" }
            : new[] { "host-train-fixtures" },
        TimeoutMilliseconds = timeoutMilliseconds,
        IsScenario = true,
        ScenarioOrchestration = RuntimeScenarioOrchestrationKind.None,
        FixturePolicy = RuntimeScenarioFixturePolicy.None,
        CleanupPolicy = RuntimeScenarioCleanupPolicy.ScenarioOwned
    };
}
#endif
