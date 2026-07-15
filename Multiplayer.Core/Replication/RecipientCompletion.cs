using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Replication;

public enum ReplicationCompletionStatus
{
    Pending,
    Complete,
    Rejected,
    Discontinuity
}

public sealed class RecipientProgress
{
    public byte PlayerId { get; set; }
    public bool Sent { get; set; }
    public bool Received { get; set; }
    public bool Applied { get; set; }
    public bool AcknowledgedWithoutApply { get; set; }

    public bool Completed => Applied || AcknowledgedWithoutApply;
}

public sealed class RecipientCompletionInput
{
    public DateTime LastUpdatedUtc { get; set; }
    public bool ValidationRejected { get; set; }
    public bool HostApplied { get; set; }
    public bool RelayFinished { get; set; }
    public IReadOnlyList<RecipientProgress> Recipients { get; set; } = Array.Empty<RecipientProgress>();
    public int SentNotReceivedTimeoutMilliseconds { get; set; } = 750;
    public int ReceivedNotAppliedTimeoutMilliseconds { get; set; } = 2000;
}

public sealed class RecipientCompletionResult
{
    public ReplicationCompletionStatus Status { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Dictionary<byte, string> RecipientDiscontinuities { get; set; } = new();
}

public static class RecipientCompletionEvaluator
{
    public static RecipientCompletionResult Evaluate(RecipientCompletionInput input, DateTime now)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.ValidationRejected)
            return new RecipientCompletionResult
            {
                Status = ReplicationCompletionStatus.Rejected,
                Reason = "validation-rejected"
            };

        RecipientCompletionResult result = new() { Status = ReplicationCompletionStatus.Pending };
        double age = Math.Max(0, (now - input.LastUpdatedUtc).TotalMilliseconds);
        foreach (RecipientProgress recipient in input.Recipients ?? Array.Empty<RecipientProgress>())
        {
            string discontinuity = string.Empty;
            if (recipient.Sent && !recipient.Received && age >= input.SentNotReceivedTimeoutMilliseconds)
                discontinuity = "sent-not-received";
            else if (recipient.Received && !recipient.Completed && age >= input.ReceivedNotAppliedTimeoutMilliseconds)
                discontinuity = "received-not-applied";
            if (!string.IsNullOrEmpty(discontinuity))
                result.RecipientDiscontinuities[recipient.PlayerId] = discontinuity;
        }

        if (result.RecipientDiscontinuities.Count > 0)
        {
            KeyValuePair<byte, string> first = result.RecipientDiscontinuities.OrderBy(value => value.Key).First();
            result.Status = ReplicationCompletionStatus.Discontinuity;
            result.Reason = $"P{first.Key}:{first.Value}";
        }
        else if (input.Recipients != null && input.Recipients.Count > 0 &&
                 input.Recipients.All(recipient => recipient.Completed))
        {
            result.Status = ReplicationCompletionStatus.Complete;
        }
        else if ((input.Recipients == null || input.Recipients.Count == 0) &&
                 input.HostApplied && input.RelayFinished)
        {
            result.Status = ReplicationCompletionStatus.Complete;
        }
        return result;
    }
}
