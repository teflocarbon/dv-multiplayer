using System.Collections.Generic;

namespace Multiplayer.Core.Jobs;

/// <summary>
/// Tracks which authoritative physical booklet copy is issued to each player for one job.
/// The job may own many copies, but a player has at most one currently issued copy.
/// </summary>
public sealed class JobBookletIssuanceIndex
{
    private readonly Dictionary<byte, ushort> playerToItem = new();
    private readonly Dictionary<ushort, byte> itemToPlayer = new();

    public int Count => playerToItem.Count;

    public bool Assign(byte playerId, ushort itemNetId)
    {
        if (playerId == 0 || itemNetId == 0)
            return false;

        if (playerToItem.TryGetValue(playerId, out ushort previousItem))
            itemToPlayer.Remove(previousItem);
        if (itemToPlayer.TryGetValue(itemNetId, out byte previousPlayer))
            playerToItem.Remove(previousPlayer);

        playerToItem[playerId] = itemNetId;
        itemToPlayer[itemNetId] = playerId;
        return true;
    }

    public bool TryGetItem(byte playerId, out ushort itemNetId)
    {
        itemNetId = 0;
        return playerId != 0 && playerToItem.TryGetValue(playerId, out itemNetId);
    }

    public bool TryGetPlayer(ushort itemNetId, out byte playerId)
    {
        playerId = 0;
        return itemNetId != 0 && itemToPlayer.TryGetValue(itemNetId, out playerId);
    }

    public bool RemoveItem(ushort itemNetId)
    {
        if (!itemToPlayer.TryGetValue(itemNetId, out byte playerId))
            return false;
        itemToPlayer.Remove(itemNetId);
        playerToItem.Remove(playerId);
        return true;
    }

    public void Clear()
    {
        playerToItem.Clear();
        itemToPlayer.Clear();
    }
}
