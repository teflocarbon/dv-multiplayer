using System;

namespace Multiplayer.Core.Items.LostAndFound;

public enum LostItemReason : byte
{
    OwnerDistance,
    InventoryOverflow,
    ContainerCleared,
    GadgetDestinationMissing,
    PurchasedDeliveryFallback,
    SaveRecovery,
    /// <summary>A player left carrying someone else's item: it goes to its owner (owned-items.md, "Leaving").</summary>
    BorrowerLeft,
    /// <summary>Its owner retrieved it from their owned-items list (owned-items.md, "Retrieval").</summary>
    RetrievedByOwner
}

public sealed class LostItemRecord
{
    public Guid PersistentItemId { get; set; }
    public uint Handle { get; set; }
    public ushort NetId { get; set; }
    public uint Revision { get; set; }
    public byte OwnerPlayerId { get; set; }
    public string OwnerIdentity { get; set; } = string.Empty;
    public string PrefabName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AuthoredItemKey { get; set; } = string.Empty;
    public byte InventoryClaimPlayerId { get; set; }
    public int InventoryClaimSlot { get; set; } = -1;
    public byte InventoryClaimFlags { get; set; }
    public LostItemReason Reason { get; set; }
    public long LostUtcTicks { get; set; }

    public LostItemRecord Clone() => (LostItemRecord)MemberwiseClone();
}
