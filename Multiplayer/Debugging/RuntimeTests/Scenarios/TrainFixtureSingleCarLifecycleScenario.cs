#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class TrainFixtureSingleCarLifecycleScenario : IRuntimeTestScenarioDefinition
{
    private readonly TrainFixtureRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => TrainFixtureScenarioDescriptors.Create(
        "scenario.train-fixture-single-car-lifecycle", "Single locomotive fixture lifecycle and cleanup");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.SingleCarLifecycle(command, run);
}
#endif
