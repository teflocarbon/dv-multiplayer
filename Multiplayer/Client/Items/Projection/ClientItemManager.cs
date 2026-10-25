using System;
using System.Collections.Generic;
using Multiplayer.Client.Items.Inventory;
using Multiplayer.Client.Items.LiveState;
using Multiplayer.Client.Items.Physics;
using Multiplayer.Client.Items.WorldItems;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Items.Projection;

/// <summary>
/// The remote client's part of <see cref="NetworkedItemManager"/>: the host's snapshots as this game
/// receives them (deferred world projections, job documents, Lost and Found tombstones), the
/// projections of scene-authored items (AuthoredProjections), unbound local items (kept dormant
/// or destroyed), and the client's fall-rescue report (FallRescueClient). The Shared manager
/// creates it on a remote client only and drives it (<see cref="IItemManagerClientPart"/>); client
/// code reaches it as <see cref="Instance"/>.
/// </summary>
internal sealed partial class ClientItemManager : IItemManagerClientPart, IInventoryRowHolder
{
    // Exact scene-authored Unity objects remain dormant while outside host interest. They are
    // rebound only by stable authored key; dynamic projections are never pooled or reused.
    internal readonly HashSet<NetworkedItem> DormantAuthoredClientProjections = new();
    private readonly Queue<NetworkedItem> PendingUnboundClientItems = new();
    private readonly HashSet<NetworkedItem> PendingUnboundClientItemSet = new();
    private readonly Dictionary<ushort, ItemUpdate> PendingSpecialItemSnapshots = new();
    private readonly Dictionary<ushort, PendingWorldProjection> PendingWorldProjections = new();
    private readonly HashSet<ushort> ClientLostAndFoundTombstones = new();
    private bool ClientInitialised = false;
    private readonly NetworkedItemManager shell;

    private sealed class PendingWorldProjection
    {
        public ItemUpdate Create;
        public readonly List<ItemUpdate> Updates = new(8);
    }

    internal ClientItemManager(NetworkedItemManager shell)
    {
        this.shell = shell;
        Spatial = new ClientItemSpatial(shell.Spatial);
        Authored = new AuthoredProjections(this, shell);
    }

    /// <summary>Reports items near this player that sank below its terrain.</summary>
    internal FallRescueClient FallRescue { get; } = new();

    /// <summary>The host's scene-authored items bound to this game's copies.</summary>
    internal AuthoredProjections Authored { get; }

    /// <summary>This game's side of the Shared spatial manager's leases.</summary>
    internal ClientItemSpatial Spatial { get; }

    /// <summary>The client's item manager, while there is one.</summary>
    internal static ClientItemManager Instance => NetworkedItemManager.Instance?.ClientPart as ClientItemManager;

    public void Update()
    {
        Spatial.Update();
        if (!ClientInitialised)
            return;

        ProcessPendingWorldProjections();

        int remainingBudget = 16;
        while (remainingBudget-- > 0 && PendingUnboundClientItems.Count > 0)
        {
            NetworkedItem item = PendingUnboundClientItems.Dequeue();
            PendingUnboundClientItemSet.Remove(item);
            if (item == null || item.NetId != 0)
                continue;

            if (item.IsSceneAuthored)
            {
                if (!DormantAuthoredClientProjections.Contains(item))
                {
                    NetworkedItemManager.TraceItem("item.unbound-authored-item-dormant", item,
                        new() { ["reason"] = "late-authored-activation" });
                    RetireClientProjection(item, "late-authored-activation");
                }
                else
                {
                    item.Client()?.GateAsSceneObjectAwaitingHostCreate();
                    item.gameObject.SetActive(false);
                }
            }
            else
            {
                NetworkedItemManager.TraceItem("item.unbound-client-object-destroyed", item,
                    new() { ["reason"] = "not-host-authorized" });
                RetireClientProjection(item, "not-host-authorized");
            }
        }
    }

    public void Tick(uint tick) => FallRescue.Run();

    public void DetachAuthoredProjectionForPossession(NetworkedItem item, ItemUpdate snapshot) =>
        Authored.DetachForPossession(item, snapshot);

    public void Clear()
    {
        Spatial.Clear();
        Authored.Clear();
        InventoryRows.Clear();
        RetireStandIns();
    }

    public bool IsLostAndFoundTombstoned(ushort itemNetId) =>
        itemNetId != 0 && ClientLostAndFoundTombstones.Contains(itemNetId);

    /// <summary>The host's item snapshots, from the bulk and single item packets.</summary>
    internal void ReceiveSnapshots(List<ItemUpdate> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            try
            {
                if (snapshot == null || snapshot.UpdateType == ItemUpdate.ItemUpdateType.None)
                {
                    Multiplayer.LogError($"NetworkedItemManager.ProcessReceived() Invalid Update Type: {snapshot?.UpdateType}, ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
                    continue;
                }

                ProcessReceivedAsClient(snapshot);
            }
            catch (Exception ex)
            {
                Multiplayer.LogError($"NetworkedItemManager.ProcessReceived() Error! {ex.Message}\r\n{ex.StackTrace}");
            }
        }
    }
}
