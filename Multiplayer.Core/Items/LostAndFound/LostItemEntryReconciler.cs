using Multiplayer.Core.Items.LiveState;

namespace Multiplayer.Core.Items.LostAndFound;

public enum LostItemEntryStatus : byte
{
    /// <summary>The item exists and its authority still says Lost and Found.</summary>
    Stored,
    /// <summary>The item exists but has no authority record yet. Leave the entry alone.</summary>
    AwaitingAuthority,
    /// <summary>Another host system moved the item out of Lost and Found.</summary>
    PlacementChanged,
    /// <summary>
    /// The item no longer exists, e.g. a job booklet the job system destroyed when its job ended.
    /// Nothing is left to retrieve.
    /// </summary>
    ItemDestroyed
}

/// <summary>Host check of a Lost and Found entry against the item it points at.</summary>
public static class LostItemEntryReconciler
{
    /// <param name="itemPresent">The canonical Unity object for the entry's NetId still exists.</param>
    /// <param name="placement">The item's authority placement, or null with no authority record.</param>
    public static LostItemEntryStatus Classify(bool itemPresent, LivePlacement? placement)
    {
        // Retrieval revives the canonical object in place, so without it the entry can never be
        // retrieved, whatever an authority record still says.
        if (!itemPresent)
            return LostItemEntryStatus.ItemDestroyed;
        if (placement == null)
            return LostItemEntryStatus.AwaitingAuthority;
        return placement == LivePlacement.LostAndFound
            ? LostItemEntryStatus.Stored
            : LostItemEntryStatus.PlacementChanged;
    }
}
