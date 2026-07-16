#if DEBUG
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.DebugClient;

internal sealed class RuntimeTestCoordinator
{
    private sealed class FakeClient : IRuntimeTestProcessClient
    {
        private readonly DebugSessionInfo session;
        public FakeClient(DebugSessionInfo session) { this.session = session; }
        public RuntimeTestCapabilitiesDto GetCapabilities() => new()
        {
            Available = true, MainThreadAgentReady = true, Role = session.Role, PlayerId = session.PlayerId,
            Capabilities = new[] { "runtime-self-check" }, Commands = new[] { "runtime.self-check" },
            Tests = new[] { new RuntimeTestDescriptorDto { TestId = "runtime.self-check", DisplayName = "Runtime self-check", Category = "Harness" } }
        };
        public RuntimeTestCommandAcceptedDto Enqueue(RuntimeTestCommandDto command) => new()
        {
            RequestId = command.RequestId, RunId = command.RunId, Accepted = true,
            StatusUrl = "/api/runtime-tests/runs/" + command.RequestId
        };
        public RuntimeTestRunDto GetRun(string requestId) => new()
        {
            RequestId = requestId, RunId = "coordinator-self-test", Command = "runtime.self-check",
            Status = RuntimeTestCommandStatus.Passed, QueuedUtc = DateTime.UtcNow.AddMilliseconds(-10),
            StartedUtc = DateTime.UtcNow.AddMilliseconds(-5), CompletedUtc = DateTime.UtcNow,
            Role = session.Role, PlayerId = session.PlayerId,
            Result = new Dictionary<string, object> { ["session"] = session.SessionId }
        };
        public void Cancel(string requestId) { }
    }

    private sealed class Child
    {
        public DebugSessionInfo Session;
        public IRuntimeTestProcessClient Client;
        public string RequestId;
        public RuntimeTestRunDto Last;
    }

    private sealed class CoordinatedRun
    {
        public RuntimeTestRunDto Parent;
        public List<Child> Children = new();
        public bool CaptureStarted;
        public bool CaptureStopped;
    }

    private readonly Func<IEnumerable<DebugSessionInfo>> sessions;
    private readonly Func<DebugSessionInfo, IRuntimeTestProcessClient> clientFactory;
    private readonly Func<string, string, string> startCapture;
    private readonly Func<string> stopCapture;
    private readonly ConcurrentDictionary<string, CoordinatedRun> runs = new(StringComparer.Ordinal);

    public RuntimeTestCoordinator(Func<IEnumerable<DebugSessionInfo>> sessions,
        Func<DebugSessionInfo, IRuntimeTestProcessClient> clientFactory = null,
        Func<string, string, string> startCapture = null,
        Func<string> stopCapture = null)
    {
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.clientFactory = clientFactory ?? (session => new RuntimeTestHttpClient(session));
        this.startCapture = startCapture;
        this.stopCapture = stopCapture;
    }

    public static void RunSelfTest()
    {
        DebugSessionInfo host = new() { SessionId = "host", Role = "host", PlayerId = 1, FirehoseUrl = "http://127.0.0.1:1/", ApiToken = "host-token" };
        DebugSessionInfo client = new() { SessionId = "client", Role = "client", PlayerId = 2, FirehoseUrl = "http://127.0.0.1:2/", ApiToken = "client-token" };
        Dictionary<string, FakeClient> clients = new()
        {
            [host.SessionId] = new FakeClient(host), [client.SessionId] = new FakeClient(client)
        };
        RuntimeTestCoordinator coordinator = new(() => new[] { host, client }, session => clients[session.SessionId]);
        RuntimeTestCapabilitiesDto capabilities = coordinator.GetCapabilities();
        if (!capabilities.Available || capabilities.Commands.Length != 1 || capabilities.Tests.Length != 1)
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
        RuntimeTestRunDto scenarioRun = scenarioCoordinator.GetRun("scenario-parent");
        if (!scenarioAccepted.Accepted || scenarioRun?.Status != RuntimeTestCommandStatus.Passed ||
            captureStarts != 1 || captureStops != 1 ||
            !Equals(scenarioRun.Result["captureId"], "scenario-run"))
            throw new InvalidOperationException("Runtime-test coordinator self-test failed scenario capture lifecycle.");
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
        bool scenario = command.Command?.StartsWith("scenario.", StringComparison.Ordinal) == true;
        if (scenario && runs.Values.Any(candidate => !Terminal(candidate.Parent.Status) &&
                candidate.Parent.Command?.StartsWith("scenario.", StringComparison.Ordinal) == true))
            return Rejected(command, "another-scenario-is-running");
        DebugSessionInfo[] targets = SelectTargets(command);
        if (targets.Length == 0) return Rejected(command, "no-matching-runtime-session");
        if (string.IsNullOrWhiteSpace(command.TargetSessionId) && string.IsNullOrWhiteSpace(command.TargetRole) && !command.TargetPlayerId.HasValue && targets.Length > 1)
            return Rejected(command, "target-selector-required");

        CoordinatedRun coordinated = new()
        {
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
            string capturePath = startCapture("runtime-test-" + command.Command, captureId);
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
        return new RuntimeTestCommandAcceptedDto
        {
            RequestId = command.RequestId, RunId = command.RunId, Accepted = true,
            StatusUrl = "/api/runtime-tests/runs/" + Uri.EscapeDataString(command.RequestId)
        };
    }

    public RuntimeTestRunDto GetRun(string requestId)
    {
        if (requestId == null || !runs.TryGetValue(requestId, out CoordinatedRun run)) return null;
        lock (run)
        {
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
            return Clone(run.Parent);
        }
    }

    public bool Cancel(string requestId)
    {
        if (requestId == null || !runs.TryGetValue(requestId, out CoordinatedRun run)) return false;
        lock (run)
        {
            bool requested = false;
            foreach (Child child in run.Children.Where(child => !Terminal(child.Last.Status)))
            {
                try { child.Client.Cancel(child.RequestId); requested = true; } catch { }
            }
            return requested;
        }
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
            RequestId = child.RequestId, Status = child.Last.Status, Error = child.Last.Error,
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
        }
    }

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
            RequestId = item.RequestId, Status = item.Status, Error = item.Error,
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
