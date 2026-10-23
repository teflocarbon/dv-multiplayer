// The state every run starts from, and what a run must not leave behind. Run by the framework
// (Framework/Program.cs) before and after every scenario, not by scenarios themselves.
using System.Collections;
using System.Linq;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    public static partial class World
    {
        /// <summary>
        /// This game's player back where every run starts, empty-handed: what a held item would
        /// otherwise carry into the next run. Mugs (the test item every scenario spawns) are dropped
        /// out of the inventory, a synced drop: after a few gates a client's inventory was full of
        /// them (22) and every "put it in the inventory" step failed. Not on a dedicated host: it has
        /// no player.
        /// </summary>
        [Step]
        public static IEnumerator ResetPlayer(float x, float z)
        {
            if (Hands.Held != null) yield return Hands.Drop();
            Hands.ReleaseControl();
            var inventory = DV.InventorySystem.Inventory.Instance;
            if (inventory != null)
            {
                foreach (var go in inventory.GetItemsArray(false).Where(g => g != null && g.name.StartsWith("Mug")).ToList())
                {
                    inventory.DropItemFromHandsOrInventory(go);
                    yield return new WaitForSecondsRealtime(0.1f);
                }
            }
            yield return LocalPlayer.TeleportToGround(x, z);
        }

        /// <summary>
        /// Host: every mug in the world destroyed (mugs left in the host's inventory failed
        /// host-player-persist's duplicate check), and the shared wallet topped up to
        /// <paramref name="money"/>: rerails, car deletes and fees spend it, the save-writing runs keep
        /// what's left, and one batch left $1000 and the next rerail failed. 0 leaves the wallet.
        /// </summary>
        [Step]
        public static IEnumerator ResetWorld(int money)
        {
            foreach (var m in Object.FindObjectsOfType<NetworkedItem>().Where(i => i != null && i.name.StartsWith("Mug")).ToList())
                Object.Destroy(m.gameObject);
            var inventory = DV.InventorySystem.Inventory.Instance;
            if (money > 0 && inventory != null && inventory.PlayerMoney < money)
                inventory.AddMoney(money - inventory.PlayerMoney);   // syncs to the clients
            yield return new WaitForSecondsRealtime(1f);
        }

        /// <summary>Cab control leases this game still holds, as "car: port net ID": a run gives every
        /// one back.</summary>
        [Step]
        public static string[] HeldLeases() =>
            Object.FindObjectsOfType<Multiplayer.Shared.Trains.NetworkedTrainCar>()
                .Select(n => (car: n, client: Multiplayer.Client.Trains.NetworkedTrainCarClient.Of(n)))
                .Where(c => c.client != null)
                .SelectMany(c => c.client.locallyOwnedPortAuthorities.Select(id => $"{c.car.name}: {id}"))
                .ToArray();
    }
}
