#if DEBUG
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal static class LostAndFoundScenarioDescriptors
{
    public static RuntimeTestDescriptorDto Create(string id, string name,
        string prefab = "lighter",
        RuntimeScenarioFixtureOwnership ownership =
            RuntimeScenarioFixtureOwnership.TargetPlayer,
        int timeout = 60000, bool personal = true) => new()
    {
        TestId = id,
        DisplayName = name,
        Category = "Lost and Found",
        Fidelity = "GameplayMethod+ServerAuthority+NetworkRoundTrip",
        MutationKind = RuntimeTestMutationKind.IsolatedMutation,
        RequiredCapabilities = new[] { "lost-and-found-runtime-scenario" },
        TimeoutMilliseconds = timeout,
        IsScenario = true,
        ScenarioOrchestration = RuntimeScenarioOrchestrationKind.InventoryItemFixture,
        FixturePolicy = RuntimeScenarioFixturePolicy.InventoryItem,
        CleanupPolicy = RuntimeScenarioCleanupPolicy
            .RetireInventoryFixturesAndPurgeRepresentations,
        FixtureItemOwnership = ownership,
        FixtureItemIsPersonal = personal,
        DefaultItemPrefabName = prefab
    };
}
#endif
