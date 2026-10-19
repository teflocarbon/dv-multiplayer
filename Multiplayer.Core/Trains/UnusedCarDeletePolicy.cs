namespace Multiplayer.Core.Trains;

/// <summary>
/// When the host may let DV's unused-car deleter judge a car as far from every player. A player
/// still loading, or whose game hasn't sent a position yet, has no real position: counted at the
/// world origin, they make every car look abandoned, and with nobody else online the deleter
/// removed a whole world's trains a second after a player logged in. A dedicated host's parked
/// body isn't a player, so with nobody fully in the game there is no one to measure from.
/// </summary>
public static class UnusedCarDeletePolicy
{
    /// <param name="players">Players in the game, not counting a dedicated host's parked body.</param>
    /// <param name="placed">Of those, the ones that finished loading and have sent a position.</param>
    public static bool MayDelete(int players, int placed) => players > 0 && placed == players;
}
