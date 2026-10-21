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
    void SendShopScan(byte shopIndex, byte moduleIndex);
    void SendShopDeposit(byte shopIndex, ushort moneyNetId);
    void SendShopBuy(byte shopIndex, uint revision);
    void SendShopCancel(byte shopIndex);
    /// <summary>The shelf item this player's scanner points at now (module 255: none).</summary>
    void SendShopScannerAim(ushort scannerNetId, byte shopIndex, byte moduleIndex);
}

public static class ShopsClient
{
    /// <summary>This game's player session, or null when there is none (a dedicated host).</summary>
    public static IShopsClient Current => NetworkLifecycle.Instance?.LocalSession as IShopsClient;
}
