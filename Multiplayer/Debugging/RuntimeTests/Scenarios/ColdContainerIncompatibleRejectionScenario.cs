#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class ColdContainerIncompatibleRejectionScenario : IRuntimeTestScenarioDefinition
{
    private readonly ColdContainerRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => ScenarioDescriptors.Create(
        "scenario.cold-container-incompatible-rejection",
        "Runtime compatibility rejects a shovel from a folder",
        "ItemContainerFolder", "shovel", 30000);
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.RejectIncompatibleItem(command, run);
}
#endif
