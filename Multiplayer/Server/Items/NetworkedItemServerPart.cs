using Multiplayer.Shared.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Items;

/// <summary>
/// The server's half of one <see cref="NetworkedItem"/> (<see cref="INetworkedItemServerPart"/>): a
/// change to the server's own copy waiting for the item manager's dirty pass, and who holds it by
/// the server's record. Made by the item from the server (<see cref="Session.NetworkServer"/>).
/// </summary>
public sealed class NetworkedItemServerPart : INetworkedItemServerPart
{
    private readonly NetworkedItem item;

    internal NetworkedItemServerPart(NetworkedItem item)
    {
        this.item = item;
    }

    public bool ObservationPending { get; private set; }

    public string ObservationTrigger { get; private set; }

    public void NoteLocalObservation(string reason)
    {
        ObservationPending = true;
        ObservationTrigger = reason;
    }

    public void ClearObservation() => ObservationPending = false;

    public bool HeldByAnotherPlayer
    {
        get
        {
            byte hostPlayerId = NetworkRole.LocalPlayerId;
            return item.HostPossessorPlayerId != 0 && item.HostPossessorPlayerId != hostPlayerId;
        }
    }
}
