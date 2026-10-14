#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class ColdContainerRoundTripScenario : IRuntimeTestScenarioDefinition
{
    private readonly ColdContainerRuntimeScenarioDriver driver = new();

    public RuntimeTestDescriptorDto Descriptor => new()
    {
        TestId = "scenario.cold-container-round-trip",
        DisplayName = "Owned cold-container quick-move round trip",
        Category = "Scenarios",
        Fidelity = "GameplayMethod+ServerAuthority",
        MutationKind = RuntimeTestMutationKind.IsolatedMutation,
        RequiredCapabilities = new[] { "cold-container-quick-move-scenario" },
        TimeoutMilliseconds = 60000,
        IsScenario = true,
        ScenarioOrchestration = RuntimeScenarioOrchestrationKind.InventoryFixturePair,
        FixturePolicy = RuntimeScenarioFixturePolicy.InventoryContainerAndItem,
        CleanupPolicy = RuntimeScenarioCleanupPolicy.RetireInventoryFixturesAndPurgeRepresentations,
        FixtureItemOwnership = RuntimeScenarioFixtureOwnership.TargetPlayer,
        DefaultContainerPrefabName = "ItemContainerCrate",
        DefaultItemPrefabName = "lighter"
    };

    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.RoundTrip(command, run);
}
#endif
