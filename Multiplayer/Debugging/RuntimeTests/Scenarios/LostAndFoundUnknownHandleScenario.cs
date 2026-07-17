#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class LostAndFoundUnknownHandleScenario :
    IRuntimeTestScenarioDefinition
{
    private readonly LostAndFoundRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => LostAndFoundScenarioDescriptors.Create(
        "scenario.lost-and-found-unknown-handle-rejection",
        "Unknown compact handle is rejected without mutation");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.UnknownHandleRejection(command, run);
}
#endif
