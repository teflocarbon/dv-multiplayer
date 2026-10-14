using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Multiplayer.Networking.Serialization;

/// <summary>Loads the build-generated protocol contract embedded in Multiplayer.dll.</summary>
public static class ProtocolManifestProvider
{
    public const string ResourceName = "Multiplayer.ProtocolManifest.json";
    private static readonly Lazy<ProtocolManifestInfo> manifest = new(Load);
    public static ProtocolManifestInfo Current => manifest.Value;

    private static ProtocolManifestInfo Load()
    {
        using Stream stream = typeof(ProtocolManifestProvider).Assembly.GetManifestResourceStream(ResourceName);
        if (stream == null) return new ProtocolManifestInfo { Fingerprint = "missing", BuildVersion = "missing" };
        using StreamReader reader = new(stream);
        return JsonConvert.DeserializeObject<ProtocolManifestInfo>(reader.ReadToEnd()) ?? new ProtocolManifestInfo { Fingerprint = "invalid", BuildVersion = "invalid" };
    }
}

public sealed class ProtocolManifestInfo
{
    public string ProtocolVersion { get; set; }
    public string BuildVersion { get; set; }
    public string Fingerprint { get; set; }
    public List<ProtocolManifestPacket> Packets { get; set; } = [];
    public Dictionary<string, Dictionary<string, long>> Enums { get; set; } = [];
}

public sealed class ProtocolManifestPacket
{
    public string Hash { get; set; }
    public string TypeName { get; set; }
    public string Direction { get; set; }
    public string Category { get; set; }
    public string SemanticDecoder { get; set; }
    public bool HighFrequency { get; set; }
    public bool SuppressByDefault { get; set; }
}
