#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class TrainFixtureCoupledConsistLifecycleScenario : IRuntimeTestScenarioDefinition
{
    private readonly TrainFixtureRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => TrainFixtureScenarioDescriptors.Create(
        "scenario.train-fixture-coupled-consist-lifecycle", "Coupled locomotive and carriage lifecycle");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.CoupledConsistLifecycle(command, run);
}
#endif
