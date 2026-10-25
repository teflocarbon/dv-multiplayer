using System;

namespace Multiplayer.Shared.Protocol.Data.Items;

public enum ItemPlacementKind : byte
{
    World,
    PlayerHand,
    PlayerInventory,
    Container,
    Attached,
    LostAndFound,
    Installed,
    Destroyed,
    TrainInterior,
    StaticParent,
    SnappedAttachment,
    /// <summary>In a live tool's magazine slot (containers.md, "Ammo in a magazine").</summary>
    Magazine
}

public enum ItemWorldParentKind : byte
{
    World,
    TrainInterior,
    StaticParent,
    /// <summary>A gadget's snap point (a hanger, a mount): <c>WorldParentNetId</c> is its gadget item's, <c>WorldParentKey</c> the server's stable key.</summary>
    SnapPoint
}

public enum ItemTransitionReason : byte
{
    Unknown,
    InitialRegistration,
    HostLocalState,
    ClientState,
    OwnerRecall,
    JobBookletSummon,
    FullSync,
    LostAndFoundCollection,
    LostAndFoundRetrieval,
    ContainerDeposit,
    InterestRetirement,
    AuthoredProjectionBinding,
    PersistenceRestore,
    SpatialSettlement,
    // Host re-sending canonical state so client copies that drifted heal (see Convergence).
    Convergence,
    // A client's object-state change to an item it doesn't hold, made by its own player working
    // the item where it lies (ItemObjectStateAuthority). Without it the host ignores such changes.
    ClientInteraction,
    // The host throwing a job overview back out of the office bin (Server/Jobs/JobReroll).
    JobRerollRefused,
    // The server's gadget code committing an install or a removal vanilla made (Server/Gadgets).
    GadgetInstalled,
    GadgetRemoved,
    // A leaving player's inventory item kept as data (Server/Items/Inventory/PlayerItems; D12): its
    // object goes from every game, its home stays.
    WentCold
}

[Flags]
public enum ItemInventoryClaimFlags : byte
{
    None = 0,
    Reserved = 1,
    Locked = 2,
    Dropped = 4,
    Stolen = 8
}
