using LiteNetLib.Utils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("Usage: ProtocolManifestGenerator <assembly> <managed-dir> <output-json>"); return 2; }
        string assemblyPath = Path.GetFullPath(args[0]);
        string managedDirectory = args[1];
        string outputPath = Path.GetFullPath(args[2]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) => Resolve(request.Name, Path.GetDirectoryName(assemblyPath), managedDirectory);

        Assembly assembly = Assembly.LoadFrom(assemblyPath);
        List<Type> types = GetLoadableTypes(assembly).ToList();
        ProtocolManifest manifest = new()
        {
            ProtocolVersion = assembly.GetName().Version?.ToString() ?? "unknown",
            BuildVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown",
            Packets = types.Where(IsPacket).Select(CreatePacket).OrderBy(packet => packet.Hash).ToList(),
            // Packet models commonly use enums declared in Components (for example ItemState).
            // Walk only Multiplayer-owned field/property types reachable from packet models; this
            // keeps Unity/game enums out while retaining every enum that affects wire semantics.
            Enums = GetReachableProtocolEnums(types.Where(IsPacket))
                .OrderBy(type => type.FullName).ToDictionary(type => type.FullName, CreateEnum)
        };
        manifest.Fingerprint = Fingerprint(manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllText(outputPath, JsonConvert.SerializeObject(manifest, Formatting.Indented), new UTF8Encoding(false));
        Console.WriteLine($"Generated protocol manifest: {manifest.Packets.Count} packets, {manifest.Enums.Count} enums, fingerprint {manifest.Fingerprint}");
        return 0;
    }

    private static Assembly Resolve(string name, string assemblyDirectory, string managedDirectory)
    {
        string fileName = new AssemblyName(name).Name + ".dll";
        foreach (string directory in new[] { assemblyDirectory, managedDirectory, Path.Combine(managedDirectory, "UnityModManager") }.Where(Directory.Exists))
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }
        return null;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception)
        {
            foreach (Exception error in exception.LoaderExceptions.Where(error => error != null).Distinct())
                Console.Error.WriteLine($"Manifest generator skipped unloadable type dependency: {error.Message}");
            return exception.Types.Where(type => type != null);
        }
    }

    private static bool IsPacket(Type type) => type.IsClass && !type.IsAbstract && type.FullName?.Contains("Multiplayer.Networking.Packets.") == true && type.Name.EndsWith("Packet", StringComparison.Ordinal);

    private static IEnumerable<Type> GetReachableProtocolEnums(IEnumerable<Type> packetTypes)
    {
        HashSet<Type> visited = [];
        Queue<Type> pending = new(packetTypes);
        HashSet<Type> enums = [];
        while (pending.Count > 0)
        {
            Type type = Unwrap(pending.Dequeue());
            if (type?.IsGenericType == true)
            {
                foreach (Type argument in type.GetGenericArguments()) pending.Enqueue(argument);
                type = type.GetGenericTypeDefinition();
            }
            if (type == null || !visited.Add(type)) continue;
            if (type.IsEnum) { enums.Add(type); continue; }
            if (type.Namespace?.StartsWith("Multiplayer", StringComparison.Ordinal) != true) continue;

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                pending.Enqueue(field.FieldType);
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (property.GetIndexParameters().Length == 0)
                    pending.Enqueue(property.PropertyType);
        }
        return enums;
    }

    private static Type Unwrap(Type type)
    {
        while (type != null && (type.IsArray || type.IsByRef || type.IsPointer)) type = type.GetElementType();
        return type;
    }
    private static ManifestPacket CreatePacket(Type type)
    {
        bool highFrequency = type.Name.Contains("Ping") || type.Name.Contains("Tick") || type.Name.Contains("Position") || type.Name.Contains("Physics") || type.Name.Contains("TrainPorts") || type.Name.Contains("BrakeState") || type.Name.Contains("CarHealth");
        // These are continuous replication streams, not discrete gameplay transitions. Keep
        // their counters visible, but do not let them consume the debug trace by default.
        bool suppressByDefault = highFrequency && (type.Name.Contains("Ping") || type.Name.Contains("Tick") || type.Name.Contains("Position") || type.Name.Contains("Physics") || type.Name.Contains("TrainPorts") || type.Name.Contains("BrakeState") || type.Name.Contains("CarHealth"));
        return new ManifestPacket
        {
            Hash = GetHash(type).ToString("X16"),
            TypeName = type.FullName,
            Direction = type.Namespace.Contains(".Serverbound") ? "Serverbound" : type.Namespace.Contains(".Clientbound") ? "Clientbound" : "Bidirectional",
            Category = type.Namespace.Substring(type.Namespace.IndexOf("Packets.", StringComparison.Ordinal) + "Packets.".Length),
            SemanticDecoder = type.FullName == "Multiplayer.Networking.Packets.Common.CommonItemChangePacket" ? "CommonItemChangeV1" : null,
            HighFrequency = highFrequency,
            SuppressByDefault = suppressByDefault
        };
    }
    private static Dictionary<string, long> CreateEnum(Type type) => Enum.GetValues(type).Cast<object>().ToDictionary(value => value.ToString(), value => Convert.ToInt64(value));
    private static ulong GetHash(Type type)
    {
        MethodInfo method = typeof(NetPacketProcessor).GetMethod("GetHash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return (ulong)method.MakeGenericMethod(type).Invoke(new NetPacketProcessor(), null);
    }
    private static string Fingerprint(ProtocolManifest manifest)
    {
        ProtocolManifest copy = manifest.CloneWithoutFingerprint();
        byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(copy, Formatting.None));
        using SHA256 hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}

internal sealed class ProtocolManifest
{
    public string ProtocolVersion { get; set; }
    public string BuildVersion { get; set; }
    public string Fingerprint { get; set; }
    public List<ManifestPacket> Packets { get; set; }
    public Dictionary<string, Dictionary<string, long>> Enums { get; set; }
    public ProtocolManifest CloneWithoutFingerprint() => new() { ProtocolVersion = ProtocolVersion, BuildVersion = BuildVersion, Packets = Packets, Enums = Enums };
}
internal sealed class ManifestPacket { public string Hash { get; set; } public string TypeName { get; set; } public string Direction { get; set; } public string Category { get; set; } public string SemanticDecoder { get; set; } public bool HighFrequency { get; set; } public bool SuppressByDefault { get; set; } }
