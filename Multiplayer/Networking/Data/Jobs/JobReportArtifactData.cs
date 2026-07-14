using DV.Booklets;
using DV.Common;
using DV.Logic.Job;
using DV.ServicePenalty;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using Multiplayer.Components;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Data.Items;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Multiplayer.Networking.Data.Jobs;

/// <summary>
/// Detached, versioned recipe for one runtime-rendered JobReport. This deliberately mirrors the
/// base game's booklet render DTOs instead of reflecting over Job, Task, or Unity objects.
/// </summary>
public sealed class JobReportArtifactData
{
    public const byte CurrentSchemaVersion = 1;
    private const int MaxTasks = 64;
    private const int MaxTaskDepth = 8;
    private const int MaxCarsPerTask = 128;
    private const int MaxCargoPerTask = 128;
    private const int MaxDebtCars = 128;
    private const int MaxDebtJsonLength = 64 * 1024;

    public byte SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string ArtifactToken { get; set; }
    public ushort ItemNetId { get; set; }
    public ushort JobNetId { get; set; }
    public uint ValidationStationNetId { get; set; }
    public ItemPositionData Position { get; set; }
    public JobReportJobData Job { get; set; }
    public JobReportDebtData Debt { get; set; }

    public static JobReportArtifactData Capture(ushort itemNetId, ushort jobNetId,
        uint validationStationNetId, ItemPositionData position, Job job, DisplayableDebt debt)
    {
        if (itemNetId == 0)
            throw new ArgumentOutOfRangeException(nameof(itemNetId));
        if (job == null)
            throw new ArgumentNullException(nameof(job));

        Job_data jobData = new(job);
        return new JobReportArtifactData
        {
            ArtifactToken = $"jobreport-{itemNetId}-{Guid.NewGuid():N}",
            ItemNetId = itemNetId,
            JobNetId = jobNetId,
            ValidationStationNetId = validationStationNetId,
            Position = position,
            Job = JobReportJobData.Capture(jobData),
            Debt = debt == null ? null : JobReportDebtData.Capture(new Debt_data(debt))
        };
    }

