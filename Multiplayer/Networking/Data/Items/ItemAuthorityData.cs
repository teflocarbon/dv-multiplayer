using System;

namespace Multiplayer.Networking.Data.Items;

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
    SpatialSettlement
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
