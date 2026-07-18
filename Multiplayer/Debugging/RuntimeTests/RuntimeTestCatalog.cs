#if DEBUG
using Multiplayer.Debugging.Protocol;
using System;
using System.Linq;

namespace Multiplayer.Debugging.RuntimeTests;

internal static class RuntimeTestCatalog
{
    private static readonly RuntimeTestDescriptorDto[] descriptors =
    {
        new() { TestId = "runtime.self-check", DisplayName = "Runtime self-check", Category = "Harness", Fidelity = "ReadOnly", MutationKind = RuntimeTestMutationKind.ReadOnly, RequiredCapabilities = new[] { "runtime-self-check" }, TimeoutMilliseconds = 5000 },
        new() { TestId = "environment.status", DisplayName = "Environment readiness", Category = "Environment", Fidelity = "ReadOnly", MutationKind = RuntimeTestMutationKind.ReadOnly, RequiredCapabilities = new[] { "environment-orchestration" }, TimeoutMilliseconds = 5000 },
        new() { TestId = "environment.save-catalog", DisplayName = "Available host saves", Category = "Environment", Fidelity = "ReadOnly", MutationKind = RuntimeTestMutationKind.ReadOnly, RequiredCapabilities = new[] { "environment-orchestration" }, TimeoutMilliseconds = 5000 },
        new() { TestId = "environment.host-save", DisplayName = "Host configured save", Category = "Environment", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "environment-orchestration" }, TimeoutMilliseconds = 15000 },
        new() { TestId = "environment.host-latest-save", DisplayName = "Host latest save (legacy)", Category = "Environment", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "environment-orchestration" }, TimeoutMilliseconds = 15000 },
        new() { TestId = "environment.connect-client", DisplayName = "Connect runtime client", Category = "Environment", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "environment-orchestration" }, TimeoutMilliseconds = 15000 },
        new() { TestId = "player.teleport", DisplayName = "Native player teleport", Category = "Arrangement", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "player-teleport" }, TimeoutMilliseconds = 15000 },
        new() { TestId = "item.pickup", DisplayName = "Raycast world pickup", Category = "Items", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "non-vr-item-interaction" }, TimeoutMilliseconds = 10000 },
        new() { TestId = "item.drop", DisplayName = "Held-item drop", Category = "Items", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "non-vr-item-interaction" }, TimeoutMilliseconds = 10000 },
        new() { TestId = "item.throw", DisplayName = "Held-item throw", Category = "Items", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "non-vr-item-interaction" }, TimeoutMilliseconds = 10000 },
        new() { TestId = "inventory.inspect", DisplayName = "Inspect local inventory", Category = "Inventory", Fidelity = "ReadOnly", MutationKind = RuntimeTestMutationKind.ReadOnly, RequiredCapabilities = new[] { "inventory-runtime-arrangement" }, TimeoutMilliseconds = 5000 },
        new() { TestId = "inventory.prefab-catalog", DisplayName = "List item prefabs", Category = "Inventory", Fidelity = "ReadOnly", MutationKind = RuntimeTestMutationKind.ReadOnly, RequiredCapabilities = new[] { "inventory-runtime-arrangement" }, TimeoutMilliseconds = 5000 },
        new() { TestId = "inventory.fixture-create", DisplayName = "Create authoritative inventory fixture", Category = "Inventory", Fidelity = "DirectState+ServerAuthority", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "host-inventory-fixtures" }, TimeoutMilliseconds = 15000 },
        new() { TestId = "inventory.fixture-place", DisplayName = "Place authoritative inventory fixture", Category = "Inventory", Fidelity = "DirectState+ServerAuthority", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "host-inventory-fixtures" }, TimeoutMilliseconds = 10000 },
        new() { TestId = "inventory.fixture-destroy", DisplayName = "Retire authoritative inventory fixture", Category = "Inventory", Fidelity = "DirectState+ServerAuthority", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "host-inventory-fixtures" }, TimeoutMilliseconds = 10000 },
        new() { TestId = "inventory.fixture-clean-local", DisplayName = "Purge local runtime fixture representations", Category = "Inventory", Fidelity = "DirectState+FixtureIdentity", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "inventory-runtime" }, TimeoutMilliseconds = 10000 },
        new() { TestId = "inventory.local-place", DisplayName = "Arrange item through local DV inventory", Category = "Inventory", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "inventory-runtime-arrangement" }, TimeoutMilliseconds = 10000 }
    };

    private static RuntimeTestDescriptorDto[] AllDescriptors => descriptors
        .Concat(RuntimeTestScenarioRegistry.Descriptors).ToArray();

    public static string[] Commands => AllDescriptors.Select(item => item.TestId).ToArray();
    public static RuntimeTestDescriptorDto[] Descriptors => AllDescriptors
        .Select(RuntimeTestDescriptorCloner.Clone).ToArray();
    public static RuntimeTestDescriptorDto Find(string id) => AllDescriptors.FirstOrDefault(item =>
        string.Equals(item.TestId, id, StringComparison.Ordinal));
}
#endif
