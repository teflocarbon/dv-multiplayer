using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Interaction;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A consumable used up is one server step (OI-VAN-03; docs/design/items/vanilla-storage.md,
    /// "Used up"). The client loads a spray can of its own into a sprayer and sprays a flatbed: the car
    /// has the can's paint on both games, the can is gone with its entry ended <c>UsedUp</c>, and an
    /// empty can, the client's and born <c>Replaced</c>, is in the sprayer's magazine on both. On a
    /// player host the host player does the same. Then the client opens a locked garage with its key:
    /// the key is gone, its entry ended <c>UsedUp</c>, and the garage is unlocked on both games. A save
    /// whose garages are all unlocked skips the key half, saying so.
    /// </summary>
    [Scenario("use-up", "A spray can and a garage key used up go through the server: gone, entry UsedUp, the empty can born Replaced, the garage open on both (OI-VAN-03)")]
    [Budget(600)]
    public sealed class UseUp : Scenario<UseUp.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(5000);
            await Spray(pair, "client");
            if (!pair.Dedicated)
                await Spray(pair, "host");
            else
                Report.Note("the host player's spray: skipped, a dedicated host has no player");
            await OpenGarage(pair);
        }

        private static async Task Spray(Pair pair, string who)
        {
            Peer sprayerGame = who == "host" ? pair.Host : pair.Client;
            string car = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "FlatbedEmpty", who));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { car })));
            ushort sprayer = await pair.Host.Run<ushort>(s => InGame.SpawnWith(s, "sprayer", who));
            ushort can = await pair.Host.Run<ushort>(s => InGame.SpawnWith(s, "can", who));
            pair.Cleanup(() => pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { sprayer, can }), 60));
            await pair.Host.Run(() => Ownership.Give(can, who));
            string canId = await pair.Host.Run(() => Owned.IdOf(can));
            string theme = await pair.Host.Run(() => InGame.CanTheme(can));
            Report.Note($"the {who}'s can {can} ({canId}) holds {theme}; sprayer {sprayer}; car {car}");

            string loaded = await sprayerGame.Run<string>(s => InGame.Load(s, sprayer, can), 60);
            Report.Note($"the {who} loaded the can: {loaded}");
            await Until.Value(() => pair.Host.Run(() => Items.Record(can)), r => r.Placement == "Magazine", 20, $"the server has the {who}'s can in the sprayer");
            string sprayed = await sprayerGame.Run<string>(s => InGame.Spray(s, sprayer, car), 60);
            Report.Note($"the {who} sprayed: {sprayed}");

            await Until.Value(() => pair.Host.Run(() => Items.Record(can)), r => !r.Known, 20, $"the {who}'s can is gone on the server");
            ushort empty = await Until.Value(() => pair.Host.Run(() => InGame.InSprayer(sprayer)), n => n != 0 && n != can, 20,
                "the server has an empty can in the sprayer");
            pair.Cleanup(() => pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { empty }), 60));
            await Until.Value(() => pair.Client.Run(() => InGame.InSprayer(sprayer)), n => n == empty, 20, "the client has the empty can in the sprayer");
            Expect.That((await pair.Client.Run(() => Items.Existing(new[] { can }))).Length == 0, $"the client has no copy of the {who}'s can");
            Expect.Equal(theme, await pair.Host.Run(() => InGame.CarTheme(car)), "the car's paint on the host");
            await Until.Value(() => pair.Client.Run(() => InGame.CarTheme(car)), t => t == theme, 20, "the car's paint on the client");

            LedgerEntry used = await pair.Host.Run(() => Owned.Entry(canId));
            Report.Note($"the {who}'s can's entry: {used}");
            Expect.That(used.Known && !used.Live, $"the {who}'s can's entry has ended", used.ToString());
            Expect.Equal("UsedUp", used.Ended, $"why the {who}'s can's entry ended");
            string emptyId = await pair.Host.Run(() => Owned.IdOf(empty));
            LedgerEntry made = await pair.Host.Run(() => Owned.Entry(emptyId));
            Report.Note($"the empty can {empty}'s entry: {made}");
            Expect.That(made.Live, "the empty can's entry lives", made.ToString());
            Expect.Equal("Replaced", made.Born, "how the empty can was born");
            Expect.Equal(used.Owner, made.Owner, "the empty can is the can's owner's");
        }

        private static async Task OpenGarage(Pair pair)
        {
            Garage garage = await pair.Host.Run(() => InGame.LockedGarage());
            if (!garage.Found)
            {
                Report.Note("the key half: skipped, this save has no locked garage (use a save with one)");
                return;
            }
            Report.Note($"the locked garage: {garage.Type}, its padlock wants {garage.Key}, at {garage.At}");
            await pair.Client.Run(() => InGame.GoTo(garage.At.X + 1.5f, garage.At.Z), 60);
            if (!pair.Dedicated)
                await pair.Host.Run(() => InGame.GoTo(garage.At.X - 3f, garage.At.Z), 60);
            ushort key = await pair.Host.Run<ushort>(s => InGame.SpawnKey(s, garage.Key));
            pair.Cleanup(() => pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { key }), 60));
            await pair.Host.Run(() => Ownership.Give(key, "client"));
            string keyId = await pair.Host.Run(() => Owned.IdOf(key));

            string used = await pair.Client.Run<string>(s => InGame.UseKey(s, key, garage.At), 60);
            Report.Note("the client used the key: " + used);
            await Until.Value(() => pair.Host.Run(() => Items.Record(key)), r => !r.Known, 20, "the key is gone on the server");
            Expect.That(await pair.Host.Run(() => InGame.GarageUnlocked(garage.Type)), "the garage is unlocked on the host");
            await Until.Value(() => pair.Client.Run(() => InGame.GarageUnlocked(garage.Type)), u => u, 20, "the garage is unlocked on the client");
            await Until.Value(() => pair.Client.Run(() => Items.Existing(new[] { key })), e => e.Length == 0, 20, "the client has no copy of the key");
            LedgerEntry e = await pair.Host.Run(() => Owned.Entry(keyId));
            Report.Note("the key's entry: " + e);
            Expect.That(e.Known && !e.Live, "the key's entry has ended", e.ToString());
            Expect.Equal("UsedUp", e.Ended, "why the key's entry ended");
        }

        /// <summary>A locked garage's padlock: where it is, the key it wants and the garage.</summary>
        public sealed class Garage
        {
            public bool Found;
            public Abs At = new();
            public string Key = "", Type = "";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: a fresh personal sprayer (<paramref name="kind"/> "sprayer") or a full paint can
            /// ("can") beside a player ("host" or "client"), found by what it is. Result: its net ID.
            /// </summary>
            [Step]
            public static IEnumerator SpawnWith(Step<ushort> step, string kind, string near)
            {
                var spec = DV.Globals.G.Items.items.FirstOrDefault(i => kind == "sprayer"
                        ? i.GetComponent<PaintSprayer>() != null
                        : i.GetComponent<PaintCan>() is { } c && c.theme != null && c.emptyCanPrefab != null)
                    ?? throw new Exception("no item spec for a " + kind);
                yield return Items.SpawnPersonal(step, spec.ItemPrefabName, near);
            }

            /// <summary>Host: a fresh garage key that opens <paramref name="keyType"/>'s padlock, beside the client.</summary>
            [Step]
            public static IEnumerator SpawnKey(Step<ushort> step, string keyType)
            {
                var spec = DV.Globals.G.Items.items.FirstOrDefault(i => i.GetComponent<PadlockKey>() is { } k && k.keyType.ToString() == keyType)
                    ?? throw new Exception("no item spec for a key " + keyType);
                yield return Items.SpawnPersonal(step, spec.ItemPrefabName, "client");
            }

            [Step]
            public static string CanTheme(ushort netId) => Items.Find(netId).GetComponent<PaintCan>()?.theme?.name ?? "";

            [Step]
            public static string CarTheme(string car) => Trains.Find(car).PaintExterior?.CurrentTheme?.name ?? "";

            /// <summary>The net ID of what is in the sprayer's magazine slot 0 on this game, 0 for nothing.</summary>
            [Step]
            public static ushort InSprayer(ushort sprayer)
            {
                if (!NetworkedItem.TryGet(sprayer, out NetworkedItem item) || item == null) return 0;
                GameObject loaded = item.GetComponent<PaintSprayer>()?.magazine?[0];
                return loaded != null && loaded.TryGetComponent(out NetworkedItem n) ? n.NetId : (ushort)0;
            }

            /// <summary>Picks up the sprayer and loads the can into it, as vanilla's use of a can on it does.</summary>
            [Step]
            public static IEnumerator Load(Step<string> step, ushort sprayer, ushort can)
            {
                yield return Items.Arrive(sprayer);
                yield return Items.Arrive(can);
                if (Hands.Held != null) yield return Hands.Drop();
                NetworkedItem tool = Items.Find(sprayer);
                yield return LocalPlayer.GoTo(tool);
                yield return Hands.Pickup(sprayer);
                PaintSprayer paint = tool.GetComponent<PaintSprayer>();
                bool added = paint.magazine.AddItem(Items.Find(can).gameObject, 0);
                for (int i = 0; i < 90; i++) yield return null;
                step.Result = added ? $"loaded, slot 0 holds {InSprayer(sprayer)}" : "the magazine refused it";
            }

            /// <summary>Sprays the held sprayer's can on the car's outside, as vanilla does once the spray is done.</summary>
            [Step]
            public static IEnumerator Spray(Step<string> step, ushort sprayer, string car)
            {
                PaintSprayer paint = Items.Find(sprayer).GetComponent<PaintSprayer>();
                TrainCar target = Trains.Find(car);
                if (Hands.Held == null || Hands.Held.NetId != sprayer) throw new Exception($"not holding the sprayer {sprayer}");
                paint.Apply(target.PaintExterior);
                for (int i = 0; i < 120; i++) yield return null;
                step.Result = $"the car is {CarTheme(car)}, slot 0 holds {InSprayer(sprayer)}";
            }

            /// <summary>Host: a locked garage's padlock (one the scene has, active or not), or none.</summary>
            [Step]
            public static Garage LockedGarage()
            {
                var unlocker = Resources.FindObjectsOfTypeAll<GaragePadlockUnlocker>()
                    .FirstOrDefault(u => u.gameObject.scene.isLoaded && u.padlock != null && u.padlock.IsLocked &&
                                         u.GetComponent<GarageCarSpawner>() is { } g &&
                                         !LicenseManager.Instance.IsGarageUnlocked(g.garageType));
                if (unlocker == null) return new Garage();
                return new Garage
                {
                    Found = true,
                    At = Positions.Of(unlocker.padlock.transform.position),
                    Key = unlocker.padlock.unlockingKey.ToString(),
                    Type = unlocker.GetComponent<GarageCarSpawner>().garageType.ToString()
                };
            }

            /// <summary>This game's player to a point on the ground.</summary>
            [Step]
            public static IEnumerator GoTo(float x, float z) => LocalPlayer.TeleportToGround(x, z);

            [Step]
            public static bool GarageUnlocked(string type) =>
                LicenseManager.Instance.IsGarageUnlocked(DV.Globals.G.Types.garages.First(g => g.ToString() == type));

            /// <summary>Client: picks up the key and uses it on the padlock nearest <paramref name="at"/>, as vanilla's use does.</summary>
            [Step]
            public static IEnumerator UseKey(Step<string> step, ushort key, Abs at)
            {
                yield return Items.Arrive(key);
                if (Hands.Held != null) yield return Hands.Drop();
                NetworkedItem item = Items.Find(key);
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(key);
                Padlock padlock = null;
                Vector3 there = new(at.X, at.Y, at.Z);
                yield return Wait.Until(() => (padlock = UnityEngine.Object.FindObjectsOfType<Padlock>()
                    .OrderBy(p => Vector3.Distance(Dev.Abs(p.transform.position), there)).FirstOrDefault()) != null, 30f, "the padlock to load here");
                ItemUseTarget target = padlock.GetComponentInChildren<ItemUseTarget>(true) ?? padlock.GetComponentInParent<ItemUseTarget>()
                    ?? throw new Exception("the padlock has no use target");
                bool used = item.GetComponent<KeyUse>().HandleUse(target);
                for (int i = 0; i < 120; i++) yield return null;
                step.Result = $"{(used ? "used" : "not used")}; the key is {(item == null ? "gone" : "still here")}";
            }
        }
    }
}
