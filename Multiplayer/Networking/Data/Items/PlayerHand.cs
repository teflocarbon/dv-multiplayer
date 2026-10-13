namespace Multiplayer.Networking.Data.Items;

/// <summary>
/// Canonical hand used for an item placement. None also represents desktop possession.
/// </summary>
public enum PlayerHand : byte
{
    None = 0,
    Left = 1,
    Right = 2
}

public static class PlayerHandRules
{
    public static bool IsDefined(PlayerHand hand) =>
        hand is PlayerHand.None or PlayerHand.Left or PlayerHand.Right;

    public static PlayerHand Normalize(PlayerHand hand) =>
        IsDefined(hand) ? hand : PlayerHand.None;
}
