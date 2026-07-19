#if DEBUG
using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Multiplayer.DebugClient;

internal sealed class ExternalScenarioDefinition
{
    public string TestId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = "External scenarios";
    public string Fidelity { get; set; } = "ExternalOrchestration";
    public int TimeoutMilliseconds { get; set; } = 60000;
    public string[] RequiredCapabilities { get; set; } = Array.Empty<string>();
    public List<ExternalScenarioStep> Steps { get; set; } = new();
    public List<ExternalScenarioStep> Cleanup { get; set; } = new();

    public RuntimeTestDescriptorDto Descriptor() => new()
    {
        TestId = TestId, DisplayName = DisplayName, Category = Category, Fidelity = Fidelity,
        MutationKind = RuntimeTestMutationKind.IsolatedMutation,
        RequiredCapabilities = RequiredCapabilities ?? Array.Empty<string>(),
        TimeoutMilliseconds = TimeoutMilliseconds, IsScenario = true
    };
}

internal sealed class ExternalScenarioStep
{
    public string Id { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Target { get; set; } = "selected";
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Capture { get; set; } = new(StringComparer.Ordinal);
    public List<ExternalScenarioAssertion> Assertions { get; set; } = new();
    public string[] RequiredVariables { get; set; } = Array.Empty<string>();
}

internal sealed class ExternalScenarioAssertion
{
    public string Path { get; set; } = string.Empty;
    [JsonProperty("equals")]
    public JToken Expected { get; set; }
    public bool? Exists { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal sealed class ExternalScenarioCatalog
{
    private readonly string[] roots;
    private readonly object gate = new();
    private DateTime lastScanUtc;
    private Dictionary<string, ExternalScenarioDefinition> definitions = new(StringComparer.Ordinal);
    private string[] errors = Array.Empty<string>();

    public ExternalScenarioCatalog()
    {
        roots = new[]
        {
            // Prefer editable workspace definitions over the build's copied fallback.
            Path.Combine(Environment.CurrentDirectory, "DebugRemoteClient", "Scenarios"),
            Path.Combine(AppContext.BaseDirectory, "Scenarios")
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ExternalScenarioDefinition Find(string id)
    {
        Refresh();
        lock (gate) return id != null && definitions.TryGetValue(id, out ExternalScenarioDefinition value) ? value : null;
    }

    public RuntimeTestDescriptorDto[] Descriptors { get { Refresh(); lock (gate) return definitions.Values.Select(value => value.Descriptor()).ToArray(); } }
    public string[] Errors { get { Refresh(); lock (gate) return errors.ToArray(); } }

    private void Refresh()
    {
        lock (gate)
        {
            if (DateTime.UtcNow - lastScanUtc < TimeSpan.FromMilliseconds(300)) return;
            lastScanUtc = DateTime.UtcNow;
            Dictionary<string, ExternalScenarioDefinition> next = new(StringComparer.Ordinal);
            List<string> failures = new();
            foreach (string file in roots.Where(Directory.Exists).SelectMany(root =>
                Directory.GetFiles(root, "*.scenario.json", SearchOption.AllDirectories)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    ExternalScenarioDefinition definition = JsonConvert.DeserializeObject<ExternalScenarioDefinition>(File.ReadAllText(file));
                    if (definition == null || string.IsNullOrWhiteSpace(definition.TestId) ||
                        string.IsNullOrWhiteSpace(definition.DisplayName) || definition.Steps.Count == 0)
                        throw new InvalidDataException("missing testId, displayName, or steps");
                    // The same definition is copied beside the dashboard during builds. The
                    // first root wins so workspace edits remain hot-reloadable without turning
                    // that deployment fallback into a duplicate-definition error.
                    if (next.ContainsKey(definition.TestId)) continue;
                    next.Add(definition.TestId, definition);
                }
                catch (Exception exception) { failures.Add(Path.GetFileName(file) + ": " + exception.GetBaseException().Message); }
            }
            definitions = next;
            errors = failures.ToArray();
        }
    }
}
#endif
