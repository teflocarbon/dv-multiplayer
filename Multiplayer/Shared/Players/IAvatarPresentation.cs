namespace Multiplayer.Shared.Players;

/// <summary>
///     How another player's avatar looks: its name tag, character model and the ping it shows.
///     <see cref="PlayerAvatars"/> keeps the avatars (their presence, which the server needs too);
///     the client side dresses them (<c>Client/Players/AvatarPresentation</c>).
/// </summary>
public interface IAvatarPresentation
{
    void AddNameTag(NetworkedPlayer player);
    void ChangeModel(NetworkedPlayer player, string characterId);
    void SetPing(NetworkedPlayer player, int ping);
}
