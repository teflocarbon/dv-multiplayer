using Multiplayer.Shared.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Multiplayer.Shared.Protocol.Data.RPCs;

/// <summary>
/// The RPC response types by a hash of their name, for <c>ClientboundRpcResponsePacket</c>: the server
/// writes the hash of the response it sends, the client makes that type to read it.
/// </summary>
public static class RpcResponseTypes
{
    private static readonly Dictionary<Type, uint> responseTypeToHash = [];
    private static readonly Dictionary<uint, Type> hashToResponseType = [];

    public static uint GetResponseTypeHash(IRpcResponse response)
    {
        var type = response.GetType();

        if (!responseTypeToHash.TryGetValue(type, out var hash))
        {
            RegisterResponseType(type);
            hash = responseTypeToHash[type];
        }

        return hash;
    }

    public static IRpcResponse CreateResponseInstance(uint responseHash)
    {
        if (!hashToResponseType.TryGetValue(responseHash, out var responseType))
        {
            // Get all IRpcResponses, add to hash, then try again
            Discover();

            if (!hashToResponseType.TryGetValue(responseHash, out responseType))
            {
                Multiplayer.LogError($"Received RPC response with unknown type hash {responseHash:X8}");
                return null;
            }
        }

        Multiplayer.LogDebug(() => $"RpcResponseTypes.CreateResponseInstance({responseHash:X8}) Found response type {responseType.Name}, creating instance...");
        return (IRpcResponse)Activator.CreateInstance(responseType);
    }

    public static void Discover()
    {
        // Get all assemblies in the current domain
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        foreach (var assembly in assemblies)
        {
            try
            {
                // Find all types that implement IRpcResponse and are not abstract/interfaces with a parameterless constructor
                var responseTypes = assembly.GetTypes()
                    .Where
                    (
                        type =>
                            typeof(IRpcResponse).IsAssignableFrom(type) &&
                            !type.IsInterface &&
                            !type.IsAbstract &&
                            type.GetConstructor(Type.EmptyTypes) != null
                    )
                    .ToArray();

                foreach (var type in responseTypes)
                    RegisterResponseType(type);
            }
            catch (ReflectionTypeLoadException ex)
            {
                Multiplayer.Log($"RPC response types could not load some types from assembly {assembly.FullName}: {ex.Message}");
            }
        }

        Multiplayer.LogDebug(() => $"RpcResponseTypes.Discover() Registered types: {string.Join(", ", hashToResponseType.Values.Select(t => t.Name))}");
    }

    private static void RegisterResponseType(Type responseType)
    {
        uint hash = StringHashing.Fnv1aHash(responseType.FullName);

        if (hashToResponseType.ContainsKey(hash))
            return;

        hashToResponseType[hash] = responseType;
        responseTypeToHash[responseType] = hash;
    }
}
