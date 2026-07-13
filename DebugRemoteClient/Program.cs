using LiteNetLib;
using LiteNetLib.Utils;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Managers.Client;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Packets.Clientbound.World;
using Multiplayer.Networking.Packets.Common;
using Multiplayer.Networking.Packets.Serverbound;
using Multiplayer.Networking.Serialization;
using Multiplayer.Networking.TransportLayers;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using UnityEngine;

namespace Multiplayer.DebugClient;

internal static class Program
{
    private static readonly NetPacketProcessor processor = new();
    private static readonly ConcurrentQueue<string> commands = new();
    private static readonly Dictionary<byte, DebugPlayer> players = [];
    private static readonly JsonSerializerSettings debugJsonSettings = new()
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        MaxDepth = 8,
        Converters = { new UnityValueJsonConverter() }
    };
    private static ITransportPeer server;
    private static TraceWebServer traceUi;
    private static DebugClientProfile profile;
    private static PlayerLoadingState loadState;
    private static bool gameParams, saveData;
    private static uint? expectedTrainsets;
    private static uint receivedTrainsets;
    private static DateTime nextPhaseAt;
    private static string followingPlayer;
    private static Vector3 manualPosition;
    private static bool exitRequested;
    private static string localProtocolFingerprint;
    private static bool rawHexEnabled;
    private static bool verboseEnabled;
    private static readonly Dictionary<string, int> suppressedPackets = [];
    private static DateTime nextSuppressedReportAt = DateTime.UtcNow.AddSeconds(1);
    private static DatagramTrace currentDatagram;

    private static int Main(string[] args)
    {
        string profilePath = args.Length == 1 ? args[0] : "Debug/debug-client.local.json";
        try
        {
            profile = new JavaScriptSerializer().Deserialize<DebugClientProfile>(File.ReadAllText(profilePath));
            profile.Validate();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Invalid debug profile: {exception.Message}");
            return 2;
        }

        PacketSerializationRegistry.Register(processor);
        localProtocolFingerprint = LoadLocalProtocolFingerprint();
        Console.WriteLine($"Protocol manifest fingerprint: {localProtocolFingerprint}");
        SemanticPacketDecoder.RunProductionRoundTripTest();
        Console.WriteLine("Semantic schema self-test passed: CommonItemChangePacket production round-trip.");
        PacketCatalog.RegisterAll(processor, OnDecodedPacket);

        if (profile.EnableWebUi)
        {
            try
            {
                traceUi = new TraceWebServer(profile.WebUiPort);
                traceUi.Start();
                Console.WriteLine($"Trace UI: {traceUi.Url}");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Trace UI unavailable: {exception.Message}");
            }
        }

        LiteNetLibTransport transport = new(message => Console.WriteLine($"[transport] {message}"));
        transport.OnPeerConnected += peer => Console.WriteLine($"Connected to loopback server as peer {peer.Id}.");
        transport.OnPeerDisconnected += (_, reason) =>
        {
            Console.WriteLine($"Disconnected: {reason}");
            exitRequested = true;
        };
        transport.OnNetworkError += (endpoint, error) => Console.WriteLine($"Network error from {endpoint}: {error}");
        transport.OnNetworkReceive += OnRawDatagram;

        NetDataWriter writer = new();
        processor.Write(writer, profile.CreateLoginPacket());
        transport.Start();
        server = transport.Connect(profile.Host, profile.Port, writer);
        followingPlayer = profile.HostUsername;
        StartCommandReader();

        Console.WriteLine("Debug client started. Type /status or /quit.");
        while (!exitRequested)
        {
            transport.PollEvents();
            ProcessCommands();
            AdvanceSettledPhase();
            SendFollowPosition();
            FlushSuppressedPackets();
            Thread.Sleep(10);
        }

        transport.Stop(true);
        traceUi?.Dispose();
        return 0;
    }

    private static void OnRawDatagram(ITransportPeer peer, NetDataReader reader, byte channel, DeliveryMethod delivery)
    {
        // Disconnect additional-data readers have already consumed LiteNetLib's internal
        // connection header. Copy only the unread protocol payload, never RawData's capacity.
        byte[] raw = new byte[reader.AvailableBytes];
        Buffer.BlockCopy(reader.RawData, reader.Position, raw, 0, raw.Length);
        currentDatagram = new DatagramTrace(peer.Id, channel, delivery, raw);
        try
        {
            if (PacketCatalog.TryGetOpaquePacket(raw, out string opaquePacket))
            {
                Console.WriteLine($"OPAQUE {opaquePacket}: bytes={raw.Length} (use /raw true for full payload)");
                PublishTrace("inbound", opaquePacket, "Opaque", $"{raw.Length} bytes; game-runtime decoder intentionally disabled", raw, null, peer, channel, delivery);
                if (opaquePacket == nameof(ClientboundSpawnTrainSetPacket))
                {
                    receivedTrainsets++;
                    TryAdvanceTrainsets();
                }
                return;
            }

            if (SemanticPacketDecoder.TryDecode(raw, out SemanticPacket semanticPacket))
            {
                string detail = semanticPacket.ToJson();
                Console.WriteLine($"SEMANTIC {semanticPacket.PacketType}: {Shorten(detail)}");
                PublishTrace("inbound", semanticPacket.PacketType, semanticPacket is SemanticDecodeFailure ? "DecodeFailure" : "SemanticDecoded", Shorten(detail), raw, detail, peer, channel, delivery);
                return;
            }

            // LiteNetLib raises this callback once per user payload sent through NetPeer.Send.
            // ReadAllPackets treats any remaining bytes as another packet; that produces a
            // misleading "Undefined packet" after a valid trainset has already decoded.
            // Keep the payload boundary intact and make residual bytes visible as diagnostics.
            NetDataReader packetReader = new(raw);
            processor.ReadPacket(packetReader, peer);
            if (packetReader.AvailableBytes > 0)
            {
                byte[] remaining = new byte[packetReader.AvailableBytes];
                Buffer.BlockCopy(packetReader.RawData, packetReader.Position, remaining, 0, remaining.Length);
                Console.WriteLine($"WARNING: decoded datagram has {remaining.Length} trailing byte(s); hex={BitConverter.ToString(remaining)}");
            }
            if (rawHexEnabled && !currentDatagram.Reported)
                WriteRaw(currentDatagram, true);
        }
        catch (Exception exception)
        {
            WriteRaw(currentDatagram, true);
            Console.WriteLine($"DECODE FAILURE: {exception}");
            PublishTrace("inbound", "Unknown", "DecodeFailure", exception.Message, raw, exception.ToString(), peer, channel, delivery);
        }
        finally { currentDatagram = null; }
    }

    private static void OnDecodedPacket(object packet)
    {
        if (currentDatagram != null && rawHexEnabled && !currentDatagram.Reported)
            WriteRaw(currentDatagram, true);

        string detail = DescribePacket(packet);
        PublishTrace("inbound", packet.GetType().Name, "ProductionDecoded", Shorten(detail), currentDatagram?.Raw, detail, null, 0, default);

        if (IsHighFrequency(packet) && !verboseEnabled)
        {
            string typeName = packet.GetType().Name;
            suppressedPackets[typeName] = suppressedPackets.TryGetValue(typeName, out int count) ? count + 1 : 1;
        }
        else
        {
            Console.WriteLine($"DECODED {packet.GetType().Name}: {detail}");
        }
        switch (packet)
        {
            case ClientboundLoginResponsePacket login when login.Accepted:
                if (!VerifyHostProtocolFingerprint(login.ProtocolFingerprint))
                {
                    Console.Error.WriteLine("Protocol manifest mismatch: semantic decoding is disabled and the debug client will disconnect.");
                    exitRequested = true;
                    return;
                }
                SemanticPacketDecoder.Enable();
                SendLoadState(PlayerLoadingState.ReadyForGameData);
                break;
            case ClientboundLoginResponsePacket login:
                Console.WriteLine($"Login rejected: {login.ReasonKey}");
                if (login.Missing.Length > 0)
                    Console.WriteLine($"  Host does not have client mod IDs: {string.Join(", ", login.Missing.Select(FormatMod))}");
                if (login.Extra.Length > 0)
                    Console.WriteLine($"  Add these host mod IDs to the debug profile: {string.Join(", ", login.Extra.Select(FormatMod))}");
                break;
            case ClientboundGameParamsPacket:
                gameParams = true; TryAdvanceGameData(); break;
            case ClientboundSaveGameDataPacket:
                saveData = true; TryAdvanceGameData(); break;
            case ClientboundRailwayStatePacket:
                SendLoadState(PlayerLoadingState.ReadyForTrainSets); break;
            case ClientboundLoadStateInfoPacket info when info.LoadingState == PlayerLoadingState.ReadyForTrainSets:
                expectedTrainsets = info.ItemsToLoad; TryAdvanceTrainsets(); break;
            case ClientboundSpawnTrainSetPacket:
                receivedTrainsets++; TryAdvanceTrainsets(); break;
            case ClientboundPlayerJoinedPacket joined:
                players[joined.PlayerId] = new DebugPlayer(joined.Username, joined.Position, joined.Rotation); break;
            case ClientboundPlayerDisconnectPacket left:
                players.Remove(left.PlayerId); break;
            case ClientboundPlayerPositionPacket position when players.TryGetValue(position.PlayerId, out DebugPlayer player):
                player.Position = position.Position; player.Rotation = position.RotationY; break;
        }
    }

    private static string DescribePacket(object packet)
    {
        switch (packet)
        {
            case ClientboundGameParamsPacket gameParamsPacket:
                return $"SerializedGameParams bytes={gameParamsPacket.SerializedGameParams?.Length ?? 0}";
            case ClientboundSaveGameDataPacket saveGamePacket:
                return $"GameMode={saveGamePacket.GameMode}; Money={saveGamePacket.Money}; PlayerItems={saveGamePacket.PlayerItems?.Length ?? 0}";
            case ClientboundSpawnTrainSetPacket trainsetPacket:
                return $"SpawnParts={trainsetPacket.SpawnParts?.Length ?? 0}; AutoCouple={trainsetPacket.AutoCouple}";
            case global::Multiplayer.Networking.Packets.Common.CommonItemChangePacket itemPacket:
                return $"Items={itemPacket.Items?.Count ?? 0}";
            case global::Multiplayer.Networking.Packets.Clientbound.Jobs.ClientboundJobsCreatePacket jobsPacket:
                return $"StationNetId={jobsPacket.StationNetId}; Jobs={jobsPacket.Jobs?.Length ?? 0}";
        }

        try
        {
            return JsonConvert.SerializeObject(packet, Formatting.None, debugJsonSettings);
        }
        catch (Exception exception)
        {
            return $"<decoded; format failed: {exception.GetType().Name}: {exception.Message}>";
        }
    }

    private static bool IsHighFrequency(object packet) => packet is ClientboundTickSyncPacket || packet is ClientboundPingUpdatePacket || packet is ClientboundPlayerPositionPacket;

    private static void FlushSuppressedPackets()
    {
        if (DateTime.UtcNow < nextSuppressedReportAt || suppressedPackets.Count == 0) return;
        Console.WriteLine($"SUPPRESSED (use /verbose on): {string.Join(", ", suppressedPackets.Select(pair => $"{pair.Key} x{pair.Value}"))}");
        suppressedPackets.Clear();
        nextSuppressedReportAt = DateTime.UtcNow.AddSeconds(1);
    }

    private static void WriteRaw(DatagramTrace trace, bool includeHex)
    {
        trace.Reported = true;
        string hex = includeHex ? BitConverter.ToString(trace.Raw) : "<hidden; use /raw on>";
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] RAW inbound peer={trace.PeerId} channel={trace.Channel} delivery={trace.Delivery} bytes={trace.Raw.Length} hex={hex}");
    }

    private static void TryAdvanceGameData()
    {
        if (gameParams && saveData && loadState == PlayerLoadingState.ReadyForGameData)
            SendLoadState(PlayerLoadingState.ReadyForWorldState);
    }

    private static void TryAdvanceTrainsets()
    {
        if (expectedTrainsets.HasValue && receivedTrainsets >= expectedTrainsets.Value && loadState == PlayerLoadingState.ReadyForTrainSets)
        {
            SendLoadState(PlayerLoadingState.ReadyForItems);
            nextPhaseAt = DateTime.UtcNow.AddMilliseconds(250);
        }
    }

    private static void AdvanceSettledPhase()
    {
        if (nextPhaseAt == default || DateTime.UtcNow < nextPhaseAt) return;
        nextPhaseAt = default;
        if (loadState == PlayerLoadingState.ReadyForItems) { SendLoadState(PlayerLoadingState.ReadyForJobs); nextPhaseAt = DateTime.UtcNow.AddMilliseconds(250); }
        else if (loadState == PlayerLoadingState.ReadyForJobs) { SendLoadState(PlayerLoadingState.ReadyForTiles); nextPhaseAt = DateTime.UtcNow.AddMilliseconds(250); }
        else if (loadState == PlayerLoadingState.ReadyForTiles) SendLoadState(PlayerLoadingState.Complete);
    }

    private static void SendLoadState(PlayerLoadingState state)
    {
        if (state <= loadState) return;
        Send(new ServerboundLoadStateUpdatePacket { LoadState = state }, DeliveryMethod.ReliableOrdered);
        loadState = state;
        Console.WriteLine($"OUTBOUND load state: {state}");
    }

    private static string FormatMod(ModInfo mod) => string.IsNullOrEmpty(mod.Version) ? mod.Id : $"{mod.Id} v{mod.Version}";

    private static DateTime lastPositionAt;
    private static void SendFollowPosition()
    {
        if (loadState != PlayerLoadingState.Complete || DateTime.UtcNow - lastPositionAt < TimeSpan.FromMilliseconds(100)) return;
        Vector3 position = manualPosition;
        if (!string.IsNullOrWhiteSpace(followingPlayer) && players.Values.FirstOrDefault(p => p.Username.Equals(followingPlayer, StringComparison.OrdinalIgnoreCase)) is DebugPlayer host)
        {
            float radians = host.Rotation * Mathf.Deg2Rad;
            Vector3 right = new(Mathf.Cos(radians), 0, -Mathf.Sin(radians));
            Vector3 forward = new(Mathf.Sin(radians), 0, Mathf.Cos(radians));
            position = host.Position + right * profile.FollowOffset.Right + Vector3.up * profile.FollowOffset.Up + forward * profile.FollowOffset.Forward;
        }
        Send(new ServerboundPlayerPositionPacket { Position = position, MoveDir = Vector2.zero, RotationY = 0, CarID = 0 }, DeliveryMethod.Sequenced);
        lastPositionAt = DateTime.UtcNow;
    }

    private static void Send<T>(T packet, DeliveryMethod method) where T : class, new()
    {
        NetDataWriter writer = new();
        processor.Write(writer, packet);
        string hex = rawHexEnabled ? BitConverter.ToString(writer.Data, 0, writer.Length) : "<hidden; use /raw on>";
        Console.WriteLine($"OUTBOUND {typeof(T).Name} delivery={method} bytes={writer.Length} hex={hex}");
        byte[] raw = new byte[writer.Length];
        Buffer.BlockCopy(writer.Data, 0, raw, 0, raw.Length);
        string detail = DescribePacket(packet);
        PublishTrace("outbound", typeof(T).Name, "Sent", Shorten(detail), raw, detail, server, 0, method);
        server?.Send(writer, method);
    }

    private static void StartCommandReader() => Task.Run(() => { string line; while ((line = Console.ReadLine()) != null) commands.Enqueue(line); });
    private static void ProcessCommands()
    {
        while (commands.TryDequeue(out string command))
        {
            string[] parts = command.Split(' '); string op = parts[0].ToLowerInvariant();
            if (op == "/quit") exitRequested = true;
            if (op == "/status") Console.WriteLine($"state={loadState}; players={string.Join(", ", players.Select(p => $"{p.Key}:{p.Value.Username}"))}; following={followingPlayer}");
            if (op == "/ui" && traceUi != null) Console.WriteLine($"Trace UI: {traceUi.Url}");
            if (op == "/raw" && parts.Length == 2 && bool.TryParse(parts[1], out bool raw)) { rawHexEnabled = raw; Console.WriteLine($"Raw hex {(raw ? "enabled" : "disabled")}. Decode failures always include hex."); }
            if (op == "/verbose" && parts.Length == 2 && bool.TryParse(parts[1], out bool verbose)) { verboseEnabled = verbose; Console.WriteLine($"High-frequency packet output {(verbose ? "enabled" : "suppressed")}. "); }
            if (op == "/follow-host") followingPlayer = profile.HostUsername;
            if (op == "/follow" && parts.Length > 1) followingPlayer = string.Join(" ", parts.Skip(1));
            if (op == "/teleport" && parts.Length == 4 && float.TryParse(parts[1], out float x) && float.TryParse(parts[2], out float y) && float.TryParse(parts[3], out float z)) { followingPlayer = null; manualPosition = new Vector3(x, y, z); }
            if (op == "/offset" && parts.Length == 4 && float.TryParse(parts[1], out float r) && float.TryParse(parts[2], out float u) && float.TryParse(parts[3], out float f)) profile.FollowOffset = new FollowOffset { Right = r, Up = u, Forward = f };
        }
    }

    private static string Shorten(string value) => value.Length <= 180 ? value : value.Substring(0, 177) + "...";

    private static void PublishTrace(string direction, string packetType, string status, string summary, byte[] raw, string detail, ITransportPeer peer, byte channel, DeliveryMethod delivery)
    {
        traceUi?.Publish(new TraceEvent
        {
            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'"),
            Direction = direction,
            PacketType = packetType,
            Status = status,
            Summary = summary,
            Detail = detail ?? string.Empty,
            RawHex = raw == null ? string.Empty : BitConverter.ToString(raw),
            PeerId = peer?.Id ?? currentDatagram?.PeerId ?? -1,
            Channel = peer == null && currentDatagram != null ? currentDatagram.Channel : channel,
            Delivery = peer == null && currentDatagram != null ? currentDatagram.Delivery.ToString() : delivery.ToString()
        });
    }

    private static string LoadLocalProtocolFingerprint()
    {
        ProtocolManifestInfo embedded = ProtocolManifestProvider.Current;
        string sidecarPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "protocol-manifest.json");
        if (!File.Exists(sidecarPath))
        {
            Console.Error.WriteLine("WARNING: protocol-manifest.json is missing; semantic decoding will remain disabled.");
            return "missing";
        }
        ProtocolManifestInfo sidecar = JsonConvert.DeserializeObject<ProtocolManifestInfo>(File.ReadAllText(sidecarPath));
        if (string.IsNullOrWhiteSpace(embedded.Fingerprint) || embedded.Fingerprint == "missing" || embedded.Fingerprint != sidecar?.Fingerprint)
        {
            Console.Error.WriteLine($"WARNING: embedded manifest ({embedded.Fingerprint}) does not match sidecar ({sidecar?.Fingerprint}); semantic decoding will remain disabled.");
            return "mismatch";
        }
        return embedded.Fingerprint;
    }

    private static bool VerifyHostProtocolFingerprint(string hostFingerprint)
    {
        if (string.IsNullOrWhiteSpace(hostFingerprint))
        {
            Console.Error.WriteLine("Host did not provide a protocol manifest fingerprint.");
            return false;
        }
        if (localProtocolFingerprint == "missing" || localProtocolFingerprint == "mismatch" || !string.Equals(localProtocolFingerprint, hostFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Host fingerprint: {hostFingerprint}; debug fingerprint: {localProtocolFingerprint}");
            return false;
        }
        Console.WriteLine("Protocol manifest fingerprint verified against host.");
        return true;
    }
}

