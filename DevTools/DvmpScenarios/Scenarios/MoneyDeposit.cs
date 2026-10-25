using System;
using System.Collections;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A money deposit ends its entry <c>MoneyDeposited</c>, with nothing replacing it (OI-LIN-05;
    /// docs/design/items/ledger.md, "Births and ends"). The host makes a banknote worth $1,234 owned by
    /// the client, which picks it up and puts it in its inventory, as vanilla spends money: the shared
    /// wallet gains its value on both games, the note is gone and its entry ended <c>MoneyDeposited</c>.
    /// On a player host the host player then does the same with one of their own (vanilla's own
    /// inventory destroy used to end it <c>DestroyedFromInventory</c>).
    /// </summary>
    [Scenario("money-deposit", "A banknote spent by a client or the host player ends its entry MoneyDeposited and pays the shared wallet (OI-LIN-05)")]
    [Budget(300)]
    public sealed class MoneyDeposit : Scenario<MoneyDeposit.Options>
    {
        public sealed class Options
        {
            /// <summary>The note's value.</summary>
            public double Amount = 1234;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(5000);
            await Spend(pair, o, "client");
            if (pair.Dedicated)
            {
                Report.Note("the host player's note: skipped, a dedicated host has no player");
                return;
            }
            await Spend(pair, o, "host");
        }

        private static async Task Spend(Pair pair, Options o, string who)
        {
            ushort note = await pair.Host.Run<ushort>(s => InGame.SpawnNote(s, who, o.Amount));
            await pair.Host.Run(() => Ownership.Give(note, who));
            string id = await pair.Host.Run(() => Owned.IdOf(note));
            pair.Cleanup(() => pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { note }), 60));
            Require.That(id.Length > 0, $"the {who}'s note has an entry");
            double before = await pair.Host.Run(() => Jobs.Wallet());

            Peer spender = who == "host" ? pair.Host : pair.Client;
            string spent = await spender.Run<string>(s => InGame.PickUpAndStash(s, note), 60);
            Report.Note($"the {who} put note {note} in its inventory: {spent}");

            await Until.Value(() => pair.Host.Run(() => Items.Record(note)), r => !r.Known, 20, $"the {who}'s note is gone on the server");
            double after = await pair.Host.Run(() => Jobs.Wallet());
            Expect.That(Math.Abs(after - before - o.Amount) < 0.01, $"the wallet gained the {who}'s note's value", $"{before:N2} -> {after:N2}");
            await Until.Value(() => pair.Client.Run(() => Jobs.Wallet()), w => Math.Abs(w - after) < 0.01, 20, "the client's wallet shows it");
            Expect.That((await pair.Client.Run(() => Items.Existing(new[] { note }))).Length == 0, $"the client has no copy of the {who}'s note");
            LedgerEntry e = await pair.Host.Run(() => Owned.Entry(id));
            Report.Note($"the {who}'s note's entry: {e}");
            Expect.That(e.Known && !e.Live, $"the {who}'s note's entry has ended", e.ToString());
            Expect.Equal("MoneyDeposited", e.Ended, $"why the {who}'s note's entry ended");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: a banknote worth <paramref name="amount"/> beside a player ("host" or "client"). Result: its net ID.</summary>
            [Step]
            public static IEnumerator SpawnNote(Step<ushort> step, string near, double amount)
            {
                Multiplayer.Shared.Players.NetworkedPlayer client = null;
                if (near != "host")
                    yield return Wait.Until(() => (client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().Length > 0
                        ? UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>()[0] : null) != null, 60f, "the client's avatar");
                Vector3 at = near == "host" ? PlayerManager.PlayerTransform.position : client.transform.position;
                var go = (GameObject)UnityEngine.Object.Instantiate(Resources.Load("Banknotes", typeof(GameObject)),
                    at + Vector3.up * 0.6f + Vector3.right * 0.8f, Quaternion.identity, WorldMover.OriginShiftParent);
                go.GetComponent<Banknotes>().Amount = amount;
                NetworkedItem item = go.GetComponent<NetworkedItem>();
                yield return Wait.Until(() => item != null && item.NetId != 0, 5f, "the note to get a net ID");
                for (int i = 0; i < 60; i++) yield return null;
                step.Result = item.NetId;
            }

            /// <summary>Walks to the note, picks it up and puts it in the inventory, which spends it. Result: what this game saw.</summary>
            [Step]
            public static IEnumerator PickUpAndStash(Step<string> step, ushort netId)
            {
                yield return Items.Arrive(netId);
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(netId);
                DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
                for (int i = 0; i < 90; i++) yield return null;
                step.Result = item == null ? "the note is gone" : "the note is still here, " + item.GetItemState();
            }
        }
    }
}
