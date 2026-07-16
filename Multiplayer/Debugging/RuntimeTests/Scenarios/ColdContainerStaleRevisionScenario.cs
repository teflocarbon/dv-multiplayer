#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class ColdContainerStaleRevisionScenario : IRuntimeTestScenarioDefinition
{
    private readonly ColdContainerRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => ScenarioDescriptors.Create(
        "scenario.cold-container-stale-revision",
        "Reject a stale cold-container withdrawal revision");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.RejectStaleRevision(command, run);
}
#endif
