using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using Multiplayer.Shared.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Items on gadget hangers go through the server (gadgets.md, PR 10). The server puts a hanger in a DE2
    /// by the client and a mug by the client; the client picks the mug up and rides the DE2.
    /// - It hangs the mug: on the hanger in both games; the hanger can't be taken off while it holds it.
    /// - The server pushes the DE2: the mug swings, the same way in both games (within 5°).
    /// - The server knocks the mug off (a forced unsnap): off the hanger in both games.
    /// </summary>
    [Scenario("gadget-hangers", "An item hung on a gadget hanger is hung in every game, swings the same way, and a forced unsnap drops it everywhere")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetHangers : Scenario<GadgetHangers.Options>
    {
        public sealed class Options
        {
            public string Livery = "LocoDE2";
            public float Seconds = 10f;
            public float Degrees = 5f;
            public int Lag;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Loco loco = await pair.Host.Run<Loco>(s => Cars.SpawnLocoNear(s, o.Livery, true, "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            Kit kit = await pair.Host.Run<Kit>(s => InGame.Build(s, loco.Id));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(kit.PersistentIds)));
            Report.Note(kit.Note);
            Require.That(kit.Hanger != 0 && kit.Mug != 0, "the server put a hanger in the DE2 and a mug by the client", kit.Note);

            await pair.Client.Run(() => Items.Pickup(kit.Mug), 60);
            Require.Equal(kit.Mug, await pair.Client.Run<ushort>(() => Items.Held()), "the client holds the mug");
            await pair.Client.Run(() => Players.Board(loco.Id));
            await Task.Delay(1500);

            using (await pair.Client.Lag(o.Lag))
            {
                await pair.Client.Run(() => InGame.Hang(kit.Hanger, kit.Mug));
                Expect.Equal(kit.Mug, await pair.Host.Run<ushort>(s => InGame.HungWhen(s, kit.Hanger, kit.Mug, o.Seconds)),
                    "the client hung the mug: on the server's hanger");
                Expect.Equal(kit.Mug, await pair.Client.Run<ushort>(s => InGame.HungWhen(s, kit.Hanger, kit.Mug, o.Seconds)),
                    "the client hung the mug: on its own hanger");
                Expect.That(!await pair.Host.Run<bool>(() => InGame.Removable(kit.Hanger)),
                    "the hanger can't be taken off while the mug is on it (the server's rule)");

                await pair.Host.Run(() => Cars.Push(loco.Id, 3f, 3f, 1f), 60);
                float host = await pair.Host.Run<float>(() => InGame.SwingAngle(kit.Hanger));
                float client = await pair.Client.Run<float>(() => InGame.SwingAngle(kit.Hanger));
                Report.Note($"swing from straight down: server {host:0.0}°, client {client:0.0}°");
                Expect.That(Math.Abs(host - client) <= o.Degrees + o.Lag / 50f, "the mug swings the same way in both games",
                    $"server {host:0.0}°, client {client:0.0}°");

                await pair.Host.Run(() => InGame.KnockOff(kit.Hanger));
                Expect.Equal((ushort)0, await pair.Client.Run<ushort>(s => InGame.HungWhen(s, kit.Hanger, 0, o.Seconds)),
                    "the server knocked the mug off: off the client's hanger");
            }
        }

        public sealed class Kit
        {
            public ushort Hanger, Mug;
            public string[] PersistentIds;
            public string Note;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static GadgetBase Gadget(ushort netId) =>
                NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null ? item.GetComponent<GadgetItem>()?.Gadget : null;

            private static SnapPointGadget Point(ushort hanger) => Gadget(hanger)?.GetComponentInChildren<SnapPointGadget>(true);

            /// <summary>Host: a hanger in the loco, and a mug by the client, both owned by it.</summary>
            [Step]
            public static IEnumerator Build(Step<Kit> step, string carId)
            {
                var client = Gadgets.Client();
                TrainCar car = Dvmp.Lib.Trains.Find(carId);
                Vector3 beside = client.WorldPosition + Vector3.right * 0.8f + Vector3.up * 0.6f;
                InventoryItemSpec hangerSpec = DV.Globals.G.Items.items
                    .Where(s => s != null && s.GetComponent<GadgetItem>()?.gadgetPrefab is GadgetWithSnapPoint g && g.GetComponentInChildren<Dangler>(true) != null)
                    .OrderBy(s => s.ItemPrefabName, StringComparer.Ordinal).FirstOrDefault();
                InventoryItemSpec mugSpec = DV.Globals.G.Items.items.FirstOrDefault(s => s != null && s.ItemPrefabName == "Mug");
                NetworkedItem hanger = null, mug = null;
                if (hangerSpec != null)
                    yield return Gadgets.SpawnOwned(hangerSpec, beside, client, i => hanger = i);
                if (mugSpec != null)
                    yield return Gadgets.SpawnOwned(mugSpec, beside + Vector3.forward * 0.5f, client, i => mug = i);
                GadgetBase installed = hanger != null
                    ? Gadgets.Install(hanger, car.Customization, car.interior.TransformPoint(new Vector3(0.3f, 1.9f, 0f)))
                    : null;
                for (int i = 0; i < 10; i++) yield return null;
                step.Result = new Kit
                {
                    Hanger = installed != null && installed.IsLinked ? hanger.NetId : (ushort)0,
                    Mug = mug != null ? mug.NetId : (ushort)0,
                    PersistentIds = new[] { hanger, mug }.Where(i => i != null)
                        .Select(i => i.GetComponent<Multiplayer.Server.Items.PersistentWorldItemIdentity>().Value.ToString("D")).ToArray(),
                    Note = $"hanger {hangerSpec?.ItemPrefabName ?? "none"}, mug {mugSpec?.ItemPrefabName ?? "none"}"
                };
            }

            /// <summary>Hangs the held item on the hanger, as a player putting it there does.</summary>
            [Step]
            public static void Hang(ushort hanger, ushort item) => Point(hanger).SnapItem(Items.Find(item).Item);

            /// <summary>The item on the hanger here (0 for none) once it is <paramref name="expected"/>, or after <paramref name="seconds"/>.</summary>
            [Step]
            public static IEnumerator HungWhen(Step<ushort> step, ushort hanger, ushort expected, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    var snapped = Point(hanger)?.SnappedItem;
                    step.Result = snapped != null ? snapped.GetComponent<NetworkedItem>()?.NetId ?? 0 : (ushort)0;
                    if (step.Result == expected)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }

            [Step]
            public static bool Removable(ushort hanger) => Gadget(hanger).CanBeRemovedUsingMethod(GadgetBase.GadgetRemovalMethod.Remover);

            /// <summary>The pendulum's angle from hanging straight down, relative to the car, in degrees.</summary>
            [Step]
            public static float SwingAngle(ushort hanger)
            {
                Dangler dangler = Gadget(hanger).GetComponentInChildren<Dangler>(true);
                return Quaternion.Angle(Quaternion.identity, dangler.transform.localRotation);
            }

            /// <summary>Host: knocks the item off the hanger, as the pendulum turning over does.</summary>
            [Step]
            public static void KnockOff(ushort hanger) => Point(hanger).UnsnapItem(forced: true);
        }
    }
}
