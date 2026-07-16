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
        new() { TestId = "environment.host-latest-save", DisplayName = "Host latest save", Category = "Environment", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "environment-orchestration" }, TimeoutMilliseconds = 15000 },
        new() { TestId = "environment.connect-client", DisplayName = "Connect runtime client", Category = "Environment", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "environment-orchestration" }, TimeoutMilliseconds = 15000 },
        new() { TestId = "player.teleport", DisplayName = "Native player teleport", Category = "Arrangement", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "player-teleport" }, TimeoutMilliseconds = 15000 },
        new() { TestId = "item.pickup", DisplayName = "Raycast world pickup", Category = "Items", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "non-vr-item-interaction" }, TimeoutMilliseconds = 10000 },
        new() { TestId = "item.drop", DisplayName = "Held-item drop", Category = "Items", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "non-vr-item-interaction" }, TimeoutMilliseconds = 10000 },
        new() { TestId = "item.throw", DisplayName = "Held-item throw", Category = "Items", Fidelity = "GameplayMethod", MutationKind = RuntimeTestMutationKind.IsolatedMutation, RequiredCapabilities = new[] { "non-vr-item-interaction" }, TimeoutMilliseconds = 10000 }
    };

    public static string[] Commands => descriptors.Select(item => item.TestId).ToArray();
    public static RuntimeTestDescriptorDto[] Descriptors => descriptors.Select(Clone).ToArray();
    public static RuntimeTestDescriptorDto Find(string id) => descriptors.FirstOrDefault(item => string.Equals(item.TestId, id, StringComparison.Ordinal));

    private static RuntimeTestDescriptorDto Clone(RuntimeTestDescriptorDto value) => new()
    {
        TestId = value.TestId, DisplayName = value.DisplayName, Category = value.Category,
        Fidelity = value.Fidelity, MutationKind = value.MutationKind,
        RequiredCapabilities = value.RequiredCapabilities.ToArray(), TimeoutMilliseconds = value.TimeoutMilliseconds
    };
}
#endif
