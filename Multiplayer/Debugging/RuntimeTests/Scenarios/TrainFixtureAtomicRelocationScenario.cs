#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class TrainFixtureAtomicRelocationScenario : IRuntimeTestScenarioDefinition
{
    private readonly TrainFixtureRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => TrainFixtureScenarioDescriptors.Create(
        "scenario.train-fixture-atomic-relocation", "Coupled consist atomic relocation and cleanup");
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.AtomicRelocation(command, run);
}
#endif
