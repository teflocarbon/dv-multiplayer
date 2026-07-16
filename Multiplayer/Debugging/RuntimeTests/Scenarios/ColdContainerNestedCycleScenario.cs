#if DEBUG
using Multiplayer.Debugging.Protocol;
using System.Collections;

namespace Multiplayer.Debugging.RuntimeTests.Scenarios;

internal sealed class ColdContainerNestedCycleScenario : IRuntimeTestScenarioDefinition
{
    private readonly ColdContainerRuntimeScenarioDriver driver = new();
    public RuntimeTestDescriptorDto Descriptor => ScenarioDescriptors.Create(
        "scenario.cold-container-nested-cycle-guard",
        "Nested container browse and ancestor-cycle guard", "ItemContainerRegistrator",
        "ItemContainerFolder", 75000);
    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        driver.NestedContainerCycleGuard(command, run);
}
#endif
