#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class WorldItemSpatialSettlementReprojectionScenario : IRuntimeTestScenarioDefinition
{
    private readonly WorldItemSyncRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => WorldItemSyncScenarioDescriptors.Fixture(
        "scenario.world-item-spatial-settlement-reprojection",
        "Settled pose survives interest retirement and reprojection",
        timeoutMilliseconds: 90000);
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.SpatialSettlementReprojectsExactPose(command, run);
}
#endif
