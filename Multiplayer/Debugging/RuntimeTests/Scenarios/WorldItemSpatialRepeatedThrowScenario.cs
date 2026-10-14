#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class WorldItemSpatialRepeatedThrowScenario : IRuntimeTestScenarioDefinition
{
    private readonly WorldItemSyncRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => WorldItemSyncScenarioDescriptors.Fixture(
        "scenario.world-item-spatial-repeated-throw",
        "Repeated native throws commit and retire every spatial lease",
        timeoutMilliseconds: 120000);
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.RepeatedSpatialThrowsRetireEveryLease(command, run);
}
#endif
