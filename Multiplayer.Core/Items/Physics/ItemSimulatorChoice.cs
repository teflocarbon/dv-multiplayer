using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Items.Physics;

/// <summary>Who could simulate an item under a lease: a player, or the server itself.</summary>
public readonly struct SimulatorCandidate
{
    public SimulatorCandidate(byte id, bool hasItem, float distanceSquared, bool inParentCar = false,
        bool hostWorld = false, bool serverItself = false)
    {
        Id = id;
        HasItem = hasItem;
        DistanceSquared = distanceSquared;
        InParentCar = inParentCar;
        HostWorld = hostWorld;
        ServerItself = serverItself;
    }

    public byte Id { get; }
    /// <summary>Its game has the item to simulate: projected and acknowledged, or the host's own object, active where its world is loaded.</summary>
    public bool HasItem { get; }
    /// <summary>From the item, squared; the server itself is where the item is.</summary>
    public float DistanceSquared { get; }
    /// <summary>Riding the car the item is in (a train wake).</summary>
    public bool InParentCar { get; }
    /// <summary>The host's own world: its player, or the server itself on a dedicated host.</summary>
    public bool HostWorld { get; }
    /// <summary>The server, with no player of its own (a dedicated host): it has no position.</summary>
    public bool ServerItself { get; }
}

/// <summary>
/// Who takes a lease another simulator gave up, or a train wake (docs/design/items/physics.md,
/// "Who simulates"). A dedicated host takes them like a player host's player does (OI-PHY-07, F32):
/// before, candidates were players only, and a thrown item froze in mid-air when its thrower left.
/// </summary>
public static class ItemSimulatorChoice
{
    /// <summary>
    /// A lease handed on (its simulator left, or its samples stopped): the host's own world first,
    /// as the authority with nothing in between, then the nearest player in range who has the item.
    /// </summary>
    public static bool ForTransfer(IEnumerable<SimulatorCandidate> candidates, byte previous,
        float maximumDistance, out byte chosen) =>
        Pick(Eligible(candidates, maximumDistance).Where(c => c.ServerItself || c.Id != previous)
            .OrderByDescending(c => c.HostWorld)
            .ThenBy(c => c.DistanceSquared), out chosen);

    /// <summary>
    /// A train wake: the player whose act woke it, then a rider of its car, then the nearest player
    /// in range who has the item; the server itself last, so a player there sees it first-hand.
    /// </summary>
    public static bool ForWake(IEnumerable<SimulatorCandidate> candidates, byte preferred,
        float maximumDistance, out byte chosen) =>
        Pick(Eligible(candidates, maximumDistance)
            .OrderByDescending(c => !c.ServerItself && c.Id == preferred)
            .ThenByDescending(c => c.InParentCar)
            .ThenBy(c => c.ServerItself)
            .ThenBy(c => c.DistanceSquared), out chosen);

    /// <summary>
    /// The players in range of a wake who don't have the item yet, nearest first: the ones to send
    /// it to, so a wake nobody can simulate yet finds a simulator (OI-PHY-12).
    /// </summary>
    public static IEnumerable<byte> Lacking(IEnumerable<SimulatorCandidate> candidates, float maximumDistance) =>
        (candidates ?? Enumerable.Empty<SimulatorCandidate>())
        .Where(c => !c.ServerItself && !c.HasItem && InRange(c, maximumDistance))
        .OrderByDescending(c => c.InParentCar).ThenBy(c => c.DistanceSquared).Select(c => c.Id);

    private static IEnumerable<SimulatorCandidate> Eligible(IEnumerable<SimulatorCandidate> candidates,
        float maximumDistance) =>
        (candidates ?? Enumerable.Empty<SimulatorCandidate>()).Where(c => c.HasItem && InRange(c, maximumDistance));

    private static bool InRange(SimulatorCandidate c, float maximumDistance) =>
        c.ServerItself || c.InParentCar || c.DistanceSquared <= maximumDistance * maximumDistance;

    private static bool Pick(IEnumerable<SimulatorCandidate> ordered, out byte chosen)
    {
        foreach (SimulatorCandidate c in ordered)
        {
            chosen = c.Id;
            return true;
        }
        chosen = 0;
        return false;
    }
}