internal static class PacketCatalog
{
    private static readonly Dictionary<ulong, string> opaquePackets = [];

    public static void RegisterAll(NetPacketProcessor processor, Action<object> sink)
    {
        // Login rejection arrives as connection additional-data, before any other packet can be
        // observed. Register the bootstrap messages explicitly as well as through the reflective
        // catalog so configuration errors are always decoded.
        RegisterReusable<ClientboundLoginResponsePacket>(processor, sink);
        RegisterReusable<ClientboundDisconnectPacket>(processor, sink);

        foreach (Type type in GetLoadableTypes(typeof(NetworkClient).Assembly).Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters && t.Name.EndsWith("Packet") && (t.Namespace?.Contains("Packets.Clientbound") == true || t.Namespace?.Contains("Packets.Common") == true) && t.GetConstructor(Type.EmptyTypes) != null))
        {
            if (RequiresGameRuntime(type))
            {
                opaquePackets[GetHash(processor, type)] = type.Name;
                continue;
            }
            MethodInfo method = typeof(PacketCatalog).GetMethod(typeof(INetSerializable).IsAssignableFrom(type) ? nameof(RegisterSerializable) : nameof(RegisterReusable), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(type);
            method.Invoke(null, [processor, sink]);
        }
    }

    public static bool TryGetOpaquePacket(byte[] raw, out string packetName)
    {
        packetName = null;
        if (raw.Length < sizeof(ulong)) return false;
        ulong hash = BitConverter.ToUInt64(raw, 0);
        return opaquePackets.TryGetValue(hash, out packetName);
    }

