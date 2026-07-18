#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class WorldItemSyncEssentialInterestScenario : IRuntimeTestScenarioDefinition
{
    private readonly WorldItemSyncRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => WorldItemSyncScenarioDescriptors.Fixture(
        "scenario.world-item-essential-owner-interest",
        "Essential owner silhouette survives outside spatial interest", "CommsRadio");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.EssentialOwnerClaimRemainsRelevant(command, run);
}
#endif
