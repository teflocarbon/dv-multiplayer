#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class TrainFixtureAnchorValidationScenario : IRuntimeTestScenarioDefinition
{
    private readonly TrainFixtureRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => TrainFixtureScenarioDescriptors.Create(
        "scenario.train-fixture-anchor-validation", "Named train fixture anchor remains valid", true, 20000);
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.AnchorValidation(command, run);
}
#endif