    private static bool RequiresGameRuntime(Type type) => type == typeof(ClientboundSpawnTrainSetPacket) || type.FullName == "Multiplayer.Networking.Packets.Clientbound.Jobs.ClientboundJobsCreatePacket";

    private static ulong GetHash(NetPacketProcessor processor, Type type)
    {
        MethodInfo getHash = typeof(NetPacketProcessor).GetMethod("GetHash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (getHash == null)
            throw new MissingMethodException(typeof(NetPacketProcessor).FullName, "GetHash<T>");
        return (ulong)getHash.MakeGenericMethod(type).Invoke(processor, null);
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            Console.WriteLine("WARNING: some non-protocol Multiplayer types could not load in the standalone process:");
            foreach (Exception loaderException in exception.LoaderExceptions.Where(e => e != null).Distinct())
                Console.WriteLine($"  {loaderException.GetType().Name}: {loaderException.Message}");

            return exception.Types.Where(type => type != null);
        }
    }
    private static void RegisterReusable<T>(NetPacketProcessor processor, Action<object> sink) where T : class, new() => processor.SubscribeReusable<T>(packet => sink(packet));
    private static void RegisterSerializable<T>(NetPacketProcessor processor, Action<object> sink) where T : class, INetSerializable, new() => processor.SubscribeNetSerializable<T>(packet => sink(packet));
}

