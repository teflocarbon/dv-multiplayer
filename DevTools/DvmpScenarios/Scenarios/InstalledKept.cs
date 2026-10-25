using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Customization;
using Multiplayer.Client.Items.Projection;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The item code never destroys an installed item, on any game, and logs a refused destroy
    /// (docs/design/item-refactor.md, "The gadget seam"; OI-GAD-06, F49). The server installs a
    /// gadget owned by the client on a loco. Then two item-code destroys are run on it: on the
    /// server, a remake from an unloadable saved state (ItemSaveLoad.RemakeFresh: the object goes
    /// and a fresh one goes to Lost and Found); on the client, the retirement of its copy
    /// (ClientItemManager.RetireClientProjection, which a destroy from the server or an unbound
    /// item ends in). Both must be refused: the gadget still installed on the loco, once, not in
    /// Lost and Found, and the client's copy still there. Before R7 both destroyed it.
    /// </summary>
    [Scenario("installed-kept", "Item code refuses to destroy an installed gadget's item, on the server and on a client")]
    [Budget(300)]
    public sealed class InstalledKept : Scenario<InstalledKept.Options>
    {
        public sealed class Options
        {
            public string Loco = "LocoDE2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            HeldLoco loco = await pair.Host.Run<HeldLoco>(s => Cars.SpawnHeldLoco(s, o.Loco));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            string id = await pair.Host.Run<string>(s => InGame.Install(s, loco.Loco));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(new[] { id })));
            GadgetRecord before = await pair.Host.Run<GadgetRecord>(() => Gadgets.Record(id));
            Require.Equal("Installed", before.Placement, "the gadget's placement");
            await Until.Value(() => pair.Client.Run(() => InGame.Installed(before.NetId)), v => v, 20,
                "the client to have the gadget installed");

            await pair.Host.Run(() => InGame.RemakeFresh(before.NetId, id));
            bool clientKept = await pair.Client.Run<bool>(s => InGame.Retire(s, before.NetId));
            await Task.Delay(2000);

            GadgetRecord after = await pair.Host.Run<GadgetRecord>(() => Gadgets.Record(id));
            Report.Note($"after the destroys: {after.Count} of it, {after.Placement} on {after.Anchor}, " +
                        $"in Lost and Found {after.InLostAndFound}; the client's copy kept {clientKept}");
            Expect.Equal(1, after.Count, "items with the gadget's id");
            Expect.Equal(before.NetId, after.NetId, "the gadget's net id (not remade)");
            Expect.Equal("Installed", after.Placement, "the gadget's placement");
            Expect.That(!after.InLostAndFound, "the gadget is not in Lost and Found");
            Expect.That(clientKept, "the client's copy of the installed gadget is still there");
            Expect.That(await pair.Client.Run(() => InGame.Installed(before.NetId)), "the client still has it installed");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: a simple gadget owned by the client, installed in the loco's cab. Result: its persistent id.</summary>
            [Step]
            public static IEnumerator Install(Step<string> step, string locoId)
            {
                var client = Gadgets.Client();
                TrainCar car = CarSpawner.Instance.AllCars.First(c => c != null && c.ID == locoId);
                NetworkedItem gadget = null;
                yield return Gadgets.SpawnOwned(Gadgets.SimpleSpec(), client.WorldPosition + Vector3.right * 1.5f + Vector3.up,
                    client, item => gadget = item);
                Gadgets.Install(gadget, car.Customization, car.interior.TransformPoint(Vector3.up * 1.5f));
                yield return new WaitForSeconds(2);
                step.Result = gadget.GetComponent<ItemIdentity>().Value.ToString("D");
            }

            /// <summary>This game has the item, and its last state was Installed.</summary>
            [Step]
            public static bool Installed(ushort netId) =>
                NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null &&
                item.LastObservedState == ItemState.Installed;

            /// <summary>Host: the item code's remake of an item whose saved state won't load.</summary>
            [Step]
            public static IEnumerator RemakeFresh(ushort netId, string persistentId)
            {
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                if (item == null || !LiveItems.TryGet(netId, out LiveItems.LiveItem record))
                    throw new Exception("no installed item " + netId);
                yield return ItemSaveLoad.RemakeFresh(item, Guid.Parse(persistentId), record.PrefabName, "the scenario's unloadable state");
                yield return new WaitForSeconds(1);
            }

            /// <summary>Client: retires its copy of the item. Result: the copy is still there, bound.</summary>
            [Step]
            public static IEnumerator Retire(Step<bool> step, ushort netId)
            {
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                if (item == null) throw new Exception("the client has no item " + netId);
                ClientItemManager.Instance.RetireClientProjection(item, "the scenario");
                yield return null;
                yield return null;
                step.Result = item != null && item.NetId == netId;
            }
        }
    }
}
