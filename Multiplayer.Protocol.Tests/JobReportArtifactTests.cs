using DV.Logic.Job;
using DV.ServicePenalty;
using DV.ThingTypes;
using Multiplayer.Networking.Data.Jobs;
using Multiplayer.Networking.Data.Items;
using NUnit.Framework;
using System;
using System.IO;
using UnityEngine;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class JobReportArtifactTests
{
    [Test]
    public void RenderRecipeRoundTripsWithoutRuntimeObjects()
    {
        JobReportArtifactData source = Fixture(includeDebt: true);

        JobReportArtifactData result = JobReportArtifactData.Deserialize(source.Serialize());

        Assert.Multiple(() =>
        {
            Assert.That(result.SchemaVersion, Is.EqualTo(JobReportArtifactData.CurrentSchemaVersion));
            Assert.That(result.ArtifactToken, Is.EqualTo("jobreport-767-fixture"));
            Assert.That(result.ItemNetId, Is.EqualTo(767));
            Assert.That(result.JobNetId, Is.EqualTo(19));
            Assert.That(result.ValidationStationNetId, Is.EqualTo(42));
            Assert.That(result.Job.Id, Is.EqualTo("SM-SU-02"));
            Assert.That(result.Job.CompletionTime, Is.EqualTo(123.5f));
            Assert.That(result.Job.Tasks, Has.Length.EqualTo(1));
            Assert.That(result.Job.Tasks[0].Cars, Has.Length.EqualTo(1));
            Assert.That(result.Job.Tasks[0].Cars[0].Id, Is.EqualTo("CAR-01"));
            Assert.That(result.Job.Tasks[0].NestedTasks, Has.Length.EqualTo(1));
            Assert.That(result.Debt, Is.Not.Null);
            Assert.That(result.Debt.Id, Is.EqualTo("debt-SM-SU-02"));
            Assert.That(result.Debt.CountsInFeeTolerance, Is.True);
            Assert.That(result.Debt.CarDebtJson, Is.Empty);
        });
        AssertVector(result.Position.Position, new Vector3(7923.04f, 133.34f, 7336.67f));
    }

    [Test]
    public void NoDebtRemainsDistinctFromAnEmptyDebt()
    {
        JobReportArtifactData result = JobReportArtifactData.Deserialize(Fixture(includeDebt: false).Serialize());
        Assert.That(result.Debt, Is.Null);
    }

    [Test]
    public void UnsupportedSchemaIsRejected()
    {
        byte[] compressed = Fixture(includeDebt: false).Serialize();
        byte[] raw = PacketCompression.Decompress(compressed);
        raw[0] = 99;
        byte[] invalid = PacketCompression.Compress(raw);

        Assert.Throws<InvalidDataException>(() => JobReportArtifactData.Deserialize(invalid));
    }

    private static JobReportArtifactData Fixture(bool includeDebt) => new()
    {
        ArtifactToken = "jobreport-767-fixture",
        ItemNetId = 767,
        JobNetId = 19,
        ValidationStationNetId = 42,
        Position = new ItemPositionData
        {
            Position = new Vector3(7923.04f, 133.34f, 7336.67f),
            Rotation = new Quaternion(.27f, .33f, -.10f, .89f)
        },
        Job = new JobReportArtifactData.JobReportJobData
        {
            Id = "SM-SU-02",
            Type = (JobType)1,
            State = JobState.Completed,
            CompletionTime = 123.5f,
            TimeOnJob = 120f,
            TimeLimit = 300f,
            BasePayment = 1000f,
            BonusPayment = 250f,
            TotalPayment = 1250f,
            RequiredLicenses = default,
            Tasks = new[]
            {
                new JobReportArtifactData.JobReportTaskData
                {
                    Type = (TaskType)1,
                    InstanceType = (TaskType)1,
                    State = (TaskState)1,
                    StartTime = 1f,
                    FinishTime = 2f,
                    StartTrack = "A1",
                    DestinationTrack = "B2",
                    Cars = new[]
                    {
                        new JobReportArtifactData.JobReportCarData
                        {
                            Id = "CAR-01", LiveryId = "Boxcar", OnDestinationTrack = true,
                            Length = 12f, CarOnlyMass = 20f, Capacity = 30f
                        }
                    },
                    CargoTypeIds = Array.Empty<uint>(),
                    NestedTasks = new[]
                    {
                        new JobReportArtifactData.JobReportTaskData
                        {
                            Type = (TaskType)2, InstanceType = (TaskType)2,
                            Cars = Array.Empty<JobReportArtifactData.JobReportCarData>(),
                            CargoTypeIds = Array.Empty<uint>(),
                            NestedTasks = Array.Empty<JobReportArtifactData.JobReportTaskData>()
                        }
                    }
                }
            }
        },
        Debt = includeDebt ? new JobReportArtifactData.JobReportDebtData
        {
            Id = "debt-SM-SU-02",
            Type = (DebtType)1,
            DamageableTotal = 10f,
            Sum1 = 11f,
            Sum2 = 12f,
            EnvironmentTotal = 13f,
            Total = 46f,
            Taxable = true,
            Staged = true,
            CountsInFeeTolerance = true,
            CarDebtJson = Array.Empty<string>()
        } : null
    };

    private static void AssertVector(Vector3 actual, Vector3 expected) => Assert.Multiple(() =>
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(.001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(.001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(.001f));
    });
}
