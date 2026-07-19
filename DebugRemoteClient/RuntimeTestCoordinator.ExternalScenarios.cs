#if DEBUG
using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Multiplayer.DebugClient;

internal sealed partial class RuntimeTestCoordinator
{
    private sealed class ExternalExecution
    {
        public ExternalScenarioDefinition Definition;
        public RuntimeTestCommandDto Command;
        public int Index;
        public bool Cleaning;
        public Child Active;
        public string Failure = string.Empty;
        public readonly List<string> CleanupFailures = new();
        public readonly Dictionary<string, string> Variables = new(StringComparer.Ordinal);
    }

    private RuntimeTestCommandAcceptedDto EnqueueExternal(RuntimeTestCommandDto command,
        ExternalScenarioDefinition definition)
    {
        CoordinatedRun run = new()
        {
            IsScenario = true,
            Parent = new RuntimeTestRunDto
            {
                RequestId = command.RequestId, RunId = command.RunId, CaseId = command.CaseId,
                Command = command.Command, Status = RuntimeTestCommandStatus.Running,
                QueuedUtc = DateTime.UtcNow, StartedUtc = DateTime.UtcNow, Role = "dashboard"
            },
            ExternalScenario = new ExternalExecution { Definition = definition, Command = command }
        };
        if (startCapture != null)
        {
            run.CaptureStarted = true;
            run.Parent.Result["captureId"] = command.RunId;
            run.Parent.Result["capturePath"] = startCapture(CaptureName(command), command.RunId) ?? string.Empty;
        }
        runs[command.RequestId] = run;
        lock (scenarioQueueGate) activeScenarioRequestId = command.RequestId;
        StartExternalStep(run);
        Publish(run.Parent);
        return new RuntimeTestCommandAcceptedDto { Accepted = true, RequestId = command.RequestId,
            RunId = command.RunId, StatusUrl = "/api/runtime-tests/runs/" + Uri.EscapeDataString(command.RequestId) };
    }

    private void AdvanceExternal(CoordinatedRun run)
    {
        // A completed external scenario is immutable. GetRun is a polling endpoint and
        // may be called by several browser/dashboard clients; re-entering completion
        // would refresh CompletedUtc and publish another terminal update on every poll.
        if (Terminal(run.Parent.Status)) return;

        ExternalExecution execution = run.ExternalScenario;
        if (execution.Active == null) { StartExternalStep(run); return; }
        try { execution.Active.Last = execution.Active.Client.GetRun(execution.Active.RequestId) ?? execution.Active.Last; }
        catch (Exception exception)
        {
            execution.Active.Last.Status = RuntimeTestCommandStatus.Failed;
            execution.Active.Last.Error = "status-poll-failed:" + exception.GetBaseException().Message;
        }
        UpdateExternalProcesses(run);
        if (!Terminal(execution.Active.Last.Status)) return;

        ExternalScenarioStep step = CurrentExternalStep(execution);
        string failure = execution.Active.Last.Status == RuntimeTestCommandStatus.Passed ?
            ValidateExternalStep(step, execution.Active.Last, execution) : execution.Active.Last.Error;
        if (!string.IsNullOrWhiteSpace(failure))
        {
            if (execution.Cleaning) execution.CleanupFailures.Add(step.Id + ": " + failure);
            else execution.Failure = step.Id + ": " + failure;
        }
        execution.Index++;
        execution.Active = null;
        if (!execution.Cleaning && (!string.IsNullOrEmpty(execution.Failure) ||
            execution.Index >= execution.Definition.Steps.Count))
        {
            execution.Cleaning = true;
            execution.Index = 0;
        }
        StartExternalStep(run);
    }

    private void StartExternalStep(CoordinatedRun run)
    {
        ExternalExecution execution = run.ExternalScenario;
        List<ExternalScenarioStep> steps = execution.Cleaning ? execution.Definition.Cleanup : execution.Definition.Steps;
        if (execution.Index >= steps.Count)
        {
            if (!execution.Cleaning)
            {
                execution.Cleaning = true; execution.Index = 0; StartExternalStep(run); return;
            }
            CompleteExternal(run); return;
        }
        ExternalScenarioStep step = steps[execution.Index];
        if ((step.RequiredVariables ?? Array.Empty<string>()).Any(name =>
                !execution.Variables.TryGetValue(name, out string value) ||
                string.IsNullOrWhiteSpace(value)))
        {
            execution.Index++;
            StartExternalStep(run);
            return;
        }
        RuntimeTestDescriptorDto descriptor = FindDescriptor(step.Command);
        if (descriptor == null || descriptor.IsScenario)
        {
            if (execution.Cleaning) execution.CleanupFailures.Add(step.Id + ": unsupported primitive " + step.Command);
            else execution.Failure = step.Id + ": unsupported primitive " + step.Command;
            execution.Index++; StartExternalStep(run); return;
        }
        DebugSessionInfo target = SelectExternalTarget(execution.Command, step.Target);
        if (target == null)
        {
            if (execution.Cleaning) execution.CleanupFailures.Add(step.Id + ": target unavailable " + step.Target);
            else execution.Failure = step.Id + ": target unavailable " + step.Target;
            execution.Index++; StartExternalStep(run); return;
        }
        string childId = execution.Command.RequestId + "-external-" + (run.Children.Count + 1);
        RuntimeTestCommandDto childCommand = new()
        {
            RequestId = childId, RunId = execution.Command.RunId, CaseId = execution.Command.CaseId,
            PhaseId = execution.Cleaning ? "cleanup" : "scenario", StepId = step.Id,
            Command = step.Command, MutationKind = descriptor.MutationKind,
            TimeoutMilliseconds = Math.Min(execution.Definition.TimeoutMilliseconds, descriptor.TimeoutMilliseconds),
            Parameters = step.Parameters.ToDictionary(pair => pair.Key,
                pair => ExpandExternal(pair.Value, execution), StringComparer.OrdinalIgnoreCase)
        };
        IRuntimeTestProcessClient client = clientFactory(target);
        RuntimeTestCommandAcceptedDto accepted;
        try { accepted = client.Enqueue(childCommand); }
        catch (Exception exception) { accepted = Rejected(childCommand, exception.GetBaseException().Message); }
        Child child = new()
        {
            Session = target, Client = client, RequestId = childId, Stage = step.Id,
            Last = new RuntimeTestRunDto { RequestId = childId, RunId = childCommand.RunId,
                CaseId = childCommand.CaseId, PhaseId = childCommand.PhaseId, StepId = step.Id,
                Command = step.Command, QueuedUtc = DateTime.UtcNow, Role = target.Role, PlayerId = target.PlayerId,
                Status = accepted.Accepted ? RuntimeTestCommandStatus.Queued : RuntimeTestCommandStatus.Failed,
                Error = accepted.Accepted ? string.Empty : accepted.Reason }
        };
        run.Children.Add(child); execution.Active = child; UpdateExternalProcesses(run);
    }

