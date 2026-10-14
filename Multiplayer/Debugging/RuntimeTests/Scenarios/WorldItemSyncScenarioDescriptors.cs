#if DEBUG
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal static class WorldItemSyncScenarioDescriptors
{
    public static RuntimeTestDescriptorDto Fixture(string id, string name, string prefab = "lighter",
        RuntimeScenarioFixtureOwnership ownership = RuntimeScenarioFixtureOwnership.TargetPlayer,
        int timeoutMilliseconds = 60000) => new()
    {
        TestId = id,
        DisplayName = name,
        Category = "World Item Sync",
        Fidelity = "GameplayMethod+HostAuthority+InterestStreaming",
        MutationKind = RuntimeTestMutationKind.IsolatedMutation,
        RequiredCapabilities = new[] { "world-item-sync-runtime-scenario" },
        TimeoutMilliseconds = timeoutMilliseconds,
        IsScenario = true,
        ScenarioOrchestration = RuntimeScenarioOrchestrationKind.InventoryItemFixture,
        FixturePolicy = RuntimeScenarioFixturePolicy.InventoryItem,
        CleanupPolicy = RuntimeScenarioCleanupPolicy
            .RetireInventoryFixturesAndPurgeRepresentations,
        FixtureItemOwnership = ownership,
        FixtureItemIsPersonal = true,
        DefaultItemPrefabName = prefab
    };

    public static RuntimeTestDescriptorDto AuthoredProjection() => new()
    {
        TestId = "scenario.world-item-authored-projection-integrity",
        DisplayName = "Authored scene item projection integrity",
        Category = "World Item Sync",
        Fidelity = "ReadOnly+ClientAuthoredProjection",
        MutationKind = RuntimeTestMutationKind.ReadOnly,
        RequiredCapabilities = new[] { "world-item-sync-runtime-scenario" },
        TimeoutMilliseconds = 15000,
        IsScenario = true,
        ScenarioOrchestration = RuntimeScenarioOrchestrationKind.None,
        FixturePolicy = RuntimeScenarioFixturePolicy.None,
        CleanupPolicy = RuntimeScenarioCleanupPolicy.ScenarioOwned
    };
}
#endif
