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
    Destroyed
}

public enum ItemTransitionReason : byte
{
    Unknown,
    InitialRegistration,
    HostLocalState,
    ClientState,
    ClientAdoption,
    OwnerRecall,
    JobBookletSummon,
    FullSync,
    LostAndFoundCollection,
    LostAndFoundRetrieval
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
