#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class ColdContainerForeignRejectionScenario : IRuntimeTestScenarioDefinition
{
    private readonly ColdContainerRuntimeScenarioDriver driver = new();

    public RuntimeTestDescriptorDto Descriptor => new()
    {
        TestId = "scenario.cold-container-foreign-rejection",
        DisplayName = "Foreign-owned cold-container deposit rejection",
        Category = "Scenarios",
        Fidelity = "GameplayMethod+ServerAuthority",
        MutationKind = RuntimeTestMutationKind.IsolatedMutation,
        RequiredCapabilities = new[] { "cold-container-quick-move-scenario" },
        TimeoutMilliseconds = 30000,
        IsScenario = true,
        ScenarioOrchestration = RuntimeScenarioOrchestrationKind.InventoryFixturePair,
        FixturePolicy = RuntimeScenarioFixturePolicy.InventoryContainerAndItem,
        CleanupPolicy = RuntimeScenarioCleanupPolicy.RetireInventoryFixturesAndPurgeRepresentations,
        FixtureItemOwnership = RuntimeScenarioFixtureOwnership.HostPlayer,
        DefaultContainerPrefabName = "ItemContainerCrate",
        DefaultItemPrefabName = "lighter"
    };

    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.RejectForeignOwner(command, run);
}
#endif
