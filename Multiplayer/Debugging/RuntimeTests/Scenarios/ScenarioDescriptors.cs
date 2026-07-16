#if DEBUG
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal static class ScenarioDescriptors
{
    public static RuntimeTestDescriptorDto Create(string testId, string displayName,
        string containerPrefab = "ItemContainerCrate", string itemPrefab = "lighter",
        int timeout = 60000,
        RuntimeScenarioFixtureOwnership itemOwnership =
            RuntimeScenarioFixtureOwnership.TargetPlayer,
        RuntimeScenarioFixtureOwnership containerOwnership =
            RuntimeScenarioFixtureOwnership.TargetPlayer) => new()
    {
        TestId = testId,
        DisplayName = displayName,
        Category = "Cold Containers",
        Fidelity = "GameplayMethod+ServerAuthority",
        MutationKind = RuntimeTestMutationKind.IsolatedMutation,
        RequiredCapabilities = new[] { "cold-container-quick-move-scenario" },
        TimeoutMilliseconds = timeout,
        IsScenario = true,
        ScenarioOrchestration = RuntimeScenarioOrchestrationKind.InventoryFixturePair,
        FixturePolicy = RuntimeScenarioFixturePolicy.InventoryContainerAndItem,
        CleanupPolicy = RuntimeScenarioCleanupPolicy.RetireInventoryFixturesAndPurgeRepresentations,
        FixtureContainerOwnership = containerOwnership,
        FixtureItemOwnership = itemOwnership,
        DefaultContainerPrefabName = containerPrefab,
        DefaultItemPrefabName = itemPrefab
    };
}
#endif
