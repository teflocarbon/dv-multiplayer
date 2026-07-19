#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class TrainFixtureStartupSafetyScenario : IRuntimeTestScenarioDefinition
{
    private readonly TrainFixtureRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => TrainFixtureScenarioDescriptors.Create(
        "scenario.train-fixture-startup-safety", "Protected locomotive startup and bounded control");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.StartupSafety(command, run);
}
#endif
