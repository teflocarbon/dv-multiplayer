using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Trains;

/// <summary>What became of a request to take or hand back a control's lease.</summary>
public enum LeaseOutcome
{
    /// <summary>Taken: the other players' cabs block the control.</summary>
    Granted,
    /// <summary>The player held it already; nothing changes.</summary>
    AlreadyHeld,
    /// <summary>Free, but the player is too far from the car to be at the control.</summary>
    OutOfRange,
    /// <summary>Another player holds it.</summary>
    HeldByOther,
    /// <summary>Handed back: everyone's cab frees the control.</summary>
    Released,
    /// <summary>Nobody held it; nothing changes.</summary>
    NotHeld,
    /// <summary>Another player holds it, so this player can't hand it back.</summary>
    NotOwner,
}

/// <summary>
/// Who holds each of a car's control leases, as the host keeps them. A lease keeps a lever being
/// held, or the whistle rope, from being fought over: while one player holds it, nobody else may
/// write its port. A control that's only turned or clicked never takes one (a VR valve, a cab
/// window), so a port nobody holds is anyone's to write.
/// </summary>
public sealed class ControlLeases<TPlayer> where TPlayer : class
{
    private readonly Dictionary<uint, TPlayer> holders = new();

    /// <summary>How many of the car's controls are held.</summary>
    public int Count => holders.Count;

    /// <summary>Who holds the lease on a control's port, if anyone.</summary>
    public TPlayer HolderOf(uint portNetId) => holders.TryGetValue(portNetId, out TPlayer holder) ? holder : null;

    public bool Holds(TPlayer player, uint portNetId) =>
        player != null && holders.TryGetValue(portNetId, out TPlayer holder) && holder == player;

    /// <summary>A player taking hold of a control; <paramref name="inRange"/>: whether they're at the car.</summary>
    public LeaseOutcome Take(uint portNetId, TPlayer player, bool inRange)
    {
        TPlayer holder = HolderOf(portNetId);
        if (holder == player)
            return LeaseOutcome.AlreadyHeld;
        if (holder != null)
            return LeaseOutcome.HeldByOther;
        if (!inRange)
            return LeaseOutcome.OutOfRange;
        holders[portNetId] = player;
        return LeaseOutcome.Granted;
    }

    /// <summary>A player letting go of a control.</summary>
    public LeaseOutcome Release(uint portNetId, TPlayer player)
    {
        TPlayer holder = HolderOf(portNetId);
        if (holder == null)
            return LeaseOutcome.NotHeld;
        if (holder != player)
            return LeaseOutcome.NotOwner;
        holders.Remove(portNetId);
        return LeaseOutcome.Released;
    }

    /// <summary>Whether a player may write these ports: nobody else holds any of them.</summary>
    public bool MayWrite(TPlayer player, IEnumerable<uint> portNetIds) =>
        player != null && portNetIds != null &&
        portNetIds.All(portNetId => HolderOf(portNetId) is not { } holder || holder == player);

    /// <summary>A player who left: every lease they held is freed. Returns those controls' ports.</summary>
    public List<uint> ReleaseAll(TPlayer player)
    {
        List<uint> released = holders.Where(kvp => kvp.Value == player).Select(kvp => kvp.Key).ToList();
        foreach (uint portNetId in released)
            holders.Remove(portNetId);
        return released;
    }
}
