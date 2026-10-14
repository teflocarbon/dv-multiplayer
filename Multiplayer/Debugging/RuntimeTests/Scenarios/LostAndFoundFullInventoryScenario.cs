#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class LostAndFoundFullInventoryScenario :
    IRuntimeTestScenarioDefinition
{
    private readonly LostAndFoundRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => LostAndFoundScenarioDescriptors.Create(
        "scenario.lost-and-found-full-inventory-recovery",
        "Full inventory rejection preserves item and recovers");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.FullInventoryRecovery(command, run);
}
#endif
