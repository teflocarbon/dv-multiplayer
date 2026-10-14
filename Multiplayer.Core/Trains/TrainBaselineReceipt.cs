using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Trains;

public enum TrainBaselineDisposition
{
    Apply,
    Duplicate,
    Reject
}

/// <summary>
/// Pure transaction state for an initial rolling-stock baseline. Unity object creation happens
/// only after this manifest has admitted a numbered trainset.
/// </summary>
public sealed class TrainBaselineReceipt
{
    private readonly HashSet<uint> sequences = new();

    public uint BaselineId { get; private set; }
    public uint ExpectedTrainsets { get; private set; }
    public uint ExpectedCars { get; private set; }
    public uint AppliedTrainsets { get; private set; }
    public uint AppliedCars { get; private set; }
    public bool Started { get; private set; }
    public bool Complete { get; private set; }
    public string Failure { get; private set; } = string.Empty;

    public void Reset()
    {
        BaselineId = 0;
        ExpectedTrainsets = 0;
        ExpectedCars = 0;
        AppliedTrainsets = 0;
        AppliedCars = 0;
        Started = false;
        Complete = false;
        Failure = string.Empty;
        sequences.Clear();
    }

    public bool Begin(uint baselineId, uint trainsetCount, uint carCount, string hostFailure)
    {
        Reset();
        BaselineId = baselineId;
        ExpectedTrainsets = trainsetCount;
        ExpectedCars = carCount;
        Started = true;

        if (baselineId == 0)
            return Reject("server sent an invalid manifest identity");
        if (!string.IsNullOrEmpty(hostFailure))
            return Reject("host could not construct the manifest: " + hostFailure);
        if ((trainsetCount == 0) != (carCount == 0))
            return Reject("manifest has inconsistent empty counts");
        return true;
    }

    public TrainBaselineDisposition Admit(uint baselineId, uint sequence, out string failure)
    {
        failure = string.Empty;
        if (!Started || baselineId != BaselineId)
        {
            failure = $"unexpected baseline {baselineId}";
            Reject(failure);
            return TrainBaselineDisposition.Reject;
        }
        if (!string.IsNullOrEmpty(Failure))
        {
            failure = Failure;
            return TrainBaselineDisposition.Reject;
        }
        if (sequence >= ExpectedTrainsets)
        {
            failure = $"sequence {sequence} exceeds manifest {ExpectedTrainsets}";
            Reject(failure);
            return TrainBaselineDisposition.Reject;
        }
        if (sequences.Contains(sequence))
            return TrainBaselineDisposition.Duplicate;
        return TrainBaselineDisposition.Apply;
    }

    public bool Commit(uint sequence, uint carCount)
    {
        if (Complete || !string.IsNullOrEmpty(Failure) || carCount == 0 ||
            sequence >= ExpectedTrainsets || !sequences.Add(sequence))
            return false;
        AppliedTrainsets++;
        AppliedCars += carCount;
        if (AppliedCars > ExpectedCars)
            return Reject($"applied car count {AppliedCars} exceeds manifest {ExpectedCars}");
        return true;
    }

    public bool TryComplete(uint baselineId, uint trainsetCount, uint carCount)
    {
        if (!Started || baselineId != BaselineId)
            return Reject($"unexpected completion for baseline {baselineId}");
        if (trainsetCount != ExpectedTrainsets || carCount != ExpectedCars)
            return Reject("completion manifest differs from begin manifest");
        if (AppliedTrainsets != ExpectedTrainsets || sequences.Count != ExpectedTrainsets ||
            AppliedCars != ExpectedCars)
            return Reject($"incomplete baseline: {AppliedTrainsets}/{ExpectedTrainsets} trainsets, {AppliedCars}/{ExpectedCars} cars");
        Complete = true;
        return true;
    }

    public bool Reject(string reason)
    {
        if (string.IsNullOrEmpty(Failure))
            Failure = string.IsNullOrWhiteSpace(reason) ? "unspecified baseline failure" : reason;
        Complete = false;
        return false;
    }
}