    private string ValidateExternalStep(ExternalScenarioStep step, RuntimeTestRunDto child, ExternalExecution execution)
    {
        JToken root = JToken.FromObject(child.Result ?? new Dictionary<string, object>());
        foreach (KeyValuePair<string, string> capture in step.Capture)
        {
            JToken token = root.SelectToken(capture.Value, false);
            if (token == null) return "capture path missing: " + capture.Value;
            execution.Variables[capture.Key] = token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Newtonsoft.Json.Formatting.None);
        }
        foreach (ExternalScenarioAssertion assertion in step.Assertions)
        {
            JToken token = root.SelectToken(assertion.Path, false);
            bool valid = assertion.Exists.HasValue ? assertion.Exists.Value == (token != null) :
                token != null && assertion.Expected != null && JToken.DeepEquals(token, assertion.Expected);
            if (!valid) return string.IsNullOrWhiteSpace(assertion.Message) ?
                "assertion failed: " + assertion.Path : assertion.Message;
        }
        return string.Empty;
    }

    private DebugSessionInfo SelectExternalTarget(RuntimeTestCommandDto command, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector == "selected") return SelectTargets(command).FirstOrDefault();
        return LiveRuntimes().FirstOrDefault(item => string.Equals(item.Role, selector, StringComparison.OrdinalIgnoreCase));
    }

    private static ExternalScenarioStep CurrentExternalStep(ExternalExecution execution) =>
        (execution.Cleaning ? execution.Definition.Cleanup : execution.Definition.Steps)[execution.Index];

    private static string ExpandExternal(string value, ExternalExecution execution) =>
        Regex.Replace(value ?? string.Empty, @"\$\{(?<kind>input|var|run)\.(?<name>[^}]+)\}", match =>
        {
            string name = match.Groups["name"].Value;
            if (match.Groups["kind"].Value == "run")
                return string.Equals(name, "id", StringComparison.OrdinalIgnoreCase)
                    ? execution.Command.RunId ?? string.Empty : string.Empty;
            if (match.Groups["kind"].Value == "var") return execution.Variables.TryGetValue(name, out string captured) ? captured : string.Empty;
            return execution.Command.Parameters.TryGetValue(name, out string input) ? input : string.Empty;
        });

    private void CompleteExternal(CoordinatedRun run)
    {
        ExternalExecution execution = run.ExternalScenario;
        run.Parent.Error = execution.Failure;
        run.Parent.Result["externalScenario"] = true;
        run.Parent.Result["cleanupClean"] = execution.CleanupFailures.Count == 0;
        run.Parent.Result["cleanupFailures"] = execution.CleanupFailures.ToArray();
        run.Parent.Result["variables"] = new Dictionary<string, string>(execution.Variables);
        run.Parent.Status = execution.CleanupFailures.Count > 0 ? RuntimeTestCommandStatus.FailedDirty :
            string.IsNullOrEmpty(execution.Failure) ? RuntimeTestCommandStatus.Passed : RuntimeTestCommandStatus.Failed;
        run.Parent.CompletedUtc = DateTime.UtcNow;
        if (run.CaptureStarted && !run.CaptureStopped) { run.CaptureStopped = true; try { run.Parent.Result["captureStopPath"] = stopCapture?.Invoke() ?? string.Empty; } catch { } AttachCaptureFiles(run.Parent); }
        Publish(run.Parent);
    }

    private static void UpdateExternalProcesses(CoordinatedRun run) => run.Parent.Processes = run.Children.Select(child => new RuntimeTestProcessRunDto
    {
        SessionId = child.Session.SessionId, Role = child.Session.Role, PlayerId = child.Session.PlayerId,
        RequestId = child.RequestId, PhaseId = child.Last.PhaseId, StepId = child.Last.StepId,
        Command = child.Last.Command, Status = child.Last.Status, Error = child.Last.Error,
        Result = new Dictionary<string, object>(child.Last.Result ?? new(), StringComparer.Ordinal)
    }).ToList();
}
#endif
