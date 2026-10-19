using System.Collections.Generic;
using Multiplayer.Core.Items;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Items;

/// <summary>
/// The server's part of <see cref="NetworkedItemManager"/>: which items each player is sent, what
/// the host's own changes send, convergence, the host's fall rescue, adoption and recall, departing
/// players' items, and the replenishment of authored slots. The Shared manager creates it on the
/// server and drives it (<see cref="IItemManagerServerPart"/>) where this code ran before the split;
/// server code reaches it as <see cref="Instance"/>.
/// </summary>
internal sealed partial class ServerItemManager : IItemManagerServerPart
{
    // Destroys waiting for the next tick, and who they go to.
    private readonly List<ItemUpdateData> DestroyedItems = new(64);
    private readonly Dictionary<ItemUpdateData, HashSet<byte>> DestroyRecipients = new();
    // The host's interest index: world items by cell, and the items that aren't in one.
    private readonly Dictionary<WorldItemCellCoord, HashSet<NetworkedItem>> HostItemsByCell = new();
    private readonly Dictionary<NetworkedItem, WorldItemCellCoord> HostCellByItem = new();
    private readonly HashSet<NetworkedItem> HostNonSpatialItems = new();
    private readonly Dictionary<byte, HashSet<WorldItemCellCoord>> HostPlayerCells = new();
    // TEMPORARY: delete with TemporaryClientAdoptionCompatibility after all DV runtime item
    // producers have explicit host-authoritative operations.
    private readonly HostAdoptionRegistry TemporaryHostAdoptions = new();
    private readonly AuthoredWorldItemReplenishmentManager Replenishment;
    private readonly NetworkedItemManager shell;

    internal ServerItemManager(NetworkedItemManager shell)
    {
        this.shell = shell;
        Replenishment = new AuthoredWorldItemReplenishmentManager(this);
    }

    /// <summary>The server's item manager, while there is one.</summary>
    internal static ServerItemManager Instance => NetworkedItemManager.Instance?.ServerPart as ServerItemManager;

    /// <summary>The Shared manager this is the server's part of.</summary>
    internal NetworkedItemManager Shell => shell;

    public void Update() => Replenishment.Update();

    public void Tick(uint tick)
    {
        NetworkedLostAndFoundManager.HostTick(tick);
        NetworkedColdContainerManager.HostTick(tick);
        WorldItemPersistenceManager.HostTick();
        UpdatePlayerItemLists();
        ProcessChanged(tick);
        RunConvergence();
        RunFallRescue();
    }

    public void Clear()
    {
        NetworkedLostAndFoundManager.Clear();
        NetworkedColdContainerManager.Clear();
        Replenishment.Clear();
        WorldItemPersistenceManager.Clear();
        AuthoritativeItemRegistry.Clear();
    }

    public void ForgetRecord(ushort itemNetId) => AuthoritativeItemRegistry.Remove(itemNetId);

    public void AddDirtyItemSnapshot(NetworkedItem netItem, ItemUpdateData snapshot)
    {
        DestroyedItems.Add(snapshot);
        HashSet<byte> recipients = new();
        DestroyRecipients[snapshot] = recipients;
        UnregisterHostWorldItem(netItem);

        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers)
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
