using System;
using Multiplayer.Shared.Utils;

namespace Multiplayer.Shared.Trains;

/// <summary>
/// A car's cargo fire is the host's, like its explosion (<see cref="ExplosionSync"/>). Each game
/// used to run its own: a leak lit with a lighter, ground fire spreading to a car, a car catching
/// on its own (a random roll every frame) or going out all happened on one screen only, so a car
/// could burn for one player and never for anyone else. A client's lit car couldn't explode either:
/// its countdown is blocked, and the host's car wasn't burning.
///
/// Now the host sends each car's fire state (burning, about to explode) when it changes
/// (Server/Trains/CargoFireServerPatch.cs), and clients hold their copy to it
/// (Client/Trains/CargoFireClientPatch.cs). A client's lighter or flare at a car asks the host,
/// which lights its own copy if the player is close enough. Explosions themselves stay with
/// ExplosionSync. This holds what both sides name a fire by.
/// </summary>
public static class CargoFireSync
{
    [Flags]
    public enum FireState : byte { None = 0, Ignited = 1, AboutToExplode = 2 }

    internal static FireState State(CargoReactionBase reaction) =>
        (reaction.isIgnited ? FireState.Ignited : FireState.None) | (reaction.aboutToExplode ? FireState.AboutToExplode : FireState.None);

    /// <summary>A reaction's car and its index among the car's cargo reactions.</summary>
    internal static bool TryLocate(CargoReactionBase reaction, out NetworkedTrainCar networked, out byte index)
    {
        networked = null;
        index = 0;
        TrainCar car = ExplosionSync.ReactionCar(reaction);
        if (car == null || !car.TryNetworked(out networked))
            return false;
        int i = Array.IndexOf(ExplosionSync.CargoReactions(car), reaction);
        if (i < 0 || i > byte.MaxValue)
            return false;
        index = (byte)i;
        return true;
    }
}
