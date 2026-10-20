using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Shared.Protocol.Serialization;

/// <summary>
///     Names a packet from the type hash that leads it on the wire, for log lines about packets
///     nobody subscribed. It's LiteNetLib's <c>NetPacketProcessor</c> hash: FNV-1a over the type's
///     <see cref="Type.ToString"/>.
/// </summary>
public static class PacketNames
{
    private static Dictionary<ulong, string> names;

    public static string Describe(ulong hash)
    {
        names ??= typeof(PacketNames).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && !type.IsGenericTypeDefinition &&
                           type.Namespace?.StartsWith("Multiplayer.Shared.Protocol.Packets") == true)
            .GroupBy(Hash)
            .ToDictionary(group => group.Key, group => group.First().Name);
        return names.TryGetValue(hash, out string name) ? name : $"packet {hash:X16}";
    }

    public static ulong Hash(Type type)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in type.ToString())
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
