using System.Collections.Generic;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Physics;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Items.TrainWake;
using Multiplayer.Server.Items.WorldItems;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Items.Projection;

/// <summary>
/// The server's part of <see cref="NetworkedItemManager"/>: which items each player is sent, what
/// the host's own changes send, convergence, and it holds the server's item parts: the leases
/// (ServerItemSpatial), the train wake, the fall rescue (FallRescueServer) and the office stock
/// (OfficeStockReplenishment); players' items are PlayerItems. The Shared manager creates it on the
/// server and drives it (<see cref="IItemManagerServerPart"/>) where this code ran before the split;
/// server code reaches it as <see cref="Instance"/>.
/// </summary>
internal sealed partial class ServerItemManager : IItemManagerServerPart
{
    // Destroys waiting for the next tick, and who they go to.
    private readonly List<ItemUpdate> DestroyedItems = new(64);
    private readonly Dictionary<ItemUpdate, HashSet<byte>> DestroyRecipients = new();
    // The host's interest index: world items by cell, and the items that aren't in one.
    private readonly Dictionary<WorldItemCellCoord, HashSet<NetworkedItem>> HostItemsByCell = new();
    private readonly Dictionary<NetworkedItem, WorldItemCellCoord> HostCellByItem = new();
    private readonly HashSet<NetworkedItem> HostNonSpatialItems = new();
    private readonly Dictionary<byte, HashSet<WorldItemCellCoord>> HostPlayerCells = new();
    private readonly OfficeStockReplenishment Replenishment;
    private readonly NetworkedItemManager shell;

    internal ServerItemManager(NetworkedItemManager shell)
    {
        this.shell = shell;
        HostSpatial = new ServerItemSpatial(shell.Spatial, this);
        TrainItemWake = new TrainItemWakeServer(shell.Spatial, HostSpatial);
        Replenishment = new OfficeStockReplenishment(this);
        FallRescue = new FallRescueServer(this);
        // A player host's own inventory is rows, as a client's is (D14).
        InventoryRowHolder.Host = HostInventory.Instance;
    }

    /// <summary>The host's side of the Shared spatial manager's leases.</summary>
    internal ServerItemSpatial HostSpatial { get; }
    internal TrainItemWakeServer TrainItemWake { get; }
    /// <summary>Puts items that fell through the world back where they were last safe.</summary>
    internal FallRescueServer FallRescue { get; }

    /// <summary>The server's item manager, while there is one.</summary>
    internal static ServerItemManager Instance => NetworkedItemManager.Instance?.ServerPart as ServerItemManager;

    /// <summary>The Shared manager this is the server's part of.</summary>
    internal NetworkedItemManager Shell => shell;

    public void Update()
    {
        TrainItemWake.Update();
        HostSpatial.Update();
        Replenishment.Update();
        PlayerItems.Update();
        PlayerItems.UpdateAlarms();
        PlayerItems.UpdateSounds();
    }

    public void Tick(uint tick)
    {
        LostAndFoundServer.HostTick(tick);
        ContainerServer.HostTick(tick);
        ItemSaveLoad.HostTick();
        ServerLedger.HostTick();
        UpdatePlayerItemLists();
        ProcessChanged(tick);
        RunConvergence();
        FallRescue.Run();
    }

    public void Clear()
    {
        LostAndFoundServer.Clear();
        VanillaStorage.Binning.Clear();
        Consumables.UseUp.Clear();
        DeletedCarItems.Clear();
        ContainerServer.Clear();
        TrainItemWake.Clear();
        HostSpatial.Clear();
        Replenishment.Clear();
        FallRescue.Clear();
        ItemSaveLoad.Clear();
        ServerLedger.Clear();
        PlayerItems.Clear();
        LiveItems.Clear();
    }

    public void ForgetRecord(ushort itemNetId) => LiveItems.Remove(itemNetId);

    public bool IsInstalled(ushort itemNetId) =>
        LiveItems.TryGet(itemNetId, out LiveItems.LiveItem record) && record.Placement == ItemPlacementKind.Installed;

    public void OnTrainItemRemoved(ushort itemNetId) => TrainItemWake.OnItemRemoved(itemNetId);

    public void ReceiveLocalTrainWakeWitness(ServerboundItemTrainWakeWitnessPacket packet)
    {
        ServerPlayer hostPlayer = HostItemActor.Player;
        if (hostPlayer != null)
            TrainItemWake.ReceiveImpactWitness(packet, hostPlayer);
        else if (HostItemActor.IsServer)
            TrainItemWake.ReceiveImpactWitness(packet, null, serverWitness: true);
    }

    public void AppendTrainWakeDebugState(ushort itemNetId, Dictionary<string, object> state) =>
        TrainItemWake.AppendDebugState(itemNetId, state);

    /// <summary>
    /// A player left: the host's leases they were simulating move on or settle, and their interest
    /// pins go (a player who takes their number next starts with none).
    /// </summary>
    internal void OnSpatialPlayerDisconnected(byte playerId)
    {
        HostSpatial.OnPlayerDisconnected(playerId);
        pinnedInterest.Remove(playerId);
    }

    /// <summary>A destroyed item: send its Destroy to the players who know it, at the next tick.</summary>
    internal void AddDirtyItemSnapshot(NetworkedItem netItem, ItemUpdate snapshot)
    {
        DestroyedItems.Add(snapshot);
        HashSet<byte> recipients = new();
        DestroyRecipients[snapshot] = recipients;
        UnregisterHostWorldItem(netItem);

        foreach (var player in NetworkServer.Instance.ServerPlayers)
        {
            if (player.KnownItems.ContainsKey(netItem))
            {
                recipients.Add(player.PlayerId);
                player.KnownItems.Remove(netItem);
            }

            player.AcknowledgedWorldItems.Remove(netItem.NetId);

            if (player.NearbyItems.ContainsKey(netItem))
                player.NearbyItems.Remove(netItem);
        }
    }
}
