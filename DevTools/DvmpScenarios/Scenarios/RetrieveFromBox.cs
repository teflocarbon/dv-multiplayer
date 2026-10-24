using System.Threading.Tasks;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Retrieving an item out of a box that isn't its owner's (docs/design/items/owned-list.md, "Out of
    /// a box"; OI-LIST-23). The client deposits a mug of its own into a toolbox nobody owns, which it
    /// keeps by holding, and puts the box down. It retrieves the mug from its owned-items list: the mug
    /// alone comes out into its Lost and Found, asked about itself, and the box stays where it is,
    /// nobody's and empty. (A mug in the client's own box takes the box whole: <c>owned-retrieve</c>.)
    /// </summary>
    [Scenario("retrieve-from-box", "The client retrieves its mug out of a box nobody owns: the mug alone goes to its Lost and Found, the box stays (OI-LIST-23)")]
    [Budget(600)]
    public sealed class RetrieveFromBox : Scenario<RetrieveFromBox.Options>
    {
        public sealed class Options
        {
            /// <summary>The container's prefab.</summary>
            public string Container = "ItemContainerToolbox";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            ushort box = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Container, "client"));
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(mug, "client"));
            string boxId = await pair.Host.Run(() => Owned.IdOf(box));
            string mugId = await pair.Host.Run(() => Owned.IdOf(mug));
            pair.Cleanup(async () =>
            {
                ushort now = await pair.Host.Run(() => Owned.NetIdOf(mugId));
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, now != 0 ? new[] { box, now } : new[] { box }), 120);
            });

            Require.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, box, mug), 120), "the deposit of the client's mug into the box nobody owns");
            await pair.Client.Run(() => Items.Drop(), 30);
            string boxOwner = (await pair.Host.Run(() => Ownership.ById(boxId))).Owner;
            Require.That(boxOwner.Length == 0 || boxOwner == System.Guid.Empty.ToString("D"), "the box is nobody's", boxOwner);
            Require.That(await pair.Host.Run(() => Boxes.StoredSlot(mugId)) >= 0, "the mug is in the box");

            uint handle = await pair.Host.Run(() => OwnedRetrieve.InGame.HandleOf(client, mugId));
            Require.That(handle != 0, "the boxed mug is in the client's owned-items list");
            OwnedRetrieve.Result r = await pair.Client.Run<OwnedRetrieve.Result>(s => OwnedRetrieve.InGame.Retrieve(s, handle), 30);
            Report.Note($"retrieval: accepted {r.Accepted}, {r.Reason}");
            Expect.That(r.Accepted, "the retrieval of the mug out of the box nobody owns is accepted", r.Reason);
            await Task.Delay(1000);

            OwnerInfo mugNow = await pair.Host.Run(() => Ownership.ById(mugId));
            Report.Note("the mug after: " + mugNow);
            Expect.That(mugNow.Lost && mugNow.LostEntry == mugId, "the mug is in the client's Lost and Found", mugNow.ToString());
            Expect.Equal(client, mugNow.Owner, "the mug's owner");
            Expect.Equal("LostAndFound", (await pair.Host.Run(() => Owned.Entry(mugId))).Place, "the mug's ledger home");
            Expect.Equal(-1, await pair.Host.Run(() => Boxes.StoredSlot(mugId)), "the mug's slot in the box");
            OwnerInfo boxNow = await pair.Host.Run(() => Ownership.ById(boxId));
            Report.Note("the box after: " + boxNow);
            Expect.That(boxNow.Exists && !boxNow.Lost, "the box stays where it is, in no Lost and Found", boxNow.ToString());
            Expect.Equal("ok", await pair.Host.Run(() => Boxes.GraphCheck()), "the graph after the retrieval");
        }
    }
}
