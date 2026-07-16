#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class ColdContainerMoveWithinScenario : IRuntimeTestScenarioDefinition
{
    private readonly ColdContainerRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => ScenarioDescriptors.Create(
        "scenario.cold-container-move-within",
        "Move a cold row within one container");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.MoveWithinRoundTrip(command, run);
}
#endif
