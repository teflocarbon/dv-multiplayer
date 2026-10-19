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
    SnappedAttachment
}

public enum ItemWorldParentKind : byte
{
    World,
    TrainInterior,
    StaticParent
}

public enum ItemTransitionReason : byte
{
    Unknown,
    InitialRegistration,
    HostLocalState,
    ClientState,
    // TEMPORARY compatibility reason for unconverted DV item producers. Remove with the adoption
    // protocol once all creation paths are explicit host operations.
    ClientAdoption,
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
    ClientInteraction
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
