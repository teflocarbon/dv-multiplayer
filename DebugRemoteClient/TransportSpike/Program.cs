using LiteNetLib;
using LiteNetLib.Utils;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Serverbound;
using Multiplayer.Networking.TransportLayers;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;

namespace Multiplayer.TransportSpike;

internal static class Program
{
    private const uint DerailValleyAppId = 588030;
    private const int TimeoutMilliseconds = 20_000;

    private static bool connected;
    private static bool receivedLoginResponse;
    private static string failure;

    private static int Main(string[] args)
    {
        string profilePath = args.Length == 1 ? args[0] : "transport-spike.local.json";
        if (!File.Exists(profilePath))
        {
            Console.Error.WriteLine($"Profile not found: {Path.GetFullPath(profilePath)}");
            Console.Error.WriteLine("Copy transport-spike.example.json to transport-spike.local.json and fill in the hosted game's details.");
            return 2;
        }

        TransportSpikeProfile profile;
        try
        {
            profile = new JavaScriptSerializer().Deserialize<TransportSpikeProfile>(File.ReadAllText(profilePath));
            profile.Validate();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Invalid profile: {exception.Message}");
            return 2;
        }

        SteamWorksTransport transport = null;
        try
        {
            // Use normal asynchronous Steam callbacks, then pump them through
            // SteamWorksTransport.PollEvents exactly as the game does.
            SteamClient.Init(DerailValleyAppId, asyncCallbacks: true);
            if (!SteamClient.IsValid)
            {
                Console.Error.WriteLine("SteamClient.Init returned without a valid Steam client. Ensure Steam is running and the account owns Derail Valley.");
                return 3;
            }

            WriteSteamDiagnostics();

            transport = new SteamWorksTransport(
                message => Console.WriteLine($"[transport] {message}"),
                message => Console.WriteLine($"[transport] {message}"));

            transport.OnPeerConnected += peer =>
            {
                connected = true;
                Console.WriteLine($"Connected as transport peer {peer.Id} ({peer.ConnectionState}). Waiting for login response...");
            };
            transport.OnPeerDisconnected += (_, reason) => failure ??= $"Disconnected: {reason}";
            transport.OnNetworkError += (_, error) => failure ??= $"Network error: {error}";
            transport.OnNetworkReceive += (_, reader, channel, delivery) =>
            {
                Console.WriteLine($"Received {reader.AvailableBytes} bytes on channel {channel} ({delivery}).");
                try
                {
                    CreatePacketProcessor().ReadAllPackets(reader);
                }
                catch (Exception exception)
                {
                    failure ??= $"Could not decode server response: {exception.Message}";
                }
            };

            NetDataWriter writer = new();
            CreatePacketProcessor().Write(writer, profile.CreateLoginPacket());
            transport.Start();
            transport.Connect(profile.Host, profile.Port, writer);

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMilliseconds);
            while (DateTime.UtcNow < deadline && failure == null && !receivedLoginResponse)
            {
                transport.PollEvents();
                Thread.Sleep(10);
            }

            if (receivedLoginResponse)
            {
                Console.WriteLine("SUCCESS: second process connected through SteamWorksTransport and received a valid login response.");
                return 0;
            }

            Console.Error.WriteLine(failure ?? (connected
                ? "Timed out after connecting without a login response."
                : "Timed out before a Steam transport connection was established."));
            return 4;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Steam transport spike failed: {exception}");
            return 5;
        }
        finally
        {
            transport?.Stop(true);
            if (SteamClient.IsValid)
                SteamClient.Shutdown();
        }
    }

    private static NetPacketProcessor CreatePacketProcessor()
    {
        NetPacketProcessor processor = new();
        processor.RegisterNestedType(ModInfo.Serialize, ModInfo.Deserialize);
        processor.SubscribeReusable<ClientboundLoginResponsePacket>(packet =>
        {
            receivedLoginResponse = true;
            Console.WriteLine(packet.Accepted
                ? $"Login accepted. Assigned player ID: {packet.PlayerId}."
                : $"Login rejected. Reason key: {packet.ReasonKey}. Missing: {packet.Missing?.Length ?? 0}; extra: {packet.Extra?.Length ?? 0}.");
        });
        return processor;
    }

    private static void WriteSteamDiagnostics()
    {
        Assembly facepunchAssembly = typeof(SteamClient).Assembly;
        FileVersionInfo version = FileVersionInfo.GetVersionInfo(facepunchAssembly.Location);

        Console.WriteLine("Steam initialization diagnostics:");
        Console.WriteLine($"  IsValid: {SteamClient.IsValid}");
        Console.WriteLine($"  SteamId: {SteamClient.SteamId}");
        Console.WriteLine($"  Name: {SteamClient.Name}");
        Console.WriteLine($"  IsSubscribed: {SteamApps.IsSubscribed}");
        Console.WriteLine($"  Process: {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Console.WriteLine($"  Facepunch assembly: {facepunchAssembly.Location}");
        Console.WriteLine($"  Facepunch file version: {version.FileVersion}");
        Console.WriteLine($"  Native API beside executable: {File.Exists(Path.Combine(AppContext.BaseDirectory, "steam_api64.dll"))}");
        Console.WriteLine($"  AppID file beside executable: {File.Exists(Path.Combine(AppContext.BaseDirectory, "steam_appid.txt"))}");
    }
}

public sealed class TransportSpikeProfile
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7777;
    public string Username { get; set; } = "TransportSpike";
    public string Guid { get; set; } = System.Guid.NewGuid().ToString();
    public string Password { get; set; } = "";
    public string BuildVersion { get; set; } = "";
    public List<TransportSpikeMod> Mods { get; set; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("Host is required.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(Username)) throw new ArgumentException("Username is required.");
        if (!System.Guid.TryParse(Guid, out _)) throw new ArgumentException("Guid must be a valid GUID.");
        if (string.IsNullOrWhiteSpace(BuildVersion)) throw new ArgumentException("BuildVersion must exactly match the hosted game.");
    }

    public ServerboundClientLoginPacket CreateLoginPacket() => new()
    {
        Username = Username,
        Guid = System.Guid.Parse(Guid).ToByteArray(),
        Password = Password ?? string.Empty,
        BuildVersion = BuildVersion,
        Mods = Mods.ConvertAll(mod => new ModInfo(mod.Id, mod.Version, mod.Url)).ToArray()
    };
}

public sealed class TransportSpikeMod
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
}
