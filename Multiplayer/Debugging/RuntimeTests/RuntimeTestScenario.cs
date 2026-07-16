#if DEBUG
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests;

internal sealed class RuntimeTestScenarioContext
{
    private sealed class Resource
    {
        public string Kind;
        public string Id;
        public string ExpectedFinalState;
        public Func<IEnumerator> Cleanup;
        public bool Restored;
    }

    private readonly RuntimeTestCommandDto command;
    private readonly RuntimeTestRunDto run;
    private readonly List<Dictionary<string, object>> phases = new();
    private readonly List<Dictionary<string, object>> assertions = new();
    private readonly List<Resource> resources = new();
    private readonly List<string> cleanupFailures = new();
    private bool cleanupFinished;

    public bool CleanupClean => cleanupFinished && cleanupFailures.Count == 0 &&
        resources.TrueForAll(resource => resource.Restored);

    public RuntimeTestScenarioContext(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        this.command = command ?? throw new ArgumentNullException(nameof(command));
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        lock (run)
        {
            run.Result["scenario"] = command.Command;
            run.Result["phases"] = phases;
            run.Result["assertions"] = assertions;
            run.Result["resources"] = new List<Dictionary<string, object>>();
            run.Result["cleanupFailures"] = cleanupFailures;
        }
    }

    public void EnterPhase(string phaseId, string stepId)
    {
        phaseId ??= string.Empty;
        stepId ??= phaseId;
        RuntimeTestScope.Update(phaseId, stepId);
        lock (run)
        {
            run.PhaseId = phaseId;
            run.StepId = stepId;
            phases.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["phaseId"] = phaseId,
                ["stepId"] = stepId,
                ["startedUtc"] = DateTime.UtcNow.ToString("O")
            });
        }
        DebugRuntime.Publish("runtime-test", "runtime-test.phase-started",
            DebugRuntimeSide.Shared, correlationId: command.RunId, data: new()
            {
                ["caseId"] = command.CaseId,
                ["phaseId"] = phaseId,
                ["stepId"] = stepId
            });
    }

    public void RegisterResource(string kind, string id, string expectedFinalState,
        Action cleanup)
    {
        RegisterResource(kind, id, expectedFinalState, cleanup == null ? null :
            () => RunSynchronousCleanup(cleanup));
    }

    public void RegisterResource(string kind, string id, string expectedFinalState,
        Func<IEnumerator> cleanup)
    {
        resources.Add(new Resource
        {
            Kind = kind ?? string.Empty,
            Id = id ?? string.Empty,
            ExpectedFinalState = expectedFinalState ?? string.Empty,
            Cleanup = cleanup
        });
        PublishResources();
    }

    public void MarkRestored(string kind, string id)
    {
        Resource resource = resources.Find(candidate =>
            string.Equals(candidate.Kind, kind, StringComparison.Ordinal) &&
            string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (resource != null) resource.Restored = true;
        PublishResources();
    }

    public IEnumerator Eventually(string assertionId, Func<bool> condition,
        float timeoutSeconds, Func<object> actual = null)
    {
        float started = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - started <= timeoutSeconds)
        {
            if (condition())
            {
                RecordAssertion(assertionId, true,
                    Time.realtimeSinceStartup - started, actual?.Invoke());
                yield break;
            }
            yield return null;
        }
        object observed = actual?.Invoke();
        RecordAssertion(assertionId, false, Time.realtimeSinceStartup - started, observed);
        throw new InvalidOperationException("assertion-timeout:" + assertionId +
            (observed == null ? string.Empty : ":actual=" + observed));
    }

    public void Assert(string assertionId, bool passed, object actual = null)
    {
        RecordAssertion(assertionId, passed, 0f, actual);
        if (!passed)
            throw new InvalidOperationException("assertion-failed:" + assertionId +
                (actual == null ? string.Empty : ":actual=" + actual));
    }

    public IEnumerator Cleanup()
    {
        EnterPhase("cleanup", "cleanup-resources");
        if (cleanupFinished) yield break;
        for (int index = resources.Count - 1; index >= 0; index--)
        {
            Resource resource = resources[index];
            if (resource.Restored || resource.Cleanup == null) continue;
            IEnumerator operation = null;
            Exception failure = null;
            try { operation = resource.Cleanup(); }
            catch (Exception exception) { failure = exception.GetBaseException(); }
            while (failure == null && operation != null)
            {
                bool moved = false;
                object current = null;
                try
                {
                    moved = operation.MoveNext();
                    if (moved) current = operation.Current;
                }
                catch (Exception exception)
                {
                    failure = exception.GetBaseException();
                }
                if (failure != null || !moved) break;
                yield return current;
            }
            (operation as IDisposable)?.Dispose();
            if (failure == null)
                resource.Restored = true;
            else
            {
                lock (run)
                    cleanupFailures.Add(resource.Kind + ":" + resource.Id + ":" +
                        failure.Message);
            }
            PublishResources();
        }
        cleanupFinished = true;
        PublishResources();
        lock (run) run.Result["cleanupClean"] = CleanupClean;
        yield return null;
    }

    public void AbortCleanup()
    {
        if (cleanupFinished) return;
        cleanupFinished = true;
        foreach (Resource resource in resources)
            if (!resource.Restored)
            {
                lock (run)
                    cleanupFailures.Add(resource.Kind + ":" + resource.Id +
                        ":cleanup-coroutine-aborted");
            }
        PublishResources();
        lock (run) run.Result["cleanupClean"] = false;
    }

    private static IEnumerator RunSynchronousCleanup(Action cleanup)
    {
        cleanup();
        yield break;
    }

    private void RecordAssertion(string assertionId, bool passed, float elapsed,
        object actual)
    {
        Dictionary<string, object> record = new(StringComparer.Ordinal)
        {
            ["assertionId"] = assertionId ?? string.Empty,
            ["passed"] = passed,
            ["elapsedSeconds"] = elapsed
        };
        if (actual != null) record["actual"] = DebugValueSnapshotter.Snapshot(actual);
        lock (run) assertions.Add(record);
        DebugRuntime.Publish("runtime-test", passed ? "runtime-test.assertion-passed" :
                "runtime-test.assertion-failed", DebugRuntimeSide.Shared,
            passed ? DebugSeverity.Info : DebugSeverity.Error,
            correlationId: command.RunId, data: record);
    }

    private void PublishResources()
    {
        List<Dictionary<string, object>> snapshot = new();
        foreach (Resource resource in resources)
            snapshot.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["kind"] = resource.Kind,
                ["id"] = resource.Id,
                ["expectedFinalState"] = resource.ExpectedFinalState,
                ["restored"] = resource.Restored
            });
        lock (run) run.Result["resources"] = snapshot;
    }
}

internal static class RuntimeTestScenarioRunner
{
    public static IEnumerator Run(RuntimeTestScenarioContext context, IEnumerator body)
    {
        Exception failure = null;
        try
        {
            while (true)
            {
                bool moved;
                object current = null;
                try
                {
                    moved = body.MoveNext();
                    if (moved) current = body.Current;
                }
                catch (Exception exception)
                {
                    failure = exception.GetBaseException();
                    break;
                }
                if (!moved) break;
                yield return current;
            }

            IEnumerator cleanup = context.Cleanup();
            while (cleanup.MoveNext()) yield return cleanup.Current;
        }
        finally
        {
            context.AbortCleanup();
            (body as IDisposable)?.Dispose();
        }

        if (failure != null) throw failure;
        if (!context.CleanupClean)
            throw new InvalidOperationException("scenario-cleanup-incomplete");
    }
}
#endif
