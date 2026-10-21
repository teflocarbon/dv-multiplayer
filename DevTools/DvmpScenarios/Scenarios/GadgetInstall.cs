using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Customization;
using DV.Customization.Gadgets;
using Multiplayer.Shared.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Installing and removing gadgets goes through the server (gadgets.md, PR 3a). The server spawns a
    /// gadget item and a hammer by the client and a loco for it; the client picks the gadget up and rides
    /// the loco.
    /// - The client installs it (vanilla's TryPlace): it is on the loco in both games, same UID and pose,
    ///   its item Installed on the server.
    /// - The client takes it off by hand: it's in the client's hand in both games.
    /// - It installs it again and takes it off with the hammer: a loose item in both games.
    /// - On a player host, the host's player installs one: it reaches the client.
    /// - The client, 30 m away, installs it: the server refuses, and the item stays in its hand.
    /// - The server installs a gadget of the client's on a second loco and deletes the loco: the item is in
    ///   the client's Lost and Found.
    /// </summary>
    [Scenario("gadget-install", "Installing and removing gadgets goes through the server: install, hand and hammer removal, out of reach, a deleted car")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetInstall : Scenario<GadgetInstall.Options>
    {
        public sealed class Options
        {
            public string Livery = "LocoDE2";
            public float Seconds = 10f;
            public int Lag;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Loco loco = await pair.Host.Run<Loco>(s => Cars.SpawnLocoNear(s, o.Livery, true, "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            Kit kit = await pair.Host.Run<Kit>(s => InGame.SpawnKit(s));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(kit.PersistentIds)));
            Report.Note(kit.Note);
            Require.That(kit.Gadget != 0, "the server spawned a gadget item by the client", kit.Note);

            await pair.Client.Run(() => Items.Pickup(kit.Gadget), 60);
            Require.Equal(kit.Gadget, await pair.Client.Run<ushort>(() => Items.Held()), "the client holds the gadget");
            await pair.Client.Run(() => Players.Board(loco.Id));
            await Task.Delay(2000);

            using (await pair.Client.Lag(o.Lag))
            {
                // Install.
                await pair.Client.Run(() => InGame.Install(kit.Gadget, loco.Id, 0f));
                if (o.Lag > 0)
                    Expect.Equal(1, await pair.Client.Run<int>(s => InGame.InstalledWhen(s, kit.Gadget, 0.1f)),
                        "the client installed it: in its own game at once, before the server answers (3b)");
                await Installed(pair, o, kit.Gadget, loco.Id, "the client installed it");

                // Off by hand: into the client's hand.
                await pair.Client.Run(() => InGame.RemoveByHand(kit.Gadget));
                Expect.Equal(kit.Gadget, await pair.Client.Run<ushort>(s => InGame.HeldWhen(s, kit.Gadget, o.Seconds)),
                    "the client took it off by hand: the client's hand");
                Expect.Equal("PlayerHand", (await pair.Host.Run<GadgetRecord>(() => Gadgets.Record(kit.PersistentIds[0]))).Placement,
                    "the client took it off by hand: the server's record");
                await Digests(pair, o, loco.Id, "the client took it off by hand");

                // On again, off with the hammer: a loose item.
                await pair.Client.Run(() => InGame.Install(kit.Gadget, loco.Id, 0.2f));
                await Installed(pair, o, kit.Gadget, loco.Id, "the client installed it again");
                await pair.Client.Run(() => InGame.RemoveByHammer(kit.Gadget));
                Expect.That(await pair.Host.Run<bool>(s => InGame.LooseWhen(s, kit.Gadget, o.Seconds)),
                    "the client took it off with the hammer: a loose item on the server");
                Expect.That(await pair.Client.Run<bool>(s => InGame.LooseWhen(s, kit.Gadget, o.Seconds)),
                    "the client took it off with the hammer: a loose item in the client's game");
                await Digests(pair, o, loco.Id, "the client took it off with the hammer");

                // The host's player installs one (player host).
                if (!pair.Dedicated && kit.HostGadget != 0)
                {
                    await pair.Host.Run(() => InGame.HostInstall(kit.HostGadget, loco.Id));
                    await Digests(pair, o, loco.Id, "the host's player installed one");
                }

                // Out of reach: refused, still in the client's hand.
                await pair.Client.Run(() => Items.Pickup(kit.Gadget), 60);
                Require.Equal(kit.Gadget, await pair.Client.Run<ushort>(() => Items.Held()), "the client holds the gadget again");
                float[] at = await pair.Host.Run<float[]>(() => GadgetSettingChanges.InGame.CarPosition(loco.Id));
                await pair.Client.Run(() => Players.TeleportToGround(at[0] + 30f, at[1]), 90);
                await Task.Delay(2000);
                await pair.Client.Run(() => InGame.Install(kit.Gadget, loco.Id, 0.4f));
                await Task.Delay(3000);
                Expect.Equal(kit.Gadget, await pair.Client.Run<ushort>(() => Items.Held()),
                    "the client installed it from 30 m away: refused, still in the client's hand");
                Expect.Equal("PlayerHand", (await pair.Host.Run<GadgetRecord>(() => Gadgets.Record(kit.PersistentIds[0]))).Placement,
                    "the client installed it from 30 m away: the server's record");
            }

            // A deleted car's gadget goes to its owner's Lost and Found.
            Loco doomed = await pair.Host.Run<Loco>(s => Cars.SpawnLocoNear(s, o.Livery, true, "client"));
            string doomedGadget = await pair.Host.Run<string>(s => InGame.InstallOwned(s, doomed.Id));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(new[] { doomedGadget })));
            await Task.Delay(2000);
            await pair.Host.Run<string[]>(s => Cars.Delete(s, doomed.Cars));
            await Task.Delay(2000);
            GadgetRecord record = await pair.Host.Run<GadgetRecord>(() => Gadgets.Record(doomedGadget));
            Expect.That(record.InLostAndFound, "the server deleted a loco with the client's gadget on it: in the client's Lost and Found",
                $"placement {record.Placement}, count {record.Count}");
        }

        private static async Task Installed(Pair pair, Options o, ushort gadget, string loco, string what)
        {
            Expect.Equal(1, await pair.Client.Run<int>(s => InGame.InstalledWhen(s, gadget, o.Seconds)), $"{what}: on the loco in the client's game");
            Expect.Equal(1, await pair.Host.Run<int>(() => Gadgets.CountInstalled(new[] { gadget })), $"{what}: on the loco on the server");
            await Digests(pair, o, loco, what);
        }

        private static async Task Digests(Pair pair, Options o, string loco, string what)
        {
            string host = await pair.Host.Run<string>(() => Gadgets.Digest(loco));
            Expect.Equal(host, await pair.Client.Run<string>(s => Gadgets.DigestWhenEqual(s, loco, host, o.Seconds)),
                $"{what}: the client's gadgets on the loco, against the server's");
        }

        public sealed class Kit
        {
            public ushort Gadget, HostGadget;
            public string[] PersistentIds;
            public string Note;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static GadgetBase Gadget(ushort netId) =>
                NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null ? item.GetComponent<GadgetItem>()?.Gadget : null;

            private static InventoryItemSpec HammerSpec() =>
                DV.Globals.G.Items.items.Where(s => s != null && s.GetComponent<GadgetRemover>() != null)
                    .OrderBy(s => s.ItemPrefabName, StringComparer.Ordinal).FirstOrDefault();

            /// <summary>Host: a gadget item and a hammer by the client, owned by it, and on a player host a
            /// gadget item by the host's player.</summary>
            [Step]
            public static IEnumerator SpawnKit(Step<Kit> step)
            {
                var client = Gadgets.Client();
                Vector3 beside = client.WorldPosition + Vector3.right * 0.8f + Vector3.up * 0.6f;
                NetworkedItem gadget = null, hammer = null, hostGadget = null;
                yield return Gadgets.SpawnOwned(Gadgets.SimpleSpec(), beside, client, i => gadget = i);
                if (HammerSpec() is { } hammerSpec)
                    yield return Gadgets.SpawnOwned(hammerSpec, beside + Vector3.forward * 0.6f, client, i => hammer = i);
                var server = Multiplayer.Server.Session.NetworkServer.Instance;
                if (!Multiplayer.Shared.Session.NetworkLifecycle.Instance.IsDedicatedHost && server.LocalPlayer is { } host)
                    yield return Gadgets.SpawnOwned(Gadgets.SimpleSpec(), PlayerManager.PlayerTransform.position + Vector3.up, host, i => hostGadget = i);
                string Id(NetworkedItem i) => i.GetComponent<Multiplayer.Server.Items.PersistentWorldItemIdentity>().Value.ToString("D");
                step.Result = new Kit
                {
                    Gadget = gadget.NetId,
                    HostGadget = hostGadget != null ? hostGadget.NetId : (ushort)0,
                    PersistentIds = new[] { gadget, hammer, hostGadget }.Where(i => i != null).Select(Id).ToArray(),
                    Note = $"{gadget.name}, hammer {(hammer != null ? hammer.name : "none")}, host's {(hostGadget != null ? hostGadget.name : "none")}"
                };
            }

            /// <summary>Installs the held gadget on the loco the way vanilla's placing does, through
            /// <c>GadgetItem.TryPlace</c>, <paramref name="along"/> metres along the cab.</summary>
            [Step]
            public static void Install(ushort netId, string loco, float along)
            {
                TrainCar car = Dvmp.Lib.Trains.Find(loco);
                GadgetItem item = Gadget(netId).GadgetItem;
                Vector3 at = car.interior.TransformPoint(new Vector3(0.3f, 1.5f, along));
                GadgetItem.TryPlace(car.Customization, at, car.interior.rotation, item, null, false, out _);
            }

            /// <summary>Host (player host): its player installs the gadget it holds, in the server's own game.</summary>
            [Step]
            public static void HostInstall(ushort netId, string loco)
            {
                TrainCar car = Dvmp.Lib.Trains.Find(loco);
                GadgetItem item = Gadget(netId).GadgetItem;
                item.Item ??= item.GetComponent<DV.CabControls.ItemBase>();
                GadgetItem.TryPlace(car.Customization, car.interior.TransformPoint(new Vector3(-0.3f, 1.5f, 0.6f)),
                    car.interior.rotation, item, null, false, out _);
            }

            /// <summary>Host: installs a gadget owned by the client on the loco, as the server.</summary>
            [Step]
            public static IEnumerator InstallOwned(Step<string> step, string loco)
            {
                TrainCar car = Dvmp.Lib.Trains.Find(loco);
                var client = Gadgets.Client();
                NetworkedItem item = null;
                yield return Gadgets.SpawnOwned(Gadgets.SimpleSpec(), client.WorldPosition + Vector3.up, client, i => item = i);
                Gadgets.Install(item, car.Customization, car.interior.TransformPoint(new Vector3(0.3f, 1.5f, 0f)));
                for (int i = 0; i < 10; i++) yield return null;
                step.Result = item.GetComponent<Multiplayer.Server.Items.PersistentWorldItemIdentity>().Value.ToString("D");
            }

            /// <summary>Takes the gadget off with an empty hand, as vanilla's non-VR hand does.</summary>
            [Step]
            public static void RemoveByHand(ushort netId)
            {
                var hand = Resources.FindObjectsOfTypeAll<GadgetHandNonVR>().First(h => h.gameObject.scene.IsValid());
                hand.TryGrab(Gadget(netId));
            }

            /// <summary>Takes the gadget off with the hammer.</summary>
            [Step]
            public static void RemoveByHammer(ushort netId)
            {
                var hammer = Resources.FindObjectsOfTypeAll<GadgetRemover>().First(h => h.gameObject.scene.IsValid());
                hammer.Remove(Gadget(netId));
            }

            /// <summary>1 once the gadget is on a car in this game, or 0 after <paramref name="seconds"/>.</summary>
            [Step]
            public static IEnumerator InstalledWhen(Step<int> step, ushort netId, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    step.Result = Gadget(netId) is { IsLinked: true } ? 1 : 0;
                    if (step.Result == 1)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }

            /// <summary>The held item's net ID once it is <paramref name="netId"/>, or after <paramref name="seconds"/>.</summary>
            [Step]
            public static IEnumerator HeldWhen(Step<ushort> step, ushort netId, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    step.Result = Items.Held();
                    if (step.Result == netId)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }

            /// <summary>Whether the item is a loose, active item here (not installed), once it is or after <paramref name="seconds"/>.</summary>
            [Step]
            public static IEnumerator LooseWhen(Step<bool> step, ushort netId, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    step.Result = NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null && item.gameObject.activeInHierarchy &&
                                  item.GetComponent<GadgetItem>()?.Gadget is { IsLinked: false };
                    if (step.Result)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }
        }
    }
}
