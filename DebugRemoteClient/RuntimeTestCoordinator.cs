#if DEBUG
using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Multiplayer.DebugClient;

internal sealed class RuntimeTestCoordinator
{
    private sealed class FakeClient : IRuntimeTestProcessClient
    {
        private readonly DebugSessionInfo session;
        private readonly Dictionary<string, RuntimeTestCommandDto> commands = new();
        public FakeClient(DebugSessionInfo session) { this.session = session; }
        public RuntimeTestCapabilitiesDto GetCapabilities() => new()
        {
            Available = true, MainThreadAgentReady = true, Role = session.Role, PlayerId = session.PlayerId,
            Capabilities = new[] { "runtime-self-check", "cold-container-quick-move-scenario" },
            Commands = new[] { "runtime.self-check", "scenario.cold-container-round-trip",
                "scenario.cold-container-foreign-rejection" },
            Tests = new[]
            {
                new RuntimeTestDescriptorDto { TestId = "runtime.self-check", DisplayName = "Runtime self-check", Category = "Harness" },
                ScenarioDescriptor("scenario.cold-container-round-trip", RuntimeScenarioFixtureOwnership.TargetPlayer),
                ScenarioDescriptor("scenario.cold-container-foreign-rejection", RuntimeScenarioFixtureOwnership.HostPlayer)
            }
        };
        private static RuntimeTestDescriptorDto ScenarioDescriptor(string id,
            RuntimeScenarioFixtureOwnership ownership,
            RuntimeScenarioFixtureOwnership containerOwnership =
                RuntimeScenarioFixtureOwnership.TargetPlayer) => new()
        {
            TestId = id,
            DisplayName = id,
            Category = "Scenarios",
            MutationKind = RuntimeTestMutationKind.IsolatedMutation,
            TimeoutMilliseconds = 60000,
            IsScenario = true,
            ScenarioOrchestration = RuntimeScenarioOrchestrationKind.InventoryFixturePair,
            FixturePolicy = RuntimeScenarioFixturePolicy.InventoryContainerAndItem,
            CleanupPolicy = RuntimeScenarioCleanupPolicy.RetireInventoryFixturesAndPurgeRepresentations,
            FixtureContainerOwnership = containerOwnership,
            FixtureItemOwnership = ownership,
            DefaultContainerPrefabName = "ItemContainerCrate",
            DefaultItemPrefabName = "lighter"
        };
        public RuntimeTestCommandAcceptedDto Enqueue(RuntimeTestCommandDto command)
        {
            commands[command.RequestId] = command;
            return new RuntimeTestCommandAcceptedDto
            {
                RequestId = command.RequestId, RunId = command.RunId, Accepted = true,
                StatusUrl = "/api/runtime-tests/runs/" + command.RequestId
            };
        }
        public RuntimeTestRunDto GetRun(string requestId)
        {
            RuntimeTestCommandDto command = commands[requestId];
            Dictionary<string, object> result = new() { ["session"] = session.SessionId };
            if (command.Command == "inventory.fixture-create")
            {
                bool container = command.Parameters.TryGetValue("prefabName", out string prefab) &&
                    prefab.IndexOf("Container", StringComparison.OrdinalIgnoreCase) >= 0;
                result["netId"] = container ? 101 : 102;
                result["fixtureToken"] = Guid.NewGuid().ToString("D");
            }
            else if (command.Command == "scenario.cold-container-round-trip")
                result["materializedNetId"] = 103;
            else if (command.Command == "inventory.fixture-destroy")
                result["destroyed"] = true;
            else if (command.Command == "inventory.inspect")
                result["items"] = requestId.IndexOf("verify-fixture-cleanup",
                    StringComparison.OrdinalIgnoreCase) >= 0
                    ? new JArray()
                    : new JArray
                    {
                        new JObject
                        {
                            ["netId"] = 101,
                            ["normalVisibleSlot"] = true,
                            ["returnedByActiveQuery"] = true,
                            ["activeSelf"] = true
                        },
                        new JObject
                        {
                            ["netId"] = 102,
                            ["normalVisibleSlot"] = true,
                            ["returnedByActiveQuery"] = true,
                            ["activeSelf"] = true
                        }
                    };
            return new RuntimeTestRunDto
            {
                RequestId = requestId, RunId = command.RunId, Command = command.Command,
                Status = RuntimeTestCommandStatus.Passed,
                QueuedUtc = DateTime.UtcNow.AddMilliseconds(-10),
                StartedUtc = DateTime.UtcNow.AddMilliseconds(-5), CompletedUtc = DateTime.UtcNow,
                Role = session.Role, PlayerId = session.PlayerId, Result = result
            };
        }
        public void Cancel(string requestId) { }
    }

    private sealed class Child
    {
        public DebugSessionInfo Session;
        public IRuntimeTestProcessClient Client;
        public string RequestId;
        public string Stage;
        public RuntimeTestRunDto Last;
    }

    private enum FixtureScenarioStage
    {
        CreatingContainer,
        CreatingItem,
        WaitingForClientFixtures,
        RunningScenario,
        DestroyingItem,
        DestroyingContainer,
        CleaningHostFixtures,
        CleaningClientFixtures,
        VerifyingCleanup,
        Complete
    }

    private sealed class FixtureScenario
    {
        public RuntimeTestCommandDto Command;
        public RuntimeTestDescriptorDto Descriptor;
        public DebugSessionInfo Host;
        public DebugSessionInfo Client;
        public FixtureScenarioStage Stage;
        public Child ActiveChild;
        public ushort ShellNetId;
        public ushort ItemNetId;
        public ushort MaterializedNetId;
        public string ShellFixtureToken = string.Empty;
        public string ItemFixtureToken = string.Empty;
        public string ContainerPrefabName;
        public string ItemPrefabName;
        public bool ForeignOwnedItem;
        public int ProjectionAttempt;
        public DateTime ProjectionDeadlineUtc;
        public DateTime ProjectionNextPollUtc;
        public int CleanupAttempt;
        public DateTime CleanupDeadlineUtc;
        public DateTime CleanupNextPollUtc;
        public RuntimeTestCommandStatus ScenarioStatus = RuntimeTestCommandStatus.Failed;
        public string ScenarioError = string.Empty;
        public readonly List<string> CleanupFailures = new();
        public bool CancellationRequested;
    }

    private sealed class CoordinatedRun
    {
        public RuntimeTestRunDto Parent;
        public List<Child> Children = new();
        public bool IsScenario;
        public bool CaptureStarted;
        public bool CaptureStopped;
        public FixtureScenario FixtureScenario;
    }

    private readonly Func<IEnumerable<DebugSessionInfo>> sessions;
    private readonly Func<DebugSessionInfo, IRuntimeTestProcessClient> clientFactory;
    private readonly Func<string, string, string> startCapture;
    private readonly Func<string> stopCapture;
    private readonly RuntimeTestRunJournal journal;
    private readonly Action<RuntimeTestRunDto> runUpdated;
    private readonly Func<string, IEnumerable<string>> captureFiles;
    private readonly ConcurrentDictionary<string, CoordinatedRun> runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> published = new(StringComparer.Ordinal);

