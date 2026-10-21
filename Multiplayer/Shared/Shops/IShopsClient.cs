using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Shops;

/// <summary>
/// The shop requests a player at this keyboard sends. <c>NetworkClient</c> implements it
/// (<c>Client/Session/NetworkClient.Shops.cs</c>), and on the host its player's session
/// (<c>LocalPlayerSession.Shops.cs</c>), which hands them to the server in-process: the host's player
/// shops like everyone else (docs/design/shops.md).
/// </summary>
public interface IShopsClient
{
    void SendShopScan(uint requestId, byte shopIndex, byte moduleIndex);
    void SendShopDeposit(uint requestId, byte shopIndex, ushort moneyNetId);
    void SendShopBuy(uint requestId, byte shopIndex, uint revision);
    void SendShopCancel(uint requestId, byte shopIndex);
}

public static class ShopsClient
{
    /// <summary>This game's player session, or null when there is none (a dedicated host).</summary>
    public static IShopsClient Current => NetworkLifecycle.Instance?.LocalSession as IShopsClient;
}
