#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class WorldItemSyncForeignHolderInterestScenario : IRuntimeTestScenarioDefinition
{
    private readonly WorldItemSyncRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => WorldItemSyncScenarioDescriptors.Fixture(
        "scenario.world-item-foreign-holder-interest",
        "Foreign-owned item survives with its distant holder",
        ownership: RuntimeScenarioFixtureOwnership.HostPlayer);
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.ForeignHolderSurvivesInterestTeleport(command, run);
}
#endif
