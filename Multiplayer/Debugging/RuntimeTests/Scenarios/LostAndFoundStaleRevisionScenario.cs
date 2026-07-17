#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class LostAndFoundStaleRevisionScenario :
    IRuntimeTestScenarioDefinition
{
    private readonly LostAndFoundRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => LostAndFoundScenarioDescriptors.Create(
        "scenario.lost-and-found-stale-revision-recovery",
        "Stale retrieval rejection preserves item and recovers");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.StaleRevisionRecovery(command, run);
}
#endif
