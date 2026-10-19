using System;

namespace Multiplayer.Shared.Protocol.Data.World;

public enum PitStopStationInteractionType : byte
{
    Reject,
    LeverState,
    ResourceUpdate,

    CarSelectorGrab,
    CarSelectorUngrab,
    CarSelection,

    FaucetGrab,
    FaucetUngrab,
    FaucetPosition,
}