    public RuntimeTestCoordinator(Func<IEnumerable<DebugSessionInfo>> sessions,
        Func<DebugSessionInfo, IRuntimeTestProcessClient> clientFactory = null,
        Func<string, string, string> startCapture = null,
        Func<string> stopCapture = null,
        RuntimeTestRunJournal journal = null,
        Action<RuntimeTestRunDto> runUpdated = null,
        Func<string, IEnumerable<string>> captureFiles = null)
    {
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.clientFactory = clientFactory ?? (session => new RuntimeTestHttpClient(session));
        this.startCapture = startCapture;
        this.stopCapture = stopCapture;
        this.journal = journal;
        this.runUpdated = runUpdated;
        this.captureFiles = captureFiles;
    }

    public static void RunSelfTest()
    {
        RuntimeTestRunJournal.RunSelfTest();
        DebugSessionInfo host = new() { SessionId = "host", Role = "host", PlayerId = 1, FirehoseUrl = "http://127.0.0.1:1/", ApiToken = "host-token" };
        DebugSessionInfo client = new() { SessionId = "client", Role = "client", PlayerId = 2, FirehoseUrl = "http://127.0.0.1:2/", ApiToken = "client-token" };
        Dictionary<string, FakeClient> clients = new()
        {
            [host.SessionId] = new FakeClient(host), [client.SessionId] = new FakeClient(client)
        };
        RuntimeTestCoordinator coordinator = new(() => new[] { host, client }, session => clients[session.SessionId]);
        RuntimeTestCapabilitiesDto capabilities = coordinator.GetCapabilities();
        if (!capabilities.Available || capabilities.Commands.Length != 3 || capabilities.Tests.Length != 3)
            throw new InvalidOperationException("Runtime-test coordinator self-test failed capability aggregation.");
        RuntimeTestCommandAcceptedDto accepted = coordinator.Enqueue(new RuntimeTestCommandDto
        {
            RequestId = "parent", RunId = "coordinator-self-test", Command = "runtime.self-check",
            TargetRole = "all", MutationKind = RuntimeTestMutationKind.ReadOnly
        });
        if (!accepted.Accepted) throw new InvalidOperationException("Runtime-test coordinator self-test rejected a valid aggregate command.");
        RuntimeTestRunDto run = coordinator.GetRun("parent");
        if (run?.Status != RuntimeTestCommandStatus.Passed || run.Processes.Count != 2 ||
            run.Processes.Any(process => process.Status != RuntimeTestCommandStatus.Passed))
            throw new InvalidOperationException("Runtime-test coordinator self-test failed child barrier aggregation.");
        RuntimeTestCommandAcceptedDto ambiguous = coordinator.Enqueue(new RuntimeTestCommandDto
        {
            RequestId = "ambiguous", RunId = "coordinator-self-test", Command = "runtime.self-check",
            MutationKind = RuntimeTestMutationKind.ReadOnly
        });
        if (ambiguous.Accepted || ambiguous.Reason != "target-selector-required")
            throw new InvalidOperationException("Runtime-test coordinator self-test accepted an ambiguous multi-process target.");

        int captureStarts = 0;
        int captureStops = 0;
        RuntimeTestCoordinator scenarioCoordinator = new(() => new[] { host, client },
            session => clients[session.SessionId],
            (name, id) => { captureStarts++; return "capture:" + id; },
            () => { captureStops++; return "stopped"; });
        RuntimeTestCommandAcceptedDto scenarioAccepted = scenarioCoordinator.Enqueue(
            new RuntimeTestCommandDto
            {
                RequestId = "scenario-parent", RunId = "scenario-run",
                CaseId = "cold-container", Command = "scenario.cold-container-round-trip",
                TargetSessionId = client.SessionId,
                MutationKind = RuntimeTestMutationKind.IsolatedMutation
            });
        RuntimeTestRunDto scenarioRun = WaitForSelfTestRun(
            scenarioCoordinator, "scenario-parent");
        if (!scenarioAccepted.Accepted || scenarioRun?.Status != RuntimeTestCommandStatus.Passed ||
            captureStarts != 1 || captureStops != 1 ||
            !Equals(scenarioRun.Result["captureId"], "scenario-run") ||
            !Equals(scenarioRun.Result["selfContained"], true) ||
            !Equals(scenarioRun.Result["cleanupClean"], true) ||
            scenarioRun.Processes.Count != 9)
            throw new InvalidOperationException("Runtime-test coordinator self-test failed scenario capture lifecycle.");

        RuntimeTestCommandAcceptedDto foreignAccepted = scenarioCoordinator.Enqueue(
            new RuntimeTestCommandDto
            {
                RequestId = "foreign-parent", RunId = "foreign-run",
                CaseId = "cold-container-foreign",
                Command = "scenario.cold-container-foreign-rejection",
                TargetSessionId = client.SessionId,
                MutationKind = RuntimeTestMutationKind.IsolatedMutation
            });
        RuntimeTestRunDto foreignRun = WaitForSelfTestRun(
            scenarioCoordinator, "foreign-parent");
        if (!foreignAccepted.Accepted || foreignRun?.Status != RuntimeTestCommandStatus.Passed ||
            !Equals(foreignRun.Result["selfContained"], true) ||
            !Equals(foreignRun.Result["foreignOwnedItem"], true) ||
            !Equals(foreignRun.Result["cleanupClean"], true) ||
            foreignRun.Processes.Count != 9 || captureStarts != 2 || captureStops != 2)
            throw new InvalidOperationException(
                "Runtime-test coordinator self-test failed foreign rejection fixture lifecycle.");
    }

