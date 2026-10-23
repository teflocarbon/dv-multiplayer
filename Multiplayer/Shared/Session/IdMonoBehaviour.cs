using System;
using System.Collections.Generic;
using Multiplayer.Core.Identity;
using UnityEngine;

namespace Multiplayer.Shared.Session;

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
        return false;
    }

    /// <summary>Every object holding a NetId of this type, by NetId.</summary>
    protected static IEnumerable<KeyValuePair<T, IdMonoBehaviour<T, I>>> Registered => indexToObject;

    protected virtual void Awake()
    {
        if (IsIdServerAuthoritative && !NetworkRole.IsServer)
            return;
        if (!idAllocator.TryAllocate(out T id))
            throw new InvalidOperationException($"No network IDs remain for {typeof(I).Name}");
        TryRegister(id, alreadyReserved: true);
    }

    public void Register(T id)
    {
        TryRegister(id, alreadyReserved: false);
    }

    /// <summary>
    /// Frees this object's network ID now, rather than when Unity destroys it at the end of the
    /// frame, for an object that is finished with but not yet gone. The allocator prefers IDs it
    /// has never issued, so a freed ID isn't handed straight to another object.
    /// </summary>
    protected void ReleaseNetId()
    {
        if (indexToObject.TryGetValue(NetId, out IdMonoBehaviour<T, I> registered) && ReferenceEquals(registered, this))
        {
            indexToObject.Remove(NetId);
            idAllocator.Release(NetId);
        }
    }

    /// <summary>
    /// Lets go of this object's network ID without freeing it: the ID still names the thing this
    /// object stood for, kept as data meanwhile, and the next object for it takes the ID up again
    /// (<see cref="TakeUpNetId"/>). Until then nothing else is given it.
    /// </summary>
    protected void HandOverNetId()
    {
        if (indexToObject.TryGetValue(NetId, out IdMonoBehaviour<T, I> registered) && ReferenceEquals(registered, this))
            indexToObject.Remove(NetId);
        _netId = default;
    }

    /// <summary>Takes up an ID handed over (<see cref="HandOverNetId"/>). False if another object has it.</summary>
    protected bool TakeUpNetId(T id) =>
        !indexToObject.TryGetValue(id, out IdMonoBehaviour<T, I> holder) || ReferenceEquals(holder, this)
            ? TryRegister(id, alreadyReserved: true) : false;

    /// <summary>Frees an ID handed over that nothing will take up (the thing it named is gone).</summary>
    protected static void FreeHandedOverNetId(T id)
    {
        if (!indexToObject.ContainsKey(id))
            idAllocator.Release(id);
    }

    protected virtual void OnDestroy()
    {
        ReleaseNetId();
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
