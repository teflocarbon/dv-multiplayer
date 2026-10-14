using LiteNetLib;
using LiteNetLib.Utils;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Serialization;
using Multiplayer.Networking.TransportLayers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Debugging;

public static class DebugTrace
{
    private static readonly Dictionary<ulong, string> packetNames = BuildPacketNames();
    private static readonly HashSet<string> tracedPackets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> manifestHighFrequencyPackets = new(
        ProtocolManifestProvider.Current.Packets.Where(packet => packet.HighFrequency || packet.SuppressByDefault)
            .Select(packet => ShortName(packet.TypeName)), StringComparer.OrdinalIgnoreCase);

    public static void TracePacket(string packetType, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(packetType)) return;
        if (enabled) tracedPackets.Add(packetType); else tracedPackets.Remove(packetType);
    }
    public static bool IsPacketTraced(string packetType) => !string.IsNullOrWhiteSpace(packetType) && tracedPackets.Contains(packetType);
    public static bool IsNoisyPacket(string packetType) => !string.IsNullOrWhiteSpace(packetType) && manifestHighFrequencyPackets.Contains(packetType);

    public static Dictionary<string, object> ItemSnapshotData(object snapshot)
    {
        DebugPacketProjection projection = DebugPacketProjectorRegistry.Project(snapshot, DebugRuntime.Session?.PlayerId);
        Dictionary<string, object> data = projection.Wire as Dictionary<string, object> ?? new(StringComparer.Ordinal) { ["wire"] = projection.Wire };
        data["stateFingerprint"] = DebugStateFingerprint.Compute(data);
        if (!string.IsNullOrEmpty(projection.Summary)) data["summary"] = projection.Summary;
        if (projection.Context.Count > 0) data["context"] = projection.Context;
        return data;
    }

    public static void PacketReceived(ITransportPeer peer, NetDataReader reader, byte channel, DeliveryMethod delivery, DebugRuntimeSide side)
    {
        if (!DebugRuntime.EnabledFor("packet") || reader == null) return;
        int offset = reader.Position;
        int count = reader.AvailableBytes;
        DebugDiagnostics.RecordPacket(true, count);
        ulong hash = count >= sizeof(ulong) ? BitConverter.ToUInt64(reader.RawData, offset) : 0;
        packetNames.TryGetValue(hash, out string packetType);
        bool highFrequency = IsSampled(packetType);
        if (highFrequency && !DebugRuntime.ShouldEmitHighFrequency("Packet", packetType ?? hash.ToString("X16"))) return;
        Dictionary<string, object> data = PacketData(packetType, reader.RawData, offset, count);
        data["direction"] = "inbound";
        data["peerId"] = peer?.Id ?? -1;
        data["channel"] = channel;
        data["delivery"] = delivery.ToString();
        DebugRuntime.Publish("packet", "packet.raw.receive", side, entityType: "Packet", entityId: packetType ?? hash.ToString("X16"), data: data, highFrequency: highFrequency, samplingDecided: true);
    }

    public static void PacketSerialized<T>(NetDataWriter writer, DebugRuntimeSide side, T packet)
    {
        if (!DebugRuntime.EnabledFor("packet") || writer == null) return;
        string packetType = typeof(T).Name;
        bool highFrequency = IsSampled(packetType);
        if (highFrequency && !DebugRuntime.ShouldEmitHighFrequency("Packet", packetType)) return;
        Dictionary<string, object> data = PacketData(packetType, writer.Data, 0, writer.Length);
        data["direction"] = "outbound";
        if (DebugRuntime.RuntimeSettings?.TraceMode != DebugTraceMode.Summary)
        {
            DebugPacketProjection projection = DebugPacketProjectorRegistry.Project(packet, DebugRuntime.Session?.PlayerId);
            data["decoded"] = projection.Wire;
            data["summary"] = projection.Summary;
            if (projection.Context.Count > 0) data["context"] = projection.Context;
        }
        DebugRuntime.Publish("packet", "packet.serialized", side, entityType: "Packet", entityId: packetType, data: data, highFrequency: highFrequency, samplingDecided: true);
    }

    public static void PacketSending(ITransportPeer peer, NetDataWriter writer, DeliveryMethod delivery, DebugRuntimeSide side, string packetType = null)
    {
        if (!DebugRuntime.EnabledFor("packet") || writer == null) return;
        DebugDiagnostics.RecordPacket(false, writer.Length);
        if (packetType == null && writer.Length >= sizeof(ulong)) packetNames.TryGetValue(BitConverter.ToUInt64(writer.Data, 0), out packetType);
        bool highFrequency = IsSampled(packetType);
        if (highFrequency && !DebugRuntime.ShouldEmitHighFrequency("Packet", packetType ?? "Unknown")) return;
        Dictionary<string, object> data = PacketData(packetType, writer.Data, 0, writer.Length);
        data["direction"] = "outbound";
        data["peerId"] = peer?.Id ?? -1;
        data["delivery"] = delivery.ToString();
        DebugRuntime.Publish("packet", "packet.raw.send", side, entityType: "Packet", entityId: packetType ?? "Unknown", data: data, highFrequency: highFrequency, samplingDecided: true);
    }

    public static IDisposable BeginHandler(object packet, DebugRuntimeSide side, string entityType = "", string entityId = "")
    {
        if (!DebugRuntime.EnabledFor("packet")) return EmptyScope.Instance;
        string packetType = packet?.GetType().Name ?? "Unknown";
        string effectiveType = string.IsNullOrEmpty(entityType) ? "Packet" : entityType;
        string effectiveId = string.IsNullOrEmpty(entityId) ? packetType : entityId;
        bool highFrequency = IsSampled(packetType);
        if (highFrequency && !DebugRuntime.ShouldEmitHighFrequency(effectiveType, effectiveId)) return EmptyScope.Instance;
        Dictionary<string, object> data = new() { ["packetType"] = packetType };
        if (DebugRuntime.RuntimeSettings.TraceMode != DebugTraceMode.Summary)
        {
            DebugPacketProjection projection = DebugPacketProjectorRegistry.Project(packet, DebugRuntime.Session?.PlayerId);
            data["decoded"] = projection.Wire;
            data["summary"] = projection.Summary;
            if (projection.Context.Count > 0) data["context"] = projection.Context;
            DebugRuntime.Publish("packet", "packet.decoded", side,
                entityType: string.IsNullOrEmpty(entityType) ? "Packet" : entityType,
                entityId: string.IsNullOrEmpty(entityId) ? packetType : entityId,
                data: new() { ["packetType"] = packetType, ["decoded"] = projection.Wire, ["summary"] = projection.Summary, ["context"] = projection.Context }, highFrequency: highFrequency, samplingDecided: true);
        }
        DebugRuntime.Publish("packet", "packet.handler.before", side, entityType: string.IsNullOrEmpty(entityType) ? "Packet" : entityType,
            entityId: effectiveId, data: data, highFrequency: highFrequency, samplingDecided: true);
        return new Scope(() =>
        {
            Dictionary<string, object> after = new() { ["packetType"] = packetType };
            if (DebugRuntime.RuntimeSettings.TraceMode != DebugTraceMode.Summary)
            {
                DebugPacketProjection projection = DebugPacketProjectorRegistry.Project(packet, DebugRuntime.Session?.PlayerId);
                after["decoded"] = projection.Wire; after["summary"] = projection.Summary;
                if (projection.Context.Count > 0) after["context"] = projection.Context;
            }
            DebugRuntime.Publish("packet", "packet.handler.after", side,
                entityType: string.IsNullOrEmpty(entityType) ? "Packet" : entityType,
                entityId: string.IsNullOrEmpty(entityId) ? packetType : entityId,
                data: after, highFrequency: highFrequency, samplingDecided: true);
        });
    }

    public static IDisposable BeginApply(string category, string eventPrefix, DebugRuntimeSide side, string entityType, string entityId,
        Func<Dictionary<string, object>> state, bool highFrequency = false)
    {
        if (!DebugRuntime.EnabledFor(category)) return EmptyScope.Instance;
        if (highFrequency && !DebugRuntime.ShouldEmitHighFrequency(entityType, entityId)) return EmptyScope.Instance;
        DebugRuntime.Publish(category, eventPrefix + ".before", side, entityType: entityType, entityId: entityId,
            data: SafeState(state), highFrequency: highFrequency, samplingDecided: true);
        return new Scope(() => DebugRuntime.Publish(category, eventPrefix + ".after", side, entityType: entityType, entityId: entityId,
            data: SafeState(state), highFrequency: highFrequency, samplingDecided: true));
    }

    public static void Validation(string category, string entityType, string entityId, bool accepted, string reason, DebugRuntimeSide side)
    {
        DebugRuntime.Publish(category, accepted ? category + ".validation-accepted" : category + ".validation-rejected", side,
            accepted ? DebugSeverity.Info : DebugSeverity.Warning, entityType, entityId, new() { ["accepted"] = accepted, ["reason"] = reason ?? string.Empty });
    }

    private static Dictionary<string, object> PacketData(string packetType, byte[] buffer, int offset, int count)
    {
        Dictionary<string, object> data = new()
        {
            ["packetType"] = packetType ?? "Unknown",
            ["rawLength"] = count,
            ["payloadFingerprint"] = DebugPayloadFingerprint.Compute(buffer, offset, count)
        };
        bool sensitive = packetType?.IndexOf("Login", StringComparison.OrdinalIgnoreCase) >= 0;
        if (DebugRuntime.RuntimeSettings?.RawPacketCapture == true && !sensitive)
        {
            byte[] raw = new byte[count];
            Buffer.BlockCopy(buffer, offset, raw, 0, count);
            data["rawBase64"] = Convert.ToBase64String(raw);
        }
        else if (sensitive) data["rawRedacted"] = true;
        return data;
    }

    private static Dictionary<ulong, string> BuildPacketNames()
    {
        Dictionary<ulong, string> result = new();
        foreach (ProtocolManifestPacket packet in ProtocolManifestProvider.Current.Packets)
            if (ulong.TryParse(packet.Hash, System.Globalization.NumberStyles.HexNumber, null, out ulong hash)) result[hash] = ShortName(packet.TypeName);
        return result;
    }
    private static string ShortName(string name) { int index = name?.LastIndexOf('.') ?? -1; return index < 0 ? name : name.Substring(index + 1); }
    private static bool IsHighFrequency(string name) => IsNoisyPacket(name) || name?.IndexOf("Position", StringComparison.OrdinalIgnoreCase) >= 0 || name?.IndexOf("Physics", StringComparison.OrdinalIgnoreCase) >= 0 || name?.IndexOf("Ping", StringComparison.OrdinalIgnoreCase) >= 0 || name?.IndexOf("Ports", StringComparison.OrdinalIgnoreCase) >= 0;
    private static bool IsSampled(string name) => IsHighFrequency(name) && !tracedPackets.Contains(name ?? string.Empty);
    private static Dictionary<string, object> SafeState(Func<Dictionary<string, object>> state) { try { return state?.Invoke() ?? new(); } catch (Exception exception) { return new() { ["snapshotError"] = exception.Message }; } }

    private sealed class Scope : IDisposable { private Action action; public Scope(Action action) => this.action = action; public void Dispose() { Action current = action; action = null; current?.Invoke(); } }
    private sealed class EmptyScope : IDisposable { public static readonly EmptyScope Instance = new(); public void Dispose() { } }
}