    private static RuntimeTestRunDto WaitForSelfTestRun(
        RuntimeTestCoordinator coordinator, string requestId)
    {
        RuntimeTestRunDto run = null;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            run = coordinator.GetRun(requestId);
            if (run != null && Terminal(run.Status)) return run;
        }
        return run;
    }

    public RuntimeTestCapabilitiesDto GetCapabilities()
    {
        DebugSessionInfo[] targets = LiveRuntimes();
        List<(DebugSessionInfo Session, RuntimeTestCapabilitiesDto Capabilities)> snapshots = new();
        foreach (DebugSessionInfo target in targets)
        {
            try
            {
                RuntimeTestCapabilitiesDto capability = clientFactory(target).GetCapabilities();
                if (capability?.Available == true) snapshots.Add((target, capability));
            }
            catch { }
        }
        return new RuntimeTestCapabilitiesDto
        {
            Available = snapshots.Count > 0,
            MainThreadAgentReady = snapshots.Count > 0 && snapshots.All(item => item.Capabilities.MainThreadAgentReady),
            BuildConfiguration = "Debug",
            Role = "dashboard",
            Capabilities = snapshots.SelectMany(item => item.Capabilities.Capabilities)
                .Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            Commands = snapshots.SelectMany(item => item.Capabilities.Commands)
                .Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            Tests = snapshots.SelectMany(item => item.Capabilities.Tests)
                .GroupBy(item => item.TestId, StringComparer.Ordinal).Select(group => group.First())
                .OrderBy(item => item.Category).ThenBy(item => item.DisplayName).ToArray(),
            Anchors = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["processes"] = snapshots.Select(item => new
                {
                    item.Session.SessionId, item.Session.Role, item.Session.PlayerId,
                    item.Capabilities.Scene, item.Capabilities.Capabilities, item.Capabilities.Anchors
                }).ToArray()
            }
        };
    }

    public RuntimeTestCommandAcceptedDto Enqueue(RuntimeTestCommandDto command)
    {
        if (command == null) return Rejected(null, "missing-command-body");
        command.RequestId = string.IsNullOrWhiteSpace(command.RequestId) ? Guid.NewGuid().ToString("N") : command.RequestId.Trim();
        command.RunId = string.IsNullOrWhiteSpace(command.RunId) ? "run-" + Guid.NewGuid().ToString("N") : command.RunId.Trim();
        if (runs.ContainsKey(command.RequestId)) return Rejected(command, "duplicate-request-id");
        RuntimeTestDescriptorDto descriptor = FindDescriptor(command.Command);
        bool scenario = descriptor?.IsScenario == true;
        if (scenario && runs.Values.Any(candidate => !Terminal(candidate.Parent.Status) &&
                candidate.IsScenario))
            return Rejected(command, "another-scenario-is-running");
        if (scenario && descriptor.ScenarioOrchestration ==
            RuntimeScenarioOrchestrationKind.InventoryFixturePair)
            return EnqueueFixtureBackedScenario(command, descriptor);
        if (scenario && descriptor.ScenarioOrchestration !=
            RuntimeScenarioOrchestrationKind.None)
            return Rejected(command, "unsupported-scenario-orchestration:" +
                descriptor.ScenarioOrchestration);
        DebugSessionInfo[] targets = SelectTargets(command);
        if (targets.Length == 0) return Rejected(command, "no-matching-runtime-session");
        if (string.IsNullOrWhiteSpace(command.TargetSessionId) && string.IsNullOrWhiteSpace(command.TargetRole) && !command.TargetPlayerId.HasValue && targets.Length > 1)
            return Rejected(command, "target-selector-required");

        CoordinatedRun coordinated = new()
        {
            IsScenario = scenario,
            Parent = new RuntimeTestRunDto
            {
                RequestId = command.RequestId, RunId = command.RunId, CaseId = command.CaseId,
                PhaseId = command.PhaseId, StepId = command.StepId, Command = command.Command,
                Status = RuntimeTestCommandStatus.Queued, QueuedUtc = DateTime.UtcNow, Role = "dashboard"
            }
        };
        if (scenario && startCapture != null)
        {
            string captureId = command.RunId;
            string capturePath = startCapture(CaptureName(command), captureId);
            coordinated.CaptureStarted = true;
            coordinated.Parent.Result["captureId"] = captureId;
            coordinated.Parent.Result["capturePath"] = capturePath ?? string.Empty;
        }
        foreach (DebugSessionInfo target in targets)
        {
            IRuntimeTestProcessClient client = clientFactory(target);
            string childId = command.RequestId + "-" + target.SessionId;
            RuntimeTestCommandDto childCommand = CloneForChild(command, childId);
            RuntimeTestCommandAcceptedDto accepted;
            try { accepted = client.Enqueue(childCommand); }
            catch (Exception exception) { accepted = Rejected(childCommand, exception.GetBaseException().Message); }
            RuntimeTestRunDto initial = new()
            {
                RequestId = childId, RunId = command.RunId, CaseId = command.CaseId,
                PhaseId = command.PhaseId, StepId = command.StepId, Command = command.Command,
                Status = accepted?.Accepted == true ? RuntimeTestCommandStatus.Queued : RuntimeTestCommandStatus.Failed,
                QueuedUtc = DateTime.UtcNow, Role = target.Role, PlayerId = target.PlayerId,
                Error = accepted?.Accepted == true ? string.Empty : accepted?.Reason ?? "command-rejected"
            };
            coordinated.Children.Add(new Child { Session = target, Client = client, RequestId = childId, Last = initial });
        }
        RefreshParent(coordinated);
        if (!coordinated.Children.Any(child => child.Last.Status != RuntimeTestCommandStatus.Failed))
            return Rejected(command, "all-runtime-sessions-rejected-command");
        runs[command.RequestId] = coordinated;
        Publish(coordinated.Parent);
        return new RuntimeTestCommandAcceptedDto
        {
            RequestId = command.RequestId, RunId = command.RunId, Accepted = true,
            StatusUrl = "/api/runtime-tests/runs/" + Uri.EscapeDataString(command.RequestId)
        };
    }

    public RuntimeTestRunDto GetRun(string requestId)
    {
        if (requestId == null || !runs.TryGetValue(requestId, out CoordinatedRun run))
            return journal?.Get(requestId);
        lock (run)
        {
            if (run.FixtureScenario != null)
            {
                AdvanceFixtureScenario(run);
                Publish(run.Parent);
                return Clone(run.Parent);
            }
            foreach (Child child in run.Children.Where(child => !Terminal(child.Last.Status)))
            {
                try { child.Last = child.Client.GetRun(child.RequestId) ?? child.Last; }
                catch (Exception exception)
                {
                    child.Last.Status = RuntimeTestCommandStatus.Failed;
                    child.Last.Error = "status-poll-failed:" + exception.GetBaseException().Message;
                    child.Last.CompletedUtc = DateTime.UtcNow;
                }
            }
            RefreshParent(run);
            Publish(run.Parent);
            return Clone(run.Parent);
        }
    }

    public RuntimeTestRunSummaryDto[] GetRuns()
    {
        foreach (string requestId in runs.Keys.ToArray()) GetRun(requestId);
        return journal?.List() ?? runs.Values.Select(item => item.Parent)
            .OrderByDescending(item => item.QueuedUtc)
            .Select(item => new RuntimeTestRunSummaryDto
            {
                RequestId = item.RequestId, RunId = item.RunId, CaseId = item.CaseId,
                Command = item.Command, Status = item.Status, QueuedUtc = item.QueuedUtc,
                StartedUtc = item.StartedUtc, CompletedUtc = item.CompletedUtc,
                PhaseId = item.PhaseId, StepId = item.StepId, Error = item.Error,
                ProcessCount = item.Processes?.Count ?? 0
            }).ToArray();
    }

    public bool Cancel(string requestId)
    {
        if (requestId == null || !runs.TryGetValue(requestId, out CoordinatedRun run)) return false;
        lock (run)
        {
            if (run.FixtureScenario != null)
            {
                run.FixtureScenario.CancellationRequested = true;
                Child activeChild = run.FixtureScenario.ActiveChild;
                    bool cleaning = run.FixtureScenario.Stage is
                        FixtureScenarioStage.DestroyingItem or
                        FixtureScenarioStage.DestroyingContainer or
                        FixtureScenarioStage.CleaningHostFixtures or
                        FixtureScenarioStage.CleaningClientFixtures or
                        FixtureScenarioStage.VerifyingCleanup;
                if (!cleaning && activeChild != null &&
                    !Terminal(activeChild.Last.Status))
                {
                    try { activeChild.Client.Cancel(activeChild.RequestId); }
                    catch { }
                }
                return true;
            }
            bool requested = false;
            foreach (Child child in run.Children.Where(child => !Terminal(child.Last.Status)))
            {
                try { child.Client.Cancel(child.RequestId); requested = true; } catch { }
            }
            return requested;
        }
    }

    private RuntimeTestCommandAcceptedDto EnqueueFixtureBackedScenario(
        RuntimeTestCommandDto command, RuntimeTestDescriptorDto descriptor)
    {
        if (descriptor.FixturePolicy != RuntimeScenarioFixturePolicy.InventoryContainerAndItem)
            return Rejected(command, "unsupported-scenario-fixture-policy:" +
                descriptor.FixturePolicy);
        if (descriptor.CleanupPolicy !=
            RuntimeScenarioCleanupPolicy.RetireInventoryFixturesAndPurgeRepresentations)
            return Rejected(command, "unsupported-scenario-cleanup-policy:" +
                descriptor.CleanupPolicy);
        DebugSessionInfo[] live = LiveRuntimes();
        IEnumerable<DebugSessionInfo> clients = live.Where(session =>
            string.Equals(session.Role, "client", StringComparison.OrdinalIgnoreCase) &&
            session.PlayerId.HasValue);
        if (!string.IsNullOrWhiteSpace(command.TargetSessionId))
            clients = clients.Where(session => string.Equals(session.SessionId,
                command.TargetSessionId, StringComparison.Ordinal));
        if (command.TargetPlayerId.HasValue)
            clients = clients.Where(session => session.PlayerId == command.TargetPlayerId);
        if (!string.IsNullOrWhiteSpace(command.TargetRole) &&
            !string.Equals(command.TargetRole, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(command.TargetRole, "client", StringComparison.OrdinalIgnoreCase))
            clients = Array.Empty<DebugSessionInfo>();
        DebugSessionInfo[] clientTargets = clients.OrderBy(session => session.PlayerId).ToArray();
        if (clientTargets.Length == 0)
            return Rejected(command, "fixture-scenario-client-not-found");
        if (clientTargets.Length > 1)
            return Rejected(command, "fixture-scenario-client-selector-required");
        DebugSessionInfo[] hosts = live.Where(session =>
            string.Equals(session.Role, "host", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hosts.Length != 1)
            return Rejected(command, hosts.Length == 0 ? "fixture-scenario-host-not-found" :
                "fixture-scenario-host-ambiguous");

        CoordinatedRun coordinated = new()
        {
            IsScenario = true,
            Parent = new RuntimeTestRunDto
            {
                RequestId = command.RequestId, RunId = command.RunId,
                CaseId = command.CaseId, PhaseId = "arrange",
                StepId = "create-container-fixture", Command = command.Command,
                Status = RuntimeTestCommandStatus.Running, QueuedUtc = DateTime.UtcNow,
                StartedUtc = DateTime.UtcNow, Role = "dashboard"
            },
            FixtureScenario = new FixtureScenario
            {
                Command = command,
                Descriptor = descriptor,
                Host = hosts[0],
                Client = clientTargets[0],
                ContainerPrefabName = Parameter(command, "containerPrefabName",
                    descriptor.DefaultContainerPrefabName),
                ItemPrefabName = Parameter(command, "itemPrefabName",
                    descriptor.DefaultItemPrefabName),
                ForeignOwnedItem = descriptor.FixtureItemOwnership ==
                    RuntimeScenarioFixtureOwnership.HostPlayer,
                Stage = FixtureScenarioStage.CreatingContainer
            }
        };
        if (startCapture != null)
        {
            string capturePath = startCapture(CaptureName(command),
                command.RunId);
            coordinated.CaptureStarted = true;
            coordinated.Parent.Result["captureId"] = command.RunId;
            coordinated.Parent.Result["capturePath"] = capturePath ?? string.Empty;
        }
        coordinated.Parent.Result["selfContained"] = true;
        coordinated.Parent.Result["fixtureOwnerPlayerId"] = clientTargets[0].PlayerId;
        coordinated.Parent.Result["foreignOwnedItem"] =
            coordinated.FixtureScenario.ForeignOwnedItem;
        coordinated.FixtureScenario.ActiveChild = EnqueueStage(coordinated,
            hosts[0], "create-container-fixture", "inventory.fixture-create",
            "arrange", FixtureParameters(command, container: true,
                descriptor.FixtureContainerOwnership ==
                    RuntimeScenarioFixtureOwnership.HostPlayer
                        ? hosts[0].PlayerId.Value
                        : clientTargets[0].PlayerId.Value,
                clientTargets[0].PlayerId.Value,
                coordinated.FixtureScenario.ContainerPrefabName), 15000);
        runs[command.RequestId] = coordinated;
        Publish(coordinated.Parent);
        return new RuntimeTestCommandAcceptedDto
        {
            RequestId = command.RequestId, RunId = command.RunId, Accepted = true,
            StatusUrl = "/api/runtime-tests/runs/" +
                Uri.EscapeDataString(command.RequestId)
        };
    }

    private void AdvanceFixtureScenario(CoordinatedRun run)
    {
        FixtureScenario scenario = run.FixtureScenario;
        for (int transitions = 0; transitions < 8 &&
             scenario.Stage != FixtureScenarioStage.Complete; transitions++)
        {
            Child child = scenario.ActiveChild;
            if (child != null && !Terminal(child.Last.Status))
            {
                try { child.Last = child.Client.GetRun(child.RequestId) ?? child.Last; }
                catch (Exception exception)
                {
                    child.Last.Status = RuntimeTestCommandStatus.Failed;
                    child.Last.Error = "status-poll-failed:" +
                        exception.GetBaseException().Message;
                    child.Last.CompletedUtc = DateTime.UtcNow;
                }
            }
            if (child != null && !Terminal(child.Last.Status))
            {
                if (scenario.CancellationRequested)
                    try { child.Client.Cancel(child.RequestId); } catch { }
                RefreshFixtureParent(run);
                return;
            }

            switch (scenario.Stage)
            {
                case FixtureScenarioStage.CreatingContainer:
                    if (!StagePassed(child) ||
                        !TryResultUShort(child?.Last, "netId", out scenario.ShellNetId))
                    {
                        SetScenarioFailure(scenario, child, "container-fixture-create-failed");
                        FinishFixtureScenario(run);
                        return;
                    }
                    run.Parent.Result["shellNetId"] = scenario.ShellNetId;
                    if (!TryResultString(child?.Last, "fixtureToken",
                            out scenario.ShellFixtureToken))
                    {
                        SetScenarioFailure(scenario, child,
                            "container-fixture-token-missing");
                        FinishFixtureScenario(run);
                        return;
                    }
                    if (scenario.CancellationRequested)
                    {
                        scenario.ScenarioStatus = RuntimeTestCommandStatus.Cancelled;
                        scenario.ScenarioError = "cancelled";
                        BeginContainerCleanup(run);
                        break;
                    }
                    scenario.Stage = FixtureScenarioStage.CreatingItem;
                    scenario.ActiveChild = EnqueueStage(run, scenario.Host,
                        "create-item-fixture", "inventory.fixture-create", "arrange",
                        FixtureParameters(scenario.Command, container: false,
                            scenario.ForeignOwnedItem ? scenario.Host.PlayerId.Value :
                                scenario.Client.PlayerId.Value,
                            scenario.Client.PlayerId.Value,
                            scenario.ItemPrefabName), 15000);
                    break;

                case FixtureScenarioStage.CreatingItem:
                    if (!StagePassed(child) ||
                        !TryResultUShort(child?.Last, "netId", out scenario.ItemNetId))
                    {
                        SetScenarioFailure(scenario, child, "item-fixture-create-failed");
                        BeginContainerCleanup(run);
                        break;
                    }
                    run.Parent.Result["itemNetId"] = scenario.ItemNetId;
                    if (!TryResultString(child?.Last, "fixtureToken",
                            out scenario.ItemFixtureToken))
                    {
                        SetScenarioFailure(scenario, child, "item-fixture-token-missing");
                        BeginContainerCleanup(run);
                        break;
                    }
                    if (scenario.CancellationRequested)
                    {
                        scenario.ScenarioStatus = RuntimeTestCommandStatus.Cancelled;
                        scenario.ScenarioError = "cancelled";
                        BeginItemCleanup(run);
                        break;
                    }
                    scenario.Stage = FixtureScenarioStage.WaitingForClientFixtures;
                    scenario.ProjectionAttempt = 1;
                    scenario.ProjectionDeadlineUtc = DateTime.UtcNow.AddSeconds(15);
                    scenario.ProjectionNextPollUtc = DateTime.UtcNow.AddMilliseconds(250);
                    scenario.ActiveChild = EnqueueStage(run, scenario.Client,
                        "wait-for-client-fixtures-1", "inventory.inspect", "arrange",
                        FixtureInspectParameters(scenario), 5000);
                    break;

                case FixtureScenarioStage.WaitingForClientFixtures:
                    if (!StagePassed(child))
                    {
                        SetScenarioFailure(scenario, child,
                            "client-fixture-projection-inspect-failed");
                        BeginItemCleanup(run);
                        break;
                    }
                    if (!FixtureProjectionObserved(scenario, child.Last, out string projectionError))
                    {
                        if (DateTime.UtcNow >= scenario.ProjectionDeadlineUtc)
                        {
                            scenario.ScenarioStatus = RuntimeTestCommandStatus.Failed;
                            scenario.ScenarioError = "fixture-projection-timeout:" + projectionError;
                            BeginItemCleanup(run);
                            break;
                        }
                        if (DateTime.UtcNow < scenario.ProjectionNextPollUtc)
                        {
                            RefreshFixtureParent(run);
                            return;
                        }
                        scenario.ProjectionAttempt++;
                        scenario.ProjectionNextPollUtc = DateTime.UtcNow.AddMilliseconds(250);
                        scenario.ActiveChild = EnqueueStage(run, scenario.Client,
                            "wait-for-client-fixtures-" + scenario.ProjectionAttempt,
                            "inventory.inspect", "arrange",
                            FixtureInspectParameters(scenario), 5000);
                        break;
                    }
                    run.Parent.Result["fixtureProjectionAttempts"] = scenario.ProjectionAttempt;
                    scenario.Stage = FixtureScenarioStage.RunningScenario;
                    Dictionary<string, string> parameters = new(
                        scenario.Command.Parameters ?? new(),
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["shellNetId"] = scenario.ShellNetId.ToString(
                            CultureInfo.InvariantCulture),
                        ["itemNetId"] = scenario.ItemNetId.ToString(
                            CultureInfo.InvariantCulture),
                        ["shellFixtureToken"] = scenario.ShellFixtureToken,
                        ["itemFixtureToken"] = scenario.ItemFixtureToken
                    };
                    scenario.ActiveChild = EnqueueStage(run, scenario.Client,
                        "client-scenario", scenario.Command.Command, "act", parameters,
                        Math.Max(scenario.Descriptor.TimeoutMilliseconds,
                            scenario.Command.TimeoutMilliseconds));
                    break;

                case FixtureScenarioStage.RunningScenario:
                    scenario.ScenarioStatus = child?.Last.Status ??
                        RuntimeTestCommandStatus.Failed;
                    scenario.ScenarioError = child?.Last.Error ?? "scenario-result-missing";
                    if (child?.Last?.Result != null)
                    {
                        run.Parent.Result["scenarioResult"] =
                            new Dictionary<string, object>(child.Last.Result,
                                StringComparer.Ordinal);
                        foreach (KeyValuePair<string, object> pair in child.Last.Result)
                            if (!run.Parent.Result.ContainsKey(pair.Key))
                                run.Parent.Result[pair.Key] = pair.Value;
                        TryResultUShort(child.Last, "materializedNetId",
                            out scenario.MaterializedNetId);
                    }
                    BeginItemCleanup(run);
                    break;

                case FixtureScenarioStage.DestroyingItem:
                    if (!StagePassed(child))
                        scenario.CleanupFailures.Add("item-fixture:" +
                            StageError(child, "destroy-failed"));
                    BeginContainerCleanup(run);
                    break;

                case FixtureScenarioStage.DestroyingContainer:
                    if (!StagePassed(child))
                        scenario.CleanupFailures.Add("container-fixture:" +
                            StageError(child, "destroy-failed"));
                    scenario.Stage = FixtureScenarioStage.CleaningHostFixtures;
                    scenario.ActiveChild = EnqueueStage(run, scenario.Host,
                        "purge-host-fixture-representations",
                        "inventory.fixture-clean-local", "cleanup",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["shellFixtureToken"] = scenario.ShellFixtureToken,
                            ["itemFixtureToken"] = scenario.ItemFixtureToken
                        }, 10000);
                    break;

                case FixtureScenarioStage.CleaningHostFixtures:
                    if (!StagePassed(child))
                        scenario.CleanupFailures.Add("host-fixture-purge:" +
                            StageError(child, "purge-failed"));
                    scenario.Stage = FixtureScenarioStage.CleaningClientFixtures;
                    scenario.ActiveChild = EnqueueStage(run, scenario.Client,
                        "purge-client-fixture-representations",
                        "inventory.fixture-clean-local", "cleanup",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["shellFixtureToken"] = scenario.ShellFixtureToken,
                            ["itemFixtureToken"] = scenario.ItemFixtureToken
                        }, 10000);
                    break;

                case FixtureScenarioStage.CleaningClientFixtures:
                    if (!StagePassed(child))
                        scenario.CleanupFailures.Add("client-fixture-purge:" +
                            StageError(child, "purge-failed"));
                    scenario.Stage = FixtureScenarioStage.VerifyingCleanup;
                    scenario.CleanupAttempt = 1;
                    scenario.CleanupDeadlineUtc = DateTime.UtcNow.AddSeconds(15);
                    scenario.CleanupNextPollUtc = DateTime.UtcNow.AddMilliseconds(250);
                    scenario.ActiveChild = EnqueueStage(run, scenario.Client,
                        "verify-fixture-cleanup-1", "inventory.inspect", "cleanup",
                        FixtureInspectParameters(scenario), 5000);
                    break;

                case FixtureScenarioStage.VerifyingCleanup:
                    if (!StagePassed(child))
                    {
                        scenario.CleanupFailures.Add("cleanup-verification:" +
                            StageError(child, "inventory-inspect-failed"));
                        FinishFixtureScenario(run);
                        return;
                    }
                    if (!FixtureCleanupObserved(scenario, child.Last, out string error))
                    {
                        if (DateTime.UtcNow >= scenario.CleanupDeadlineUtc)
                        {
                            scenario.CleanupFailures.Add("cleanup-verification-timeout:" + error);
                            FinishFixtureScenario(run);
                            return;
                        }
                        if (DateTime.UtcNow < scenario.CleanupNextPollUtc)
                        {
                            RefreshFixtureParent(run);
                            return;
                        }
                        scenario.CleanupAttempt++;
                        scenario.CleanupNextPollUtc = DateTime.UtcNow.AddMilliseconds(250);
                        scenario.ActiveChild = EnqueueStage(run, scenario.Client,
                            "verify-fixture-cleanup-" + scenario.CleanupAttempt,
                            "inventory.inspect", "cleanup",
                            FixtureInspectParameters(scenario), 5000);
                        break;
                    }
                    run.Parent.Result["fixtureCleanupAttempts"] = scenario.CleanupAttempt;
                    FinishFixtureScenario(run);
                    return;
            }
        }
        RefreshFixtureParent(run);
    }

    private void BeginItemCleanup(CoordinatedRun run)
    {
        FixtureScenario scenario = run.FixtureScenario;
        scenario.Stage = FixtureScenarioStage.DestroyingItem;
        ushort netId = scenario.MaterializedNetId != 0 ? scenario.MaterializedNetId :
            scenario.ItemNetId;
        scenario.ActiveChild = EnqueueStage(run, scenario.Host,
            "destroy-item-fixture", "inventory.fixture-destroy", "cleanup",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["netId"] = netId.ToString(CultureInfo.InvariantCulture),
                ["fixtureToken"] = scenario.ItemFixtureToken
            }, 10000);
    }

    private void BeginContainerCleanup(CoordinatedRun run)
    {
        FixtureScenario scenario = run.FixtureScenario;
        scenario.Stage = FixtureScenarioStage.DestroyingContainer;
        scenario.ActiveChild = EnqueueStage(run, scenario.Host,
            "destroy-container-fixture", "inventory.fixture-destroy", "cleanup",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["netId"] = scenario.ShellNetId.ToString(CultureInfo.InvariantCulture),
                ["fixtureToken"] = scenario.ShellFixtureToken
            }, 10000);
    }

    private Child EnqueueStage(CoordinatedRun run, DebugSessionInfo session,
        string stage, string commandName, string phase,
        Dictionary<string, string> parameters, int timeoutMilliseconds)
    {
        string requestId = run.Parent.RequestId + "-" + stage;
        RuntimeTestCommandDto command = new()
        {
            RequestId = requestId,
            RunId = run.Parent.RunId,
            CaseId = run.Parent.CaseId,
            PhaseId = phase,
            StepId = stage,
            Command = commandName,
            MutationKind = string.Equals(commandName, "inventory.inspect",
                StringComparison.Ordinal) ? RuntimeTestMutationKind.ReadOnly :
                RuntimeTestMutationKind.IsolatedMutation,
            TimeoutMilliseconds = timeoutMilliseconds,
            Parameters = parameters ?? new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
        };
        IRuntimeTestProcessClient client = clientFactory(session);
        RuntimeTestCommandAcceptedDto accepted;
        try { accepted = client.Enqueue(command); }
        catch (Exception exception)
        {
            accepted = Rejected(command, exception.GetBaseException().Message);
        }
        Child child = new()
        {
            Session = session,
            Client = client,
            RequestId = requestId,
            Stage = stage,
            Last = new RuntimeTestRunDto
            {
                RequestId = requestId, RunId = command.RunId,
                CaseId = command.CaseId, PhaseId = phase, StepId = stage,
                Command = commandName,
                Status = accepted?.Accepted == true ? RuntimeTestCommandStatus.Queued :
                    RuntimeTestCommandStatus.Failed,
                QueuedUtc = DateTime.UtcNow, Role = session.Role,
                PlayerId = session.PlayerId,
                Error = accepted?.Accepted == true ? string.Empty :
                    accepted?.Reason ?? "command-rejected"
            }
        };
        run.Children.Add(child);
        RefreshFixtureParent(run);
        return child;
    }

    private static Dictionary<string, string> FixtureParameters(
        RuntimeTestCommandDto command, bool container, byte ownerPlayerId,
        byte holderPlayerId, string defaultPrefabName)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase)
        {
            ["prefabName"] = Parameter(command, container ? "containerPrefabName" :
                "itemPrefabName", defaultPrefabName),
            ["ownerPlayerId"] = ownerPlayerId.ToString(CultureInfo.InvariantCulture),
            ["holderPlayerId"] = holderPlayerId.ToString(CultureInfo.InvariantCulture),
            ["placement"] = "inventory"
        };
        return result;
    }

    private static Dictionary<string, string> FixtureInspectParameters(
        FixtureScenario scenario) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["shellNetId"] = scenario.ShellNetId.ToString(CultureInfo.InvariantCulture),
        ["itemNetId"] = scenario.ItemNetId.ToString(CultureInfo.InvariantCulture),
        ["materializedNetId"] = scenario.MaterializedNetId.ToString(
            CultureInfo.InvariantCulture)
    };

    private static string Parameter(RuntimeTestCommandDto command, string key,
        string fallback) => command.Parameters != null &&
        command.Parameters.TryGetValue(key, out string value) &&
        !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static void SetScenarioFailure(FixtureScenario scenario, Child child,
        string fallback)
    {
        scenario.ScenarioStatus = child?.Last.Status ==
            RuntimeTestCommandStatus.Cancelled ? RuntimeTestCommandStatus.Cancelled :
            RuntimeTestCommandStatus.Failed;
        scenario.ScenarioError = StageError(child, fallback);
    }

    private void FinishFixtureScenario(CoordinatedRun run)
    {
        FixtureScenario scenario = run.FixtureScenario;
        scenario.Stage = FixtureScenarioStage.Complete;
        run.Parent.Result["cleanupFailures"] = scenario.CleanupFailures.ToArray();
        run.Parent.Result["cleanupClean"] = scenario.CleanupFailures.Count == 0;
        run.Parent.Result["materializedNetId"] = scenario.MaterializedNetId;
        run.Parent.Status = scenario.CleanupFailures.Count > 0 ?
            RuntimeTestCommandStatus.FailedDirty : scenario.ScenarioStatus;
        run.Parent.Error = scenario.CleanupFailures.Count > 0
            ? string.Join("; ", scenario.CleanupFailures)
            : scenario.ScenarioError;
        if (run.Parent.Status == RuntimeTestCommandStatus.Passed)
            run.Parent.Error = string.Empty;
        run.Parent.CompletedUtc = DateTime.UtcNow;
        RefreshFixtureParent(run);
        StopCapture(run);
        AttachCaptureFiles(run.Parent);
    }

    private void RefreshFixtureParent(CoordinatedRun run)
    {
        FixtureScenario scenario = run.FixtureScenario;
        run.Parent.Processes = run.Children.Select(child => new RuntimeTestProcessRunDto
        {
            SessionId = child.Session.SessionId,
            Role = child.Session.Role,
            PlayerId = child.Session.PlayerId,
            RequestId = child.RequestId,
            PhaseId = child.Last.PhaseId,
            StepId = child.Last.StepId,
            Command = child.Last.Command,
            Status = child.Last.Status,
            Error = child.Last.Error,
            Result = new Dictionary<string, object>(child.Last.Result ?? new(),
                StringComparer.Ordinal)
        }).ToList();
        if (scenario.Stage != FixtureScenarioStage.Complete)
        {
            run.Parent.Status = RuntimeTestCommandStatus.Running;
            run.Parent.PhaseId = scenario.Stage is FixtureScenarioStage.DestroyingItem or
                FixtureScenarioStage.DestroyingContainer or
                FixtureScenarioStage.CleaningHostFixtures or
                FixtureScenarioStage.CleaningClientFixtures or
                FixtureScenarioStage.VerifyingCleanup ? "cleanup" :
                scenario.Stage == FixtureScenarioStage.RunningScenario ? "act" : "arrange";
            run.Parent.StepId = scenario.ActiveChild?.Stage ?? scenario.Stage.ToString();
        }
    }

    private void StopCapture(CoordinatedRun run)
    {
        if (!run.CaptureStarted || run.CaptureStopped) return;
        run.CaptureStopped = true;
        try { run.Parent.Result["captureStopPath"] = stopCapture?.Invoke() ?? string.Empty; }
        catch (Exception exception)
        {
            run.Parent.Result["captureStopError"] =
                exception.GetBaseException().Message;
        }
    }

    private static bool StagePassed(Child child) =>
        child?.Last?.Status == RuntimeTestCommandStatus.Passed;

    private static string StageError(Child child, string fallback) =>
        !string.IsNullOrWhiteSpace(child?.Last?.Error) ? child.Last.Error : fallback;

    private static bool TryResultUShort(RuntimeTestRunDto run, string key,
        out ushort value)
    {
        value = 0;
        if (run?.Result == null || !run.Result.TryGetValue(key, out object raw) ||
            raw == null) return false;
        try { value = Convert.ToUInt16(raw, CultureInfo.InvariantCulture); }
        catch { return false; }
        return value != 0;
    }

    private static bool TryResultString(RuntimeTestRunDto run, string key,
        out string value)
    {
        value = string.Empty;
        if (run?.Result == null || !run.Result.TryGetValue(key, out object raw) ||
            raw == null) return false;
        value = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool FixtureCleanupObserved(FixtureScenario scenario,
        RuntimeTestRunDto run, out string error)
    {
        error = string.Empty;
        if (run?.Result == null || !run.Result.TryGetValue("items", out object raw))
        {
            error = "inventory-items-missing";
            return false;
        }
        JArray items = raw as JArray ?? JArray.FromObject(raw ?? Array.Empty<object>());
        ushort itemId = scenario.MaterializedNetId != 0 ? scenario.MaterializedNetId :
            scenario.ItemNetId;
        foreach (JObject item in items.OfType<JObject>())
        {
            ushort netId = item.Value<ushort?>("netId") ?? 0;
            string prefab = item.Value<string>("prefabName") ?? string.Empty;
            if (netId == scenario.ShellNetId || netId == itemId)
            {
                error = "fixture-netid-still-present:" + netId;
                return false;
            }
            if (netId == 0 && (string.Equals(prefab, scenario.ContainerPrefabName,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prefab, scenario.ItemPrefabName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                error = "dead-fixture-slot-still-present:" + prefab;
                return false;
            }
        }
        if (run.Result.TryGetValue("representations", out object representationsRaw))
        {
            JArray representations = representationsRaw as JArray ??
                JArray.FromObject(representationsRaw ?? Array.Empty<object>());
            JObject remaining = representations.OfType<JObject>().FirstOrDefault();
            if (remaining != null)
            {
                error = "fixture-representation-still-present:" +
                    (remaining.Value<ushort?>("netId") ?? 0) + ":" +
                    (remaining.Value<string>("itemState") ?? "unknown");
                return false;
            }
        }
        return true;
    }

    private static bool FixtureProjectionObserved(FixtureScenario scenario,
        RuntimeTestRunDto run, out string error)
    {
        error = string.Empty;
        if (run?.Result == null || !run.Result.TryGetValue("items", out object raw))
        {
            error = "inventory-items-missing";
            return false;
        }
        JArray items = raw as JArray ?? JArray.FromObject(raw ?? Array.Empty<object>());
        JObject shell = items.OfType<JObject>().FirstOrDefault(item =>
            (item.Value<ushort?>("netId") ?? 0) == scenario.ShellNetId);
        JObject payload = items.OfType<JObject>().FirstOrDefault(item =>
            (item.Value<ushort?>("netId") ?? 0) == scenario.ItemNetId);
        if (shell == null || payload == null)
        {
            error = $"missing:{(shell == null ? "container" : string.Empty)}" +
                $"{(shell == null && payload == null ? "," : string.Empty)}" +
                $"{(payload == null ? "item" : string.Empty)}";
            return false;
        }
        if (shell.Value<bool?>("normalVisibleSlot") != true ||
            payload.Value<bool?>("normalVisibleSlot") != true ||
            shell.Value<bool?>("returnedByActiveQuery") != true ||
            payload.Value<bool?>("returnedByActiveQuery") != true)
        {
            error = "fixtures-not-visible-in-active-inventory-query";
            return false;
        }
        return true;
    }

    private DebugSessionInfo[] SelectTargets(RuntimeTestCommandDto command)
    {
        IEnumerable<DebugSessionInfo> query = LiveRuntimes();
        if (!string.IsNullOrWhiteSpace(command.TargetSessionId))
            query = query.Where(item => string.Equals(item.SessionId, command.TargetSessionId, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(command.TargetRole) && !string.Equals(command.TargetRole, "all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(item => string.Equals(item.Role, command.TargetRole, StringComparison.OrdinalIgnoreCase));
        if (command.TargetPlayerId.HasValue) query = query.Where(item => item.PlayerId == command.TargetPlayerId);
        return query.OrderBy(item => item.Role).ThenBy(item => item.PlayerId).ToArray();
    }

    private RuntimeTestDescriptorDto FindDescriptor(string command) =>
        string.IsNullOrWhiteSpace(command) ? null : GetCapabilities().Tests.FirstOrDefault(test =>
            string.Equals(test.TestId, command, StringComparison.Ordinal));

    private DebugSessionInfo[] LiveRuntimes() => (sessions() ?? Array.Empty<DebugSessionInfo>())
        .Where(item => item != null && !string.IsNullOrWhiteSpace(item.SessionId) &&
            !string.IsNullOrWhiteSpace(item.FirehoseUrl) && !string.Equals(item.Role, "dashboard", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.Role, "standalone", StringComparison.OrdinalIgnoreCase))
        .GroupBy(item => item.SessionId, StringComparer.Ordinal).Select(group => group.First()).ToArray();

    private static RuntimeTestCommandDto CloneForChild(RuntimeTestCommandDto source, string requestId) => new()
    {
        SchemaVersion = source.SchemaVersion, RequestId = requestId, RunId = source.RunId,
        CaseId = source.CaseId, PhaseId = source.PhaseId, StepId = source.StepId,
        Command = source.Command, MutationKind = source.MutationKind,
        TimeoutMilliseconds = source.TimeoutMilliseconds,
        Parameters = new Dictionary<string, string>(source.Parameters ?? new(), StringComparer.OrdinalIgnoreCase)
    };

    private void RefreshParent(CoordinatedRun run)
    {
        RuntimeTestRunDto parent = run.Parent;
        RuntimeTestRunDto[] children = run.Children.Select(item => item.Last).ToArray();
        parent.StartedUtc = children.Where(item => item.StartedUtc.HasValue).Select(item => item.StartedUtc).OrderBy(item => item).FirstOrDefault();
        parent.Processes = run.Children.Select(child => new RuntimeTestProcessRunDto
        {
            SessionId = child.Session.SessionId, Role = child.Session.Role, PlayerId = child.Session.PlayerId,
            RequestId = child.RequestId, PhaseId = child.Last.PhaseId,
            StepId = child.Last.StepId, Command = child.Last.Command,
            Status = child.Last.Status, Error = child.Last.Error,
            Result = new Dictionary<string, object>(child.Last.Result ?? new(), StringComparer.Ordinal)
        }).ToList();
        if (children.Length == 0 || children.Any(item => item.Status == RuntimeTestCommandStatus.Running)) parent.Status = RuntimeTestCommandStatus.Running;
        else if (children.Any(item => item.Status == RuntimeTestCommandStatus.Queued)) parent.Status = RuntimeTestCommandStatus.Queued;
        else if (children.Any(item => item.Status == RuntimeTestCommandStatus.FailedDirty)) parent.Status = RuntimeTestCommandStatus.FailedDirty;
        else if (children.Any(item => item.Status == RuntimeTestCommandStatus.Failed)) parent.Status = RuntimeTestCommandStatus.Failed;
        else if (children.Any(item => item.Status == RuntimeTestCommandStatus.Unsupported)) parent.Status = RuntimeTestCommandStatus.Unsupported;
        else if (children.Any(item => item.Status == RuntimeTestCommandStatus.Cancelled)) parent.Status = RuntimeTestCommandStatus.Cancelled;
        else parent.Status = RuntimeTestCommandStatus.Passed;
        if (Terminal(parent.Status)) parent.CompletedUtc = children.Select(item => item.CompletedUtc).Where(item => item.HasValue).OrderByDescending(item => item).FirstOrDefault() ?? DateTime.UtcNow;
        parent.Error = string.Join("; ", run.Children.Where(item => !string.IsNullOrWhiteSpace(item.Last.Error))
            .Select(item => $"{item.Session.Role}/P{item.Session.PlayerId}: {item.Last.Error}"));
        if (Terminal(parent.Status) && run.CaptureStarted && !run.CaptureStopped)
        {
            run.CaptureStopped = true;
            try { parent.Result["captureStopPath"] = stopCapture?.Invoke() ?? string.Empty; }
            catch (Exception exception)
            {
                parent.Result["captureStopError"] = exception.GetBaseException().Message;
            }
            AttachCaptureFiles(parent);
        }
    }

    private void AttachCaptureFiles(RuntimeTestRunDto run)
    {
        if (captureFiles == null || string.IsNullOrWhiteSpace(run?.RunId)) return;
        try
        {
            string[] paths = captureFiles(run.RunId)?.Where(path =>
                    !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray() ??
                Array.Empty<string>();
            if (paths.Length > 0) run.Result["captureFiles"] = paths;
        }
        catch (Exception exception)
        {
            run.Result["captureFileError"] = exception.GetBaseException().Message;
        }
    }

    private void Publish(RuntimeTestRunDto run)
    {
        if (run == null) return;
        journal?.Upsert(run);
        string fingerprint = DebugJson.Serialize(new
        {
            run.Status, run.PhaseId, run.StepId, run.Error, run.CompletedUtc,
            Processes = run.Processes?.Select(process => new
            {
                process.RequestId, process.Status, process.Error
            }).ToArray()
        });
        if (published.TryGetValue(run.RequestId, out string previous) &&
            string.Equals(previous, fingerprint, StringComparison.Ordinal)) return;
        published[run.RequestId] = fingerprint;
        runUpdated?.Invoke(Clone(run));
    }

    private static string CaptureName(RuntimeTestCommandDto command) =>
        "runtime-test-" + (command?.RunId ?? "run") + "-" +
        (command?.Command ?? "unknown");

    private static RuntimeTestRunDto Clone(RuntimeTestRunDto source) => new()
    {
        RequestId = source.RequestId, RunId = source.RunId, CaseId = source.CaseId,
        PhaseId = source.PhaseId, StepId = source.StepId, Command = source.Command,
        Status = source.Status, QueuedUtc = source.QueuedUtc, StartedUtc = source.StartedUtc,
        CompletedUtc = source.CompletedUtc, Role = source.Role, PlayerId = source.PlayerId,
        Error = source.Error, Result = new Dictionary<string, object>(source.Result ?? new(), StringComparer.Ordinal),
        Processes = source.Processes.Select(item => new RuntimeTestProcessRunDto
        {
            SessionId = item.SessionId, Role = item.Role, PlayerId = item.PlayerId,
            RequestId = item.RequestId, PhaseId = item.PhaseId, StepId = item.StepId,
            Command = item.Command, Status = item.Status, Error = item.Error,
            Result = new Dictionary<string, object>(item.Result ?? new(), StringComparer.Ordinal)
        }).ToList()
    };

    private static RuntimeTestCommandAcceptedDto Rejected(RuntimeTestCommandDto command, string reason) => new()
    {
        RequestId = command?.RequestId ?? string.Empty, RunId = command?.RunId ?? string.Empty,
        Accepted = false, Reason = reason
    };
    private static bool Terminal(RuntimeTestCommandStatus status) => status is RuntimeTestCommandStatus.Passed
        or RuntimeTestCommandStatus.Failed or RuntimeTestCommandStatus.FailedDirty or
        RuntimeTestCommandStatus.Cancelled or RuntimeTestCommandStatus.Unsupported;
}
#endif
