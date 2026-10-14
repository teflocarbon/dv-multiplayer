#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class LostAndFoundPrivateOwnerScenario :
    IRuntimeTestScenarioDefinition
{
    private readonly LostAndFoundRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => LostAndFoundScenarioDescriptors.Create(
        "scenario.lost-and-found-private-owner-list",
        "Another owner's lost item is never disclosed", "lighter",
        RuntimeScenarioFixtureOwnership.HostPlayer);
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.PrivateOwnerList(command, run);
}
#endif
