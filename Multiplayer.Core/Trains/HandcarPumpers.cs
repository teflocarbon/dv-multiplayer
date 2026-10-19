using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Trains;

/// <summary>
/// The players pumping a handcar's handle from other games, as the host keeps them. Vanilla's
/// HandcarDrive takes one handle: one position and whether it's held. The host's own player still
/// works the car's ports; every other player's handle is kept here, and each pumper's stroke pushes
/// the crank the way vanilla's one handle does. Their torques add up, to at most
/// <see cref="TORQUE_CAP_PLAYERS"/> players' worth: two players pumping in time go faster than one,
/// and one pumping against the other slows the car.
/// </summary>
public sealed class HandcarPumpers<TPlayer>
{
    /// <summary>The most the pumpers push with together, in multiples of one player's most.</summary>
    public const float TORQUE_CAP_PLAYERS = 2f;

    // Vanilla's: a handle pushed against the crank's motion acts harder.
    private const float ACTING_AGAINST_MULTIPLIER = 1.5f;

    private struct Handle
    {
        // The handle port's value, 0 to 1 (vanilla's BAR_EXT_IN).
        public float Bar;
        public bool Engaged;
    }

    private readonly Dictionary<TPlayer, Handle> handles = new();
    private readonly List<TPlayer> removing = new();

    /// <summary>Players holding the handle now.</summary>
    public int EngagedCount { get; private set; }

    /// <summary>A player's handle position, from their game's handle port (0 to 1).</summary>
    public void SetBar(TPlayer player, float bar)
    {
        handles.TryGetValue(player, out Handle handle);
        handle.Bar = bar;
        handles[player] = handle;
    }

    /// <summary>Whether a player is holding the handle, from their game's engaged port.</summary>
    public void SetEngaged(TPlayer player, bool engaged)
    {
        handles.TryGetValue(player, out Handle handle);
        if (handle.Engaged == engaged && handles.ContainsKey(player))
            return;
        handle.Engaged = engaged;
        handles[player] = handle;
        Recount();
    }

    /// <summary>A player who left, or went out of reach of the car.</summary>
    public void Remove(TPlayer player)
    {
        if (handles.Remove(player))
            Recount();
    }

    public void RemoveWhere(Func<TPlayer, bool> predicate)
    {
        removing.Clear();
        foreach (TPlayer player in handles.Keys)
            if (predicate(player))
                removing.Add(player);
        if (removing.Count == 0)
            return;
        foreach (TPlayer player in removing)
            handles.Remove(player);
        removing.Clear();
        Recount();
    }

    /// <summary>
    /// The crank's torque with every pumper's added to the host's own player's (which vanilla
    /// already worked out), capped. Call only with <see cref="EngagedCount"/> above 0.
    /// </summary>
    /// <param name="localTorque">Vanilla's torque from the host's own handle.</param>
    /// <param name="localEngaged">Whether the host's own player is holding the handle.</param>
    /// <param name="crankPosition">The crank, -1 to 1.</param>
    /// <param name="direction">The crank's direction of travel, 1 or -1.</param>
    /// <param name="crankRate">The crank's speed from the wheels, signed as vanilla's.</param>
    /// <param name="maxTorque">One handle's most (HandcarDrive.maxTorqueProduction).</param>
    /// <param name="positionDiffToTorque">How far the handle is from the crank, 0 to 1, to the
    /// fraction of the most torque (HandcarDrive.positionDiffToTorque).</param>
    /// <param name="firstHandlePosition">A pumper's handle, -1 to 1, for vanilla's engaged-handle
    /// output when the host's own player isn't holding it.</param>
    public float CombinedTorque(float localTorque, bool localEngaged, float crankPosition, int direction,
        float crankRate, float maxTorque, Func<float, float> positionDiffToTorque, out float firstHandlePosition)
    {
        float total = localEngaged ? localTorque : 0f;
        firstHandlePosition = 0f;
        bool first = true;
        foreach (Handle handle in handles.Values)
        {
            if (!handle.Engaged)
                continue;
            float position = handle.Bar * 2f - 1f;
            if (first)
            {
                firstHandlePosition = position;
                first = false;
            }
            total += StrokeTorque(position, crankPosition, direction, crankRate, maxTorque, positionDiffToTorque);
        }
        float cap = TORQUE_CAP_PLAYERS * maxTorque;
        return Math.Max(-cap, Math.Min(cap, total));
    }

    /// <summary>One held handle's torque on the crank: vanilla HandcarDrive.Tick's.</summary>
    public static float StrokeTorque(float handlePosition, float crankPosition, int direction, float crankRate,
        float maxTorque, Func<float, float> positionDiffToTorque)
    {
        float diff = (handlePosition - crankPosition) / 2f;
        float torque = positionDiffToTorque(Math.Abs(diff)) * maxTorque * Sign(diff) * direction;
        if (Sign(torque) != Sign(crankRate))
            torque *= ACTING_AGAINST_MULTIPLIER;
        return torque;
    }

    /// <summary>Unity's Mathf.Sign: 0 counts as positive.</summary>
    public static float Sign(float value) => value >= 0f ? 1f : -1f;

    private void Recount()
    {
        int count = 0;
        foreach (Handle handle in handles.Values)
            if (handle.Engaged)
                count++;
        EngagedCount = count;
    }
}
