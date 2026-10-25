using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Shared.Items.Physics;

/// <summary>
/// The game simulating a train item reports the impacts that could wake items settled in its car
/// (docs/design/items/train-wake.md). The server's train-wake manager (TrainItemWakeServer) takes
/// the reports and decides whether the target wakes.
/// </summary>
internal static class TrainItemImpactWitness
{
    // The train-wake manager's thresholds for an impact worth waking items for; the simulator
    // checks them before it reports one, and the host again when it takes the report.
    internal const float RelativeSpeedThreshold = 1.25f;
    internal const float ImpulseThreshold = 0.5f;
    private static uint nextWitnessId;

    /// <summary>
    /// The game simulating a train item saw it hit another item settled in the same car: it
    /// reports the impact to the host, whose train-wake manager decides whether the target wakes.
    /// </summary>
    internal static void Observe(NetworkedItemManager manager, NetworkedItem source, NetworkedItem target,
        Collision collision)
    {
        if (source == null || target == null || source == target || collision == null ||
            NetworkLifecycle.Instance == null || manager.Spatial == null ||
            !manager.Spatial.TryGetActiveLeaseIdentity(source.NetId, out uint sourceRevision,
                out uint sourceEpoch, out byte simulator, out ItemSpatialStateData sourceState) ||
            sourceState.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            sourceState.WorldParentNetId == 0 ||
            !manager.Spatial.TryGetCommitted(target.NetId, out ItemSpatialStateData targetState) ||
            targetState.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            targetState.WorldParentNetId != sourceState.WorldParentNetId)
            return;

        byte localPlayerId = NetworkRole.LocalPlayerId;
        if (simulator != localPlayerId)
            return;
        Vector3 relativeVelocity = collision.relativeVelocity;
        Vector3 impulse = collision.impulse;
        if (relativeVelocity.magnitude < RelativeSpeedThreshold &&
            impulse.magnitude < ImpulseThreshold)
            return;
        Vector3 contact = collision.contactCount > 0 ? collision.GetContact(0).point :
            target.transform.position;
        ServerboundItemTrainWakeWitnessPacket packet = new()
        {
            WitnessId = unchecked(++nextWitnessId),
            SourceItemNetId = source.NetId,
            SourceAuthorityRevision = sourceRevision,
            SourceSimulationEpoch = sourceEpoch,
            TargetItemNetId = target.NetId,
            TargetAuthorityRevision = targetState.AuthorityRevision,
            TrainCarNetId = sourceState.WorldParentNetId,
            SourceTick = NetworkLifecycle.Instance.Tick,
            RelativeVelocity = relativeVelocity,
            Impulse = impulse,
            AbsoluteContactPoint = contact - WorldMover.currentMove
        };
        // The server's own game witnessed it: straight to the server. A remote client reports it.
        if (manager.ServerPart is { } server)
            server.ReceiveLocalTrainWakeWitness(packet);
        else
            ItemClient.Current.SendItemTrainWakeWitness(packet);
    }
}
