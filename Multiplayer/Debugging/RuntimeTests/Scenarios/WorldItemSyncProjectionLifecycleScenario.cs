#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class WorldItemSyncProjectionLifecycleScenario : IRuntimeTestScenarioDefinition
{
    private readonly WorldItemSyncRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => WorldItemSyncScenarioDescriptors.Fixture(
        "scenario.world-item-projection-retire-reproject",
        "World item retires outside interest and reprojects on return");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.WorldProjectionRetiresAndReprojects(command, run);
}
#endif
