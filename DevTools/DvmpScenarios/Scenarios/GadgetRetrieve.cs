using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Retrieving an installed gadget (owned-items.md, PR 8). The server installs a gadget of the
    /// client's on a loco. The client retrieves it from its owned-items list: it comes off the loco in
    /// both games and is in the client's Lost and Found, under its own id.
    /// </summary>
    [Scenario("gadget-retrieve", "The client retrieves its gadget installed on a loco from its owned-items list: it comes off in both games and is in its Lost and Found")]
    [Budget(300)]
    public sealed class GadgetRetrieve : Scenario<GadgetRetrieve.Options>
    {
        public sealed class Options
        {
            public string Livery = "LocoDE2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(5000);
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 5000, Nearby = 5000, Grace = 900 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            Loco loco = await pair.Host.Run<Loco>(s => Cars.SpawnLocoNear(s, o.Livery, true, "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            string client = await pair.Host.Run(() => Owned.ClientIdentity());

            string gadgetId = await pair.Host.Run<string>(s => GadgetInstall.InGame.InstallOwned(s, loco.Id));
            ushort net = await pair.Host.Run(() => Owned.NetIdOf(gadgetId));
            await Task.Delay(2000);
            LedgerEntry before = await pair.Host.Run(() => Owned.Entry(gadgetId));
            Report.Note($"gadget {net} ({gadgetId}): {before.Where}");
            Require.Equal("Installed", before.Place, "where the gadget is before the retrieval");
            Require.Equal(1, await pair.Client.Run(() => Gadgets.CountInstalled(new[] { net })), "the gadget installed in the client's game");

            uint handle = await pair.Host.Run(() => OwnedRetrieve.InGame.HandleOf(client, gadgetId));
            Require.That(handle != 0, "the gadget is in the client's owned-items list");
            OwnedRetrieve.Result r = await pair.Client.Run<OwnedRetrieve.Result>(s => OwnedRetrieve.InGame.Retrieve(s, handle), 30);
            Expect.That(r.Accepted, "the retrieval is accepted", r.Reason);
            await Task.Delay(2000);
            OwnerInfo after = await pair.Host.Run(() => Ownership.ById(gadgetId));
            Report.Note("after: " + after);
            Expect.That(after.Lost && after.LostEntry == gadgetId, "the gadget is in the client's Lost and Found under its own id", after.ToString());
            Expect.Equal(0, await pair.Host.Run(() => Gadgets.CountInstalled(new[] { net })), "the gadget installed in the host's game");
            Expect.Equal(0, await pair.Client.Run(() => Gadgets.CountInstalled(new[] { net })), "the gadget installed in the client's game");
        }
    }
}
