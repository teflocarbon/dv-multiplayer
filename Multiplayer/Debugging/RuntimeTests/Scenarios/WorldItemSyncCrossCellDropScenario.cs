#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class WorldItemSyncCrossCellDropScenario : IRuntimeTestScenarioDefinition
{
    private readonly WorldItemSyncRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => WorldItemSyncScenarioDescriptors.Fixture(
        "scenario.world-item-cross-cell-held-drop",
        "Held item dropped in another cell is reindexed by the host", timeoutMilliseconds: 75000);
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.CrossCellHeldDropReindexes(command, run);
}
#endif