public sealed class DebugClientProfile
{
    public string Host { get; set; } = "127.0.0.1"; public int Port { get; set; } = 7778; public string Username { get; set; } = "DebugClient"; public string Guid { get; set; } = System.Guid.NewGuid().ToString(); public string Password { get; set; } = ""; public string BuildVersion { get; set; } = ""; public string HostUsername { get; set; } = ""; public FollowOffset FollowOffset { get; set; } = new(); public List<DebugMod> Mods { get; set; } = new(); public bool EnableWebUi { get; set; } = true; public int WebUiPort { get; set; } = 7780;
    public void Validate() { if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(BuildVersion) || !System.Guid.TryParse(Guid, out _)) throw new ArgumentException("Host, BuildVersion, and a valid Guid are required."); }
    // The server compares mod IDs only. Do not pass profile URLs into ModInfo: its URL trust
    // helper logs through UnityModManager, which is intentionally unavailable in this console app.
    public ServerboundClientLoginPacket CreateLoginPacket() => new() { Username = Username, Guid = System.Guid.Parse(Guid).ToByteArray(), Password = Password, BuildVersion = BuildVersion, Mods = Mods.Select(m => new ModInfo(m.Id, m.Version, "")).ToArray() };
}
public sealed class DebugMod { public string Id { get; set; } = ""; public string Version { get; set; } = ""; public string Url { get; set; } = ""; }
public sealed class FollowOffset { public float Right { get; set; } = 5; public float Up { get; set; } public float Forward { get; set; } }
internal sealed class DebugPlayer { public string Username; public Vector3 Position; public float Rotation; public DebugPlayer(string username, Vector3 position, float rotation) { Username = username; Position = position; Rotation = rotation; } }
internal sealed class DatagramTrace
{
    public int PeerId { get; }
    public byte Channel { get; }
    public DeliveryMethod Delivery { get; }
    public byte[] Raw { get; }
    public bool Reported { get; set; }
    public DatagramTrace(int peerId, byte channel, DeliveryMethod delivery, byte[] raw) { PeerId = peerId; Channel = channel; Delivery = delivery; Raw = raw; }
}

