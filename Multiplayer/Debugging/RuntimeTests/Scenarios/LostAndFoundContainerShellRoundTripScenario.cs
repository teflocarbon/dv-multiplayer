#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class LostAndFoundContainerShellRoundTripScenario :
    IRuntimeTestScenarioDefinition
{
    private readonly LostAndFoundRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => LostAndFoundScenarioDescriptors.Create(
        "scenario.lost-and-found-container-shell-round-trip",
        "Personal storage-container shell survives collection and retrieval",
        "ItemContainerCrate");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.AutomaticRoundTrip(command, run);
}
#endif
