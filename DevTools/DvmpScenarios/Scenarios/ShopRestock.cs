using System.Threading.Tasks;
using Multiplayer.Shared.Items;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A bought item put in a container must not restock the shop (docs/design/shops.md, gap 13).
    /// The client buys an item, so the stock drops by one. It then deposits that item in a toolbox
    /// and withdraws it again: the stock must stay down. Destroying the withdrawn copy must then
    /// bring the stock back, as binning it does. The server destroys a deposited item's GameObject,
    /// and vanilla's <c>ShopRestocker</c> restocks on any destroy, so the stock used to come back by
    /// one while the item still existed.
    /// </summary>
    [Scenario("shop-restock", "The client buys an item and puts it through a toolbox: the shop's stock stays down; destroying it then restocks")]
    [Budget(300)]
    public sealed class ShopRestock : Scenario<ShopRestock.Options>
    {
        public sealed class Options
        {
            /// <summary>The shelf item's name prefix.</summary>
            public string Item = "Lighter";
            /// <summary>The container's prefab.</summary>
            public string Container = "ItemContainerToolbox";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Places.Shop(pair, 2f);
            await pair.Host.Run(() => Shop.InGame.ResetRegisters());

            Shop.Bought b = await pair.Client.Run<Shop.Bought>(s => Shop.InGame.Buy(s, o.Item, false));
            Report.Note($"bought {b.Prefab} for ${b.Cost}: stock {b.Stock0} -> {b.Stock1}, net ID {string.Join(",", b.NetIds)}");
            Require.That(b.Done && b.NetIds.Length == 1 && b.NetIds[0] != 0, "the purchase went through with one item");
            ushort item = b.NetIds[0];
            await Task.Delay(2000);
            Shop.HostView before = await pair.Host.Run(() => Shop.InGame.HostView(b.Prefab, item, -1));
            Report.Note($"host stock after the purchase: {before.Stock}");
            Require.Equal(b.Stock1, before.Stock, "the host's stock after the purchase");

            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, "client"));
            string[] log = await pair.Client.Run<string[]>(s => Containers.InGame.RoundTrip(s, box, item), 120);
            foreach (string line in log) Report.Note(line);
            await Task.Delay(2000);

            Shop.HostView after = await pair.Host.Run(() => Shop.InGame.HostView(b.Prefab, 0, -1));
            Report.Note($"host stock after the deposit and withdrawal: {after.Stock}");
            Expect.Equal(before.Stock, after.Stock, "the host's stock after the item went through a container");

            // The withdrawn copy is the item now: really destroying it must still restock.
            string withdrew = System.Array.Find(log, l => l.StartsWith("withdrew: "));
            Require.That(withdrew != null, "the round trip reported the withdrawn item");
            ushort back = ushort.Parse(withdrew.Substring("withdrew: ".Length).Split(' ')[0]);
            await pair.Host.Run(() => InGame.Destroy(back));
            await Task.Delay(2000);
            Shop.HostView gone = await pair.Host.Run(() => Shop.InGame.HostView(b.Prefab, 0, -1));
            Report.Note($"host stock after destroying item {back}: {gone.Stock}");
            Expect.Equal(after.Stock + 1, gone.Stock, "the host's stock after the item was really destroyed");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: destroys the item, as the game does when one is binned or used up.</summary>
            [Step]
            public static void Destroy(ushort netId)
            {
                if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null)
                    throw new System.Exception($"item {netId} isn't on the host");
                UnityEngine.Object.Destroy(item.gameObject);
            }
        }
    }
}