/// <summary>
/// Unity value types expose computed properties such as Quaternion.eulerAngles and
/// Vector3.normalized. A console observer must print only their stored scalar components.
/// </summary>
internal sealed class UnityValueJsonConverter : JsonConverter
{
    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) => objectType == typeof(Vector2) || objectType == typeof(Vector3) || objectType == typeof(Vector4) || objectType == typeof(Quaternion) || objectType == typeof(Color) || objectType == typeof(Bounds);

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case Vector2 vector2:
                Write(writer, "x", vector2.x); Write(writer, "y", vector2.y); break;
            case Vector3 vector3:
                Write(writer, "x", vector3.x); Write(writer, "y", vector3.y); Write(writer, "z", vector3.z); break;
            case Vector4 vector4:
                Write(writer, "x", vector4.x); Write(writer, "y", vector4.y); Write(writer, "z", vector4.z); Write(writer, "w", vector4.w); break;
            case Quaternion quaternion:
                Write(writer, "x", quaternion.x); Write(writer, "y", quaternion.y); Write(writer, "z", quaternion.z); Write(writer, "w", quaternion.w); break;
            case Color color:
                Write(writer, "r", color.r); Write(writer, "g", color.g); Write(writer, "b", color.b); Write(writer, "a", color.a); break;
            case Bounds bounds:
                writer.WritePropertyName("center"); serializer.Serialize(writer, bounds.center);
                writer.WritePropertyName("size"); serializer.Serialize(writer, bounds.size); break;
        }
        writer.WriteEndObject();
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) => throw new NotSupportedException("Debug JSON output is write-only.");

    private static void Write(JsonWriter writer, string name, float value)
    {
        writer.WritePropertyName(name);
        writer.WriteValue(value);
    }
}
