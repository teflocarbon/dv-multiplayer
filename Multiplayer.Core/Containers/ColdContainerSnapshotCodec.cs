using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Core.Containers;

/// <summary>Bounded, versioned host-save codec. Runtime session handles are deliberately omitted.</summary>
public static class ColdContainerSnapshotCodec
{
    private const uint Magic = 0x43435644; // DVCC
    private const ushort FormatVersion = 2;
    public const int MaximumEncodedBytes = 16 * 1024 * 1024;
    public const int MaximumDetachedStateBytes = 512 * 1024;
    private const int MaximumStringBytes = 4096;

    public static byte[] Encode(ColdContainerGraphSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            WriteCount(writer, snapshot.Containers?.Count ?? 0);
            foreach (ColdContainerRecord container in snapshot.Containers ?? new List<ColdContainerRecord>())
            {
                WriteGuid(writer, container.PersistentContainerId);
                WriteString(writer, container.OwnerIdentity);
                WriteString(writer, container.PrefabName);
                writer.Write(container.Capacity);
                writer.Write(container.Revision);
                writer.Write(container.ParentItemId.HasValue);
                if (container.ParentItemId.HasValue) WriteGuid(writer, container.ParentItemId.Value);
            }
            WriteCount(writer, snapshot.Items?.Count ?? 0);
            foreach (ColdStoredItemRecord item in snapshot.Items ?? new List<ColdStoredItemRecord>())
            {
                WriteGuid(writer, item.PersistentItemId);
                WriteString(writer, item.PrefabName);
                WriteString(writer, item.DisplayName);
                WriteString(writer, item.PersistentOwnerIdentity);
                WriteGuid(writer, item.ParentContainerId);
                writer.Write(item.Slot);
                writer.Write(item.ChildContainerId.HasValue);
                if (item.ChildContainerId.HasValue) WriteGuid(writer, item.ChildContainerId.Value);
                writer.Write(item.StateVersion);
                byte[] state = item.DetachedState ?? Array.Empty<byte>();
                if (state.Length > MaximumDetachedStateBytes)
                    throw new InvalidDataException("Detached item state exceeds the configured limit.");
                writer.Write(state.Length);
                writer.Write(state);
            }
        }
        if (stream.Length > MaximumEncodedBytes)
            throw new InvalidDataException("Cold-container snapshot exceeds the configured limit.");
        return stream.ToArray();
    }

    public static bool TryDecode(byte[] data, out ColdContainerGraphSnapshot snapshot,
        out string rejectionReason)
    {
        snapshot = null;
        rejectionReason = string.Empty;
        if (data == null || data.Length == 0 || data.Length > MaximumEncodedBytes)
        {
            rejectionReason = "snapshot-size-invalid";
            return false;
        }
        try
        {
            using MemoryStream stream = new(data, false);
            using BinaryReader reader = new(stream, Encoding.UTF8, false);
            if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != FormatVersion)
                throw new InvalidDataException("snapshot-header-invalid");
            int containerCount = ReadCount(reader);
            List<ColdContainerRecord> containers = new(containerCount);
            for (int i = 0; i < containerCount; i++)
            {
                Guid id = ReadGuid(reader);
                string owner = ReadString(reader);
                string prefabName = ReadString(reader);
                int capacity = reader.ReadInt32();
                uint revision = reader.ReadUInt32();
                Guid? parent = reader.ReadBoolean() ? ReadGuid(reader) : null;
                containers.Add(new ColdContainerRecord
                {
                    PersistentContainerId = id,
                    OwnerIdentity = owner,
                    PrefabName = prefabName,
                    Capacity = capacity,
                    Revision = revision,
                    ParentItemId = parent
                });
            }
            int itemCount = ReadCount(reader);
            List<ColdStoredItemRecord> items = new(itemCount);
            for (int i = 0; i < itemCount; i++)
            {
                Guid id = ReadGuid(reader);
                string prefab = ReadString(reader);
                string display = ReadString(reader);
                string owner = ReadString(reader);
                Guid parent = ReadGuid(reader);
                int slot = reader.ReadInt32();
                Guid? child = reader.ReadBoolean() ? ReadGuid(reader) : null;
                uint stateVersion = reader.ReadUInt32();
                int stateLength = reader.ReadInt32();
                if (stateLength < 0 || stateLength > MaximumDetachedStateBytes ||
                    stateLength > stream.Length - stream.Position)
                    throw new InvalidDataException("snapshot-state-size-invalid");
                byte[] state = reader.ReadBytes(stateLength);
                items.Add(new ColdStoredItemRecord
                {
                    PersistentItemId = id,
                    PrefabName = prefab,
                    DisplayName = display,
                    PersistentOwnerIdentity = owner,
                    ParentContainerId = parent,
                    Slot = slot,
                    ChildContainerId = child,
                    StateVersion = stateVersion,
                    DetachedState = state,
                    LifecycleState = ColdItemLifecycleState.Cold
                });
            }
            if (stream.Position != stream.Length)
                throw new InvalidDataException("snapshot-trailing-data");
            snapshot = new ColdContainerGraphSnapshot
            {
                Version = 2,
                Containers = containers,
                Items = items
            };
            return true;
        }
        catch (Exception exception) when (exception is EndOfStreamException ||
            exception is InvalidDataException || exception is IOException ||
            exception is DecoderFallbackException)
        {
            rejectionReason = exception.Message;
            snapshot = null;
            return false;
        }
    }

    private static void WriteCount(BinaryWriter writer, int count)
    {
        if (count < 0 || count > 65535) throw new InvalidDataException("snapshot-count-invalid");
        writer.Write(count);
    }

    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > 65535) throw new InvalidDataException("snapshot-count-invalid");
        return count;
    }

    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());

    private static Guid ReadGuid(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw new EndOfStreamException();
        return new Guid(bytes);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > MaximumStringBytes) throw new InvalidDataException("snapshot-string-too-long");
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadUInt16();
        if (length > MaximumStringBytes) throw new InvalidDataException("snapshot-string-too-long");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }
}
