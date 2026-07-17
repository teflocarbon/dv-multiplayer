#if DEBUG
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Multiplayer.Debugging.RuntimeTests;

internal interface IRuntimeTestScenarioDefinition
{
    RuntimeTestDescriptorDto Descriptor { get; }
    IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run);
}

internal static class RuntimeTestScenarioRegistry
{
    private static readonly Lazy<IReadOnlyDictionary<string, IRuntimeTestScenarioDefinition>>
        discovered = new(Discover);

    public static RuntimeTestDescriptorDto[] Descriptors => discovered.Value.Values
        .Select(definition => RuntimeTestDescriptorCloner.Clone(definition.Descriptor))
        .OrderBy(descriptor => descriptor.DisplayName, StringComparer.Ordinal)
        .ToArray();

    public static IEnumerator CreateExecution(string testId, RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => testId != null &&
        discovered.Value.TryGetValue(testId, out IRuntimeTestScenarioDefinition definition)
            ? definition.Execute(command, run)
            : null;

    private static IReadOnlyDictionary<string, IRuntimeTestScenarioDefinition> Discover()
    {
        Dictionary<string, IRuntimeTestScenarioDefinition> result =
            new(StringComparer.Ordinal);
        IEnumerable<Type> candidates;
        try
        {
            candidates = typeof(RuntimeTestScenarioRegistry).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            candidates = exception.Types.Where(type => type != null);
        }

        foreach (Type type in candidates.Where(type => type != null && !type.IsAbstract &&
                     typeof(IRuntimeTestScenarioDefinition).IsAssignableFrom(type)))
        {
            IRuntimeTestScenarioDefinition definition =
                (IRuntimeTestScenarioDefinition)Activator.CreateInstance(type, true);
            RuntimeTestDescriptorDto descriptor = definition?.Descriptor ??
                throw new InvalidOperationException($"Runtime scenario {type.FullName} has no descriptor.");
            if (!descriptor.IsScenario)
                throw new InvalidOperationException($"Runtime scenario {type.FullName} is not marked as a scenario.");
            if (string.IsNullOrWhiteSpace(descriptor.TestId))
                throw new InvalidOperationException($"Runtime scenario {type.FullName} has no test ID.");
            if (result.ContainsKey(descriptor.TestId))
                throw new InvalidOperationException($"Duplicate runtime scenario ID: {descriptor.TestId}");
            result.Add(descriptor.TestId, definition);
        }
        return result;
    }
}

internal static class RuntimeTestDescriptorCloner
{
    public static RuntimeTestDescriptorDto Clone(RuntimeTestDescriptorDto value) => new()
    {
        TestId = value.TestId,
        DisplayName = value.DisplayName,
        Category = value.Category,
        Fidelity = value.Fidelity,
        MutationKind = value.MutationKind,
        RequiredCapabilities = value.RequiredCapabilities?.ToArray() ?? Array.Empty<string>(),
        TimeoutMilliseconds = value.TimeoutMilliseconds,
        IsScenario = value.IsScenario,
        ScenarioOrchestration = value.ScenarioOrchestration,
        FixturePolicy = value.FixturePolicy,
        CleanupPolicy = value.CleanupPolicy,
        FixtureContainerOwnership = value.FixtureContainerOwnership,
        FixtureItemOwnership = value.FixtureItemOwnership,
        FixtureItemIsPersonal = value.FixtureItemIsPersonal,
        DefaultContainerPrefabName = value.DefaultContainerPrefabName,
        DefaultItemPrefabName = value.DefaultItemPrefabName
    };
}
#endif
