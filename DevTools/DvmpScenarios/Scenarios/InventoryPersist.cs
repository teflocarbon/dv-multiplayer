using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Shared.Items.LiveState;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A client's inventory survives the host saving and reloading: the client stashes a personal
    /// item (as a purchase gives them), the host saves, both games restart from the save (the same
    /// kind of host), and the client must get the item back once: bound to the host, not as a stray
    /// local copy. HAND=1 keeps the item in the client's hand when the host saves (it comes back in
    /// the inventory).
    /// </summary>
    [Scenario("inventory-persist", "A client's personal item survives a host save and a restart of both games, back once in its inventory, bound to the host")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class InventoryPersist : Scenario<InventoryPersist.Options>
    {
        public sealed class Options
        {
            /// <summary>The item's prefab.</summary>
            public string Prefab = "Mug";
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
            /// <summary>Keep the item in the client's hand when the host saves.</summary>
            public bool Hand;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a client that has only just joined is still receiving its restored items
            Contents before = await pair.Client.Run(() => InGame.ReadContents(o.Prefab));
            ushort item = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            Report.Note($"client stashes {o.Prefab} {item} (holding {before.Count} before)");
            await pair.Client.Run<int>(s => Items.Stash(s, item, o.Hand), 60);
            int want = before.Count + 1;

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(12000);   // the client receives its restored items
            Contents after = await pair.Client.Run(() => InGame.ReadContents(o.Prefab));
            Report.Note($"client holds {after.Count} {o.Prefab} ({after.Bound} bound to the host); inventory: {after.All}");
            Expect.Equal(want, after.Count, $"{o.Prefab} in the client's inventory");
            Expect.Equal(want, after.Bound, $"{o.Prefab} in the client's inventory bound to the host");
        }

        public sealed class Contents
        {
            public int Count, Bound;
            public string All;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Client: the inventory by prefab, with how many of the prefab are bound to the
            /// host (NetId != 0; the others are marked * in All).</summary>
            [Step]
            public static Contents ReadContents(string prefab)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                var rows = new List<string>();
                for (int s = 0; s < inv.Capacity; s++)
                {
                    var go = inv.PeekItemAtSlot(s, false);
                    if (go == null) continue;
                    rows.Add(PlayerInventory.PrefabOf(go).Replace("(Clone)", "") + (PlayerInventory.NetIdOf(go) != 0 ? "" : "*"));
                }
                return new Contents { Count = rows.Count(r => r.StartsWith(prefab)), Bound = rows.Count(r => r == prefab), All = string.Join(",", rows) };
            }
        }
    }
}
