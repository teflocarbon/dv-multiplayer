using System;
using System.Threading.Tasks;
using Multiplayer.Server.Items.Containers;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A box destroyed for good takes its container with it (docs/design/items/containers.md, "A box
    /// destroyed"; OI-CON-11). The client's toolbox holds a mug of its own and a mug nobody owns (a row
    /// as a load makes one). The host destroys the box: the client's mug comes out into its Lost and
    /// Found (ContainerCleared), the unowned mug goes with the box, and the graph has no container or
    /// row of it left.
    /// </summary>
    [Scenario("box-destroyed", "A destroyed box's owned items go to their owners' Lost and Found, its unowned ones with it, and its container is gone (OI-CON-11)")]
    [Budget(600)]
    public sealed class BoxDestroyed : Scenario<BoxDestroyed.Options>
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
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, "client"));
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(mug, "client"));
            string boxId = await pair.Host.Run(() => Owned.IdOf(box));
            string mugId = await pair.Host.Run(() => Owned.IdOf(mug));
            pair.Cleanup(async () =>
            {
                ushort now = await pair.Host.Run(() => Owned.NetIdOf(mugId));
                ushort[] left = await pair.Host.Run(() => Items.Existing(now != 0 ? new[] { box, now } : new[] { box }));
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, left), 120);
            });

            Require.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, box, mug), 120), "the deposit of the client's mug");
            await pair.Client.Run(() => Items.Drop(), 30);
            string unownedId = await pair.Host.Run(() => Boxes.PutUnowned(box, "Mug"));
            Require.That(await pair.Host.Run(() => Boxes.StoredSlot(mugId)) >= 0, "the client's mug is in the box");
            Require.That(await pair.Host.Run(() => Boxes.StoredSlot(unownedId)) >= 0, "the unowned mug is in the box");

            Require.Zero((await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { box }), 120)).Length, "boxes the host couldn't destroy");
            await Task.Delay(2000);

            OwnerInfo mugNow = await pair.Host.Run(() => Ownership.ById(mugId));
            Report.Note("the client's mug after: " + mugNow);
            Expect.That(mugNow.Lost && mugNow.LostEntry == mugId, "the client's mug is in its Lost and Found", mugNow.ToString());
            Expect.Equal(client, mugNow.Owner, "the mug's owner");
            Expect.Equal("ContainerCleared", await pair.Host.Run(() => SaveHomes.InGame.LostReason(mugId)), "why the mug is in Lost and Found");
            Expect.Equal(-1, await pair.Host.Run(() => Boxes.StoredSlot(mugId)), "the client's mug's slot in a box");
            Expect.Equal(-1, await pair.Host.Run(() => Boxes.StoredSlot(unownedId)), "the unowned mug's slot in a box");
            Expect.That(!(await pair.Host.Run(() => Ownership.ById(unownedId))).Exists, "the unowned mug is no item anywhere");
            Expect.That(!await pair.Host.Run(() => InGame.HasContainer(boxId)), "the destroyed box's container is gone");
            Expect.Equal("ok", await pair.Host.Run(() => Boxes.GraphCheck()), "the graph after the box went");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: whether the graph has a container with this id.</summary>
            [Step]
            public static bool HasContainer(string id) =>
                Guid.TryParse(id, out Guid wanted) && ContainerServer.Graph.TryGetContainer(wanted, out _);
        }
    }
}
