using LiteNetLib;
using Multiplayer.Core.Shops;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Shops;
using Multiplayer.Shared.Transport;

namespace Multiplayer.Server.Session;

// NetworkServer's shops: the shelf seed and the shop check at join (see docs/design/shops.md).
public partial class NetworkServer
{
    // This session's shelf seed: every game lays the shops' shelves out from it (ShopShelfLayout).
    private int shopShelfSeed;

    // Picked once the world has loaded, so a dedicated host has it too. The host's own world is laid
    // out here, a client's when it joins (SendShopLayout).
    private void InitialiseShopShelves()
    {
        shopShelfSeed = new System.Random().Next();
        Log($"Shops: {ShopCatalogue.Compute()}, shelf seed {shopShelfSeed}");
        ShopShelfLayout.Apply(shopShelfSeed);
    }

    public void SendShopLayout(ITransportPeer peer) =>
        SendPacket(peer, new ClientboundShopLayoutPacket { Seed = shopShelfSeed }, DeliveryMethod.ReliableOrdered);

    // Shop requests name shops and items by index, so a client whose shops differ (a mod adding shop
    // items on one side only) would buy the wrong thing. Refused like a different railway.
    private bool CheckShopCatalogue(ServerPlayer player)
    {
        ShopCatalogueFingerprint hostShops = ShopCatalogue.Compute();
        string mismatch = hostShops.DescribeMismatch(player.ShopCatalogue);
        if (mismatch == null)
        {
            Log($"Player {player.Username}'s shops match: {hostShops}");
            return true;
        }

        LogError($"Refusing {player.Username}: shops {player.ShopCatalogue} don't match the host's {hostShops}");
        KickPlayer(player, mismatch);
        return false;
    }
}
