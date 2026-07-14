using System;
using System.Collections.Generic;
using Multiplayer.Components.Networking;
using Multiplayer.Core.Identity;
using Multiplayer.Utils;
using UnityEngine;

namespace Multiplayer.Components;

[DisallowMultipleComponent]
public abstract class IdMonoBehaviour<T, I> : MonoBehaviour where T : struct where I : MonoBehaviour
{
    private static readonly NetIdAllocator<T> idAllocator = new(Increment);
    private static readonly Dictionary<T, IdMonoBehaviour<T, I>> indexToObject = [];

    private T _netId;

    public T NetId {
        get => _netId;
        set => TryRegister(value, alreadyReserved: false);
    }

    protected abstract bool IsIdServerAuthoritative { get; }

    protected static bool Get(T netId, out IdMonoBehaviour<T, I> obj)
    {
        if (indexToObject.TryGetValue(netId, out obj))
            return true;
        obj = null;
        if ((netId as dynamic).CompareTo(default(T)) != 0)
            Multiplayer.LogDebug(() => $"Got invalid NetId {netId} for {typeof(I).Name}{(NetworkLifecycle.Instance.IsProcessingPacket ? $" while processing packet\r\n{Environment.StackTrace}" : "")}");
        return false;
    }

    protected static bool TryGet(T netId, out IdMonoBehaviour<T, I> obj)
    {
        if (indexToObject.TryGetValue(netId, out obj))
            return true;

        obj = null;
        return false;
    }

    protected virtual void Awake()
    {
        if (IsIdServerAuthoritative && !NetworkLifecycle.Instance.IsHost())
            return;
        if (!idAllocator.TryAllocate(out T id))
            throw new InvalidOperationException($"No network IDs remain for {typeof(I).Name}");
        TryRegister(id, alreadyReserved: true);
    }

    public void Register(T id)
    {
        TryRegister(id, alreadyReserved: false);
    }

    protected virtual void OnDestroy()
    {
        if (indexToObject.TryGetValue(NetId, out IdMonoBehaviour<T, I> registered) && ReferenceEquals(registered, this))
        {
            indexToObject.Remove(NetId);
            idAllocator.Release(NetId);
        }
        if (!UnloadWatcher.isUnloading)
            return;
        idAllocator.Reset();
        indexToObject.Clear();
    }

    private bool TryRegister(T id, bool alreadyReserved)
    {
        if (_netId.Equals(id)) return true;
        bool isZero = EqualityComparer<T>.Default.Equals(id, default);
        if (!isZero && indexToObject.TryGetValue(id, out IdMonoBehaviour<T, I> collision) &&
            !ReferenceEquals(collision, this))
        {
            if (alreadyReserved) idAllocator.Release(id);
            Multiplayer.LogError($"Rejected duplicate NetId {id} for {typeof(I).Name}: " +
                $"{collision?.name} already owns it; {name} remains {_netId}");
            return false;
        }
        if (!isZero && !alreadyReserved && !idAllocator.TryReserve(id))
        {
            Multiplayer.LogError($"Rejected already-reserved NetId {id} for {typeof(I).Name}: {name}");
            return false;
        }

        T previous = _netId;
        if (!EqualityComparer<T>.Default.Equals(previous, default) &&
            indexToObject.TryGetValue(previous, out IdMonoBehaviour<T, I> registered) &&
            ReferenceEquals(registered, this))
        {
            indexToObject.Remove(previous);
            idAllocator.Release(previous);
        }
        _netId = id;
        if (!isZero) indexToObject[id] = this;
        return true;
    }

    private static T Increment(T value)
    {
        dynamic next = value;
        next++;
        return (T)next;
    }
}
