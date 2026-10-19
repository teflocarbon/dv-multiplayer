namespace Multiplayer.Core.Trains;

// Reasons recorded in a ChangeCause (Multiplayer.Core.Diagnostics) for train changes. They go on
// the wire as a byte: append new values, never renumber.

/// <summary>Why a coupling came apart.</summary>
public enum UncoupleReason : byte
{
    /// <summary>Nothing recognised did it; the cause's detail names the calling methods.</summary>
    Unknown = 0,
    /// <summary>A player uncoupled it by hand (chain, coupling menu or cab).</summary>
    PlayerCommand = 1,
    /// <summary>A player's remote controller or in-cab decoupler.</summary>
    Remote = 2,
    /// <summary>One of the cars was deleted or despawned; the detail says why.</summary>
    CarRemoved = 3,
    /// <summary>One of the cars was rerailed, which takes it off its train.</summary>
    CarRerailed = 4,
    /// <summary>The joint broke with both cars on the rails (300 MN, so something went badly wrong).</summary>
    JointBroke = 5,
    /// <summary>The joint broke with a car derailed, where vanilla weakens it to 1.35 MN.</summary>
    JointBrokeDerailed = 6,
    /// <summary>The host undid a command it couldn't apply.</summary>
    HostRolledBack = 7,
    /// <summary>Client: undoing its own couple because the host refused it; the detail says why.</summary>
    CoupleRefused = 8,
    /// <summary>Client: applying the host's topology, which doesn't have this coupling.</summary>
    FollowingHost = 9
}

/// <summary>Why a car derailed.</summary>
public enum DerailReason : byte
{
    Unknown = 0,
    /// <summary>Vanilla's stress build-up (too fast for a curve or a junction).</summary>
    Stress = 1,
    /// <summary>A cargo explosion nearby.</summary>
    Explosion = 2,
    /// <summary>A bogie ran off the end of the track.</summary>
    EndOfTrack = 3,
    /// <summary>A bogie's track went missing under it.</summary>
    MissingTrack = 4,
    /// <summary>The car was saved derailed and loaded that way.</summary>
    LoadedDerailed = 5,
    /// <summary>A safety derailer.</summary>
    SafetyDerailer = 6,
    /// <summary>A player's comms radio (the sandbox derail mode).</summary>
    CommsRadio = 7,
    /// <summary>Client: following the host's derail.</summary>
    FollowingHost = 8
}

/// <summary>Why a car was removed from the world.</summary>
public enum CarRemovalReason : byte
{
    Unknown = 0,
    /// <summary>A player deleted it (comms radio or the delete menu).</summary>
    PlayerDeleted = 1,
    /// <summary>A player sent it back to its home garage.</summary>
    ReturnedToGarage = 2,
    /// <summary>Vanilla's cleanup of unused cars far from players.</summary>
    Despawned = 3,
    /// <summary>The job generator cleared cars from tracks it needed.</summary>
    JobGeneration = 4,
    /// <summary>The loco restoration swapped cars.</summary>
    LocoRestoration = 5,
    /// <summary>Removed while loading a save.</summary>
    SaveLoad = 6,
    /// <summary>A player binned the overview of the offered job it belonged to, to replace the job.</summary>
    JobRerolled = 7
}
