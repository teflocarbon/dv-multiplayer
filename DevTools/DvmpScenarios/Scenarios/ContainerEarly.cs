using System;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items.Ledger;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// No item request is carried out before the load has run (docs/design/items/save-load.md,
    /// "Loading"; OI-PER-23, OI-CON-05): a deposit made before it used to be wiped by the container import
    /// (F19). The host holds its load as not run, as a restart's first frames are, and the player
    /// (WHO) deposits an item into their box: it is refused <c>items-loading</c>, and the item stays in
    /// their inventory, its ledger home still their slot. Once the load counts as run the same
    /// deposit is accepted, and the item's home is the box.
    /// </summary>
    [Scenario("container-early", "A deposit before the load has run is refused (items-loading), the item kept where it was, and accepted after the load (OI-PER-23)")]
    public sealed class ContainerEarly : Scenario<ContainerEarly.Options>
    {
        public sealed class Options
        {
            /// <summary>The container's prefab.</summary>
            public string Container = "ItemContainerToolbox";
            /// <summary>The item to store.</summary>
            public string Prefab = "Mug";
            /// <summary>Who deposits: client or host.</summary>
            public string Who = "client";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who == "host" && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, o.Who));
            ushort item = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, o.Who));
            Peer who = o.Who == "host" ? pair.Host : pair.Client;
            // Stashed and the box open first, so the only request made while the load is held is the deposit.
            await who.Run(() => Boxes.Stash(item), 60);
            await who.Run(() => Boxes.OpenBox(box), 60);
            string id = await pair.Host.Run(() => Owned.IdOf(item));
            LedgerEntry before = await pair.Host.Run(() => Owned.Entry(id));
            Report.Note("before: " + before);

            string early;
            await pair.Host.Run(() => InGame.HoldTheLoad(true));
            try
            {
                early = await who.Run<string>(s => Boxes.DepositIntoOpen(s, box, item), 60);
            }
            finally
            {
                await pair.Host.Run(() => InGame.HoldTheLoad(false));
                await who.Run(() => Boxes.CloseBox(box));
            }
            Report.Note("deposit before the load: " + early);
            Expect.Equal("items-loading", early, "a deposit before the load has run");
            Expect.That(await who.Run(() => Owned.InInventory(item)), "the item is still in the player's inventory");
            Expect.Equal(before.Where, (await pair.Host.Run(() => Owned.Entry(id))).Where, "the item's home after the refusal");

            string after = await who.Run<string>(s => Boxes.Deposit(s, box, item), 120);
            Report.Note("deposit after the load: " + after);
            Expect.Equal(Boxes.Accepted, after, "the same deposit once the load has run");
            Expect.Equal("Container", (await pair.Host.Run(() => Owned.Entry(id))).Place, "the item's home after the deposit");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the load counts as not run (<paramref name="hold"/>), or as run again.</summary>
            [Step]
            public static void HoldTheLoad(bool hold) => ServerLedger.loaded = !hold;
        }
    }
}