    public byte[] Serialize()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(SchemaVersion);
        WriteBoundedString(writer, ArtifactToken);
        writer.Write(ItemNetId);
        writer.Write(JobNetId);
        writer.Write(ValidationStationNetId);
        writer.Write(Position.Position.x);
        writer.Write(Position.Position.y);
        writer.Write(Position.Position.z);
        writer.Write(Position.Rotation.x);
        writer.Write(Position.Rotation.y);
        writer.Write(Position.Rotation.z);
        writer.Write(Position.Rotation.w);
        Job.Write(writer, 0);
        writer.Write(Debt != null);
        Debt?.Write(writer);
        return PacketCompression.Compress(stream.ToArray());
    }

    public static JobReportArtifactData Deserialize(byte[] payload)
    {
        if (payload == null || payload.Length == 0)
            throw new InvalidDataException("Job report artifact payload is empty.");

        byte[] data = PacketCompression.Decompress(payload);
        using MemoryStream stream = new(data, false);
        using BinaryReader reader = new(stream);
        byte version = reader.ReadByte();
        if (version != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported JobReport artifact schema {version}.");

        JobReportArtifactData artifact = new()
        {
            SchemaVersion = version,
            ArtifactToken = ReadBoundedString(reader),
            ItemNetId = reader.ReadUInt16(),
            JobNetId = reader.ReadUInt16(),
            ValidationStationNetId = reader.ReadUInt32(),
            Position = new ItemPositionData
            {
                Position = new UnityEngine.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                Rotation = new UnityEngine.Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
            },
            Job = JobReportJobData.Read(reader, 0),
            Debt = reader.ReadBoolean() ? JobReportDebtData.Read(reader) : null
        };
        if (artifact.ItemNetId == 0 || artifact.Job == null)
            throw new InvalidDataException("Job report artifact is missing its authoritative identity or render data.");
        return artifact;
    }

    internal static void WriteBoundedString(BinaryWriter writer, string value)
    {
        value ??= string.Empty;
        if (value.Length > 256)
            value = value.Substring(0, 256);
        writer.Write(value);
    }

    internal static string ReadBoundedString(BinaryReader reader)
    {
        string value = reader.ReadString();
        if (value.Length > 256)
            throw new InvalidDataException("Job report artifact string exceeds its limit.");
        return value;
    }

    internal static int ReadCount(BinaryReader reader, int maximum, string name)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException($"Job report artifact {name} count {count} exceeds {maximum}.");
        return count;
    }

    public sealed class JobReportJobData
    {
        public string Id;
        public JobType Type;
        public JobState State;
        public float CompletionTime;
        public float TimeOnJob;
        public float TimeLimit;
        public float BasePayment;
        public float BonusPayment;
        public float TotalPayment;
        public JobLicenses RequiredLicenses;
        public JobReportTaskData[] Tasks;

        public static JobReportJobData Capture(Job_data value) => new()
        {
            Id = value.ID,
            Type = value.type,
            State = value.state,
            CompletionTime = value.completionTime,
            TimeOnJob = value.timeOnJob,
            TimeLimit = value.timeLimit,
            BasePayment = value.basePayment,
            BonusPayment = value.bonusPayment,
            TotalPayment = value.totalPayment,
            RequiredLicenses = value.requiredLicenses,
            Tasks = (value.tasksData ?? Array.Empty<Task_data>()).Take(MaxTasks).Select(JobReportTaskData.Capture).ToArray()
        };

        public Job_data ToGameData() => new(Id, Type, State, CompletionTime, TimeOnJob, TimeLimit,
            BasePayment, BonusPayment, TotalPayment, RequiredLicenses,
            (Tasks ?? Array.Empty<JobReportTaskData>()).Select(task => task.ToGameData()).ToArray(), null, null);

        internal void Write(BinaryWriter writer, int depth)
        {
            WriteBoundedString(writer, Id);
            writer.Write((int)Type);
            writer.Write((int)State);
            writer.Write(CompletionTime);
            writer.Write(TimeOnJob);
            writer.Write(TimeLimit);
            writer.Write(BasePayment);
            writer.Write(BonusPayment);
            writer.Write(TotalPayment);
            writer.Write((int)RequiredLicenses);
            JobReportTaskData[] tasks = Tasks ?? Array.Empty<JobReportTaskData>();
            writer.Write(tasks.Length);
            foreach (JobReportTaskData task in tasks)
                task.Write(writer, depth + 1);
        }

        internal static JobReportJobData Read(BinaryReader reader, int depth)
        {
            int count;
            JobReportJobData value = new()
            {
                Id = ReadBoundedString(reader),
                Type = (JobType)reader.ReadInt32(),
                State = (JobState)reader.ReadInt32(),
                CompletionTime = reader.ReadSingle(),
                TimeOnJob = reader.ReadSingle(),
                TimeLimit = reader.ReadSingle(),
                BasePayment = reader.ReadSingle(),
                BonusPayment = reader.ReadSingle(),
                TotalPayment = reader.ReadSingle(),
                RequiredLicenses = (JobLicenses)reader.ReadInt32()
            };
            count = ReadCount(reader, MaxTasks, "task");
            value.Tasks = new JobReportTaskData[count];
            for (int i = 0; i < count; i++)
                value.Tasks[i] = JobReportTaskData.Read(reader, depth + 1);
            return value;
        }
    }

    public sealed class JobReportTaskData
    {
        public TaskType Type;
        public TaskType InstanceType;
        public TaskState State;
        public float StartTime;
        public float FinishTime;
        public JobReportCarData[] Cars;
        public string StartTrack;
        public string DestinationTrack;
        public WarehouseTaskType WarehouseType;
        public uint[] CargoTypeIds;
        public float TotalCargoAmount;
        public bool CouplingRequired;
        public bool HandbrakeRequired;
        public JobReportTaskData[] NestedTasks;

        public static JobReportTaskData Capture(Task_data value)
        {
            uint[] cargo = (value.cargoTypePerCar ?? new List<CargoType>()).Take(MaxCargoPerTask).Select(type =>
            {
                CargoTypeLookup.Instance.TryGetNetId(type.ToV2(), out uint id);
                return id;
            }).ToArray();
            return new JobReportTaskData
            {
                Type = value.type,
                InstanceType = value.instanceTaskType,
                State = value.state,
                StartTime = value.taskStartTime,
                FinishTime = value.taskFinishTime,
                Cars = (value.cars ?? new List<Car_data>()).Take(MaxCarsPerTask).Select(JobReportCarData.Capture).ToArray(),
                StartTrack = value.startTrackID?.RailTrackGameObjectID,
                DestinationTrack = value.destinationTrackID?.RailTrackGameObjectID,
                WarehouseType = value.warehouseTaskType,
                CargoTypeIds = cargo,
                TotalCargoAmount = value.totalCargoAmount,
                CouplingRequired = value.couplingRequiredAndNotDone,
                HandbrakeRequired = value.anyHandbrakeRequiredAndNotDone,
                NestedTasks = (value.nestedTasks ?? Array.Empty<Task_data>()).Take(MaxTasks).Select(Capture).ToArray()
            };
        }

        public Task_data ToGameData()
        {
            List<CargoType> cargo = new();
            foreach (uint id in CargoTypeIds ?? Array.Empty<uint>())
                if (CargoTypeLookup.Instance.TryGet(id, out CargoType type))
                    cargo.Add(type);
            return new Task_data(Type, InstanceType, State, StartTime, FinishTime,
                (Cars ?? Array.Empty<JobReportCarData>()).Select(car => car.ToGameData()).Where(car => car != null).ToList(),
                ResolveTrack(StartTrack), ResolveTrack(DestinationTrack), WarehouseType, cargo,
                TotalCargoAmount, CouplingRequired, HandbrakeRequired,
                (NestedTasks ?? Array.Empty<JobReportTaskData>()).Select(task => task.ToGameData()).ToArray());
        }

        private static TrackID ResolveTrack(string id)
        {
            if (string.IsNullOrEmpty(id) || RailTrackRegistry.Instance == null)
                return null;
            return RailTrackRegistry.Instance.GetTrackWithName(id)?.LogicTrack()?.ID;
        }

        internal void Write(BinaryWriter writer, int depth)
        {
            if (depth > MaxTaskDepth)
                throw new InvalidDataException("Job report task nesting exceeds its limit.");
            writer.Write((int)Type); writer.Write((int)InstanceType); writer.Write((int)State);
            writer.Write(StartTime); writer.Write(FinishTime);
            JobReportCarData[] cars = Cars ?? Array.Empty<JobReportCarData>();
            writer.Write(cars.Length); foreach (JobReportCarData car in cars) car.Write(writer);
            WriteBoundedString(writer, StartTrack); WriteBoundedString(writer, DestinationTrack);
            writer.Write((int)WarehouseType);
            uint[] cargo = CargoTypeIds ?? Array.Empty<uint>();
            writer.Write(cargo.Length); foreach (uint id in cargo) writer.Write(id);
            writer.Write(TotalCargoAmount); writer.Write(CouplingRequired); writer.Write(HandbrakeRequired);
            JobReportTaskData[] nested = NestedTasks ?? Array.Empty<JobReportTaskData>();
            writer.Write(nested.Length); foreach (JobReportTaskData task in nested) task.Write(writer, depth + 1);
        }

        internal static JobReportTaskData Read(BinaryReader reader, int depth)
        {
            if (depth > MaxTaskDepth)
                throw new InvalidDataException("Job report task nesting exceeds its limit.");
            JobReportTaskData value = new()
            {
                Type = (TaskType)reader.ReadInt32(), InstanceType = (TaskType)reader.ReadInt32(), State = (TaskState)reader.ReadInt32(),
                StartTime = reader.ReadSingle(), FinishTime = reader.ReadSingle()
            };
            int carCount = ReadCount(reader, MaxCarsPerTask, "task car");
            value.Cars = new JobReportCarData[carCount]; for (int i = 0; i < carCount; i++) value.Cars[i] = JobReportCarData.Read(reader);
            value.StartTrack = ReadBoundedString(reader); value.DestinationTrack = ReadBoundedString(reader);
            value.WarehouseType = (WarehouseTaskType)reader.ReadInt32();
            int cargoCount = ReadCount(reader, MaxCargoPerTask, "task cargo");
            value.CargoTypeIds = new uint[cargoCount]; for (int i = 0; i < cargoCount; i++) value.CargoTypeIds[i] = reader.ReadUInt32();
            value.TotalCargoAmount = reader.ReadSingle(); value.CouplingRequired = reader.ReadBoolean(); value.HandbrakeRequired = reader.ReadBoolean();
            int nestedCount = ReadCount(reader, MaxTasks, "nested task");
            value.NestedTasks = new JobReportTaskData[nestedCount]; for (int i = 0; i < nestedCount; i++) value.NestedTasks[i] = Read(reader, depth + 1);
            return value;
        }
    }

    public sealed class JobReportCarData
    {
        public string Id; public string LiveryId; public bool Derailed; public bool OnDestinationTrack;
        public float Length; public float CarOnlyMass; public float Capacity;
        public static JobReportCarData Capture(Car_data value) => new()
        {
            Id = value.ID, LiveryId = value.type?.id, Derailed = value.derailed,
            OnDestinationTrack = value.isOnDestinationTrack, Length = value.length,
            CarOnlyMass = value.carOnlyMass, Capacity = value.capacity
        };
        public Car_data ToGameData()
        {
            if (!TrainComponentLookup.Instance.LiveryFromId(LiveryId, out TrainCarLivery livery))
                return null;
            return new Car_data(Id, livery, Derailed, OnDestinationTrack, Length, CarOnlyMass, Capacity);
        }
        internal void Write(BinaryWriter writer) { WriteBoundedString(writer, Id); WriteBoundedString(writer, LiveryId); writer.Write(Derailed); writer.Write(OnDestinationTrack); writer.Write(Length); writer.Write(CarOnlyMass); writer.Write(Capacity); }
        internal static JobReportCarData Read(BinaryReader reader) => new() { Id = ReadBoundedString(reader), LiveryId = ReadBoundedString(reader), Derailed = reader.ReadBoolean(), OnDestinationTrack = reader.ReadBoolean(), Length = reader.ReadSingle(), CarOnlyMass = reader.ReadSingle(), Capacity = reader.ReadSingle() };
    }

    public sealed class JobReportDebtData
    {
        public string Id; public DebtType Type; public float DamageableTotal; public float Sum1; public float Sum2;
        public float EnvironmentTotal; public float Total; public bool Taxable; public bool Staged;
        public bool CountsInFeeTolerance; public string[] CarDebtJson;
        public static JobReportDebtData Capture(Debt_data value) => new()
        {
            Id = value.ID, Type = value.debtType, DamageableTotal = value.totalPriceOfDamageableResources,
            Sum1 = value.sumOfDebts1, Sum2 = value.sumOfDebts2, EnvironmentTotal = value.environmentDamageTotalPrice,
            Total = value.totalPrice, Taxable = value.isTaxable, Staged = value.isStaged,
            CountsInFeeTolerance = value.countsInFeeTolerance,
            CarDebtJson = (value.debtData ?? Array.Empty<CarDebtData>()).Take(MaxDebtCars)
                .Select(car => car.GetCarDebtSaveData().ToString(Formatting.None)).ToArray()
        };
        public Debt_data ToGameData() => new(Id, Type, DamageableTotal, Sum1, Sum2, EnvironmentTotal, Total,
            Taxable, Staged, (CarDebtJson ?? Array.Empty<string>()).Select(json => CarDebtData.LoadCarDebtFromSaveData(Newtonsoft.Json.Linq.JObject.Parse(json))).ToArray(), CountsInFeeTolerance);
        internal void Write(BinaryWriter writer)
        {
            WriteBoundedString(writer, Id); writer.Write((int)Type); writer.Write(DamageableTotal); writer.Write(Sum1); writer.Write(Sum2);
            writer.Write(EnvironmentTotal); writer.Write(Total); writer.Write(Taxable); writer.Write(Staged); writer.Write(CountsInFeeTolerance);
            string[] entries = CarDebtJson ?? Array.Empty<string>(); writer.Write(entries.Length);
            foreach (string json in entries) { if ((json?.Length ?? 0) > MaxDebtJsonLength) throw new InvalidDataException("Job report debt entry exceeds its limit."); writer.Write(json ?? string.Empty); }
        }
        internal static JobReportDebtData Read(BinaryReader reader)
        {
            JobReportDebtData value = new() { Id = ReadBoundedString(reader), Type = (DebtType)reader.ReadInt32(), DamageableTotal = reader.ReadSingle(), Sum1 = reader.ReadSingle(), Sum2 = reader.ReadSingle(), EnvironmentTotal = reader.ReadSingle(), Total = reader.ReadSingle(), Taxable = reader.ReadBoolean(), Staged = reader.ReadBoolean(), CountsInFeeTolerance = reader.ReadBoolean() };
            int count = ReadCount(reader, MaxDebtCars, "debt car"); value.CarDebtJson = new string[count];
            for (int i = 0; i < count; i++) { value.CarDebtJson[i] = reader.ReadString(); if (value.CarDebtJson[i].Length > MaxDebtJsonLength) throw new InvalidDataException("Job report debt entry exceeds its limit."); }
            return value;
        }
    }
}
