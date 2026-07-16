#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class ColdContainerReopenRoundTripScenario : IRuntimeTestScenarioDefinition
{
    private readonly ColdContainerRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => ScenarioDescriptors.Create(
        "scenario.cold-container-reopen-round-trip",
        "Cold row survives container UI close and reopen");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.ReopenRoundTrip(command, run);
}
#endif
