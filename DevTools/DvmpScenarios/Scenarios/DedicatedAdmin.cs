using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.ServicePenalty.UI;
using Multiplayer.Server.Dedicated;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A dedicated host run by its players: admin commands and the empty-server pause.
    /// 1. A player who isn't an admin can't /save.
    /// 2. Made an admin (Settings.Admins, in memory only), /save saves. /payfees is refused while the
    ///    wallet can't cover the fees; then the fees screen's request pays them once, cheapest first
    ///    until the insurance quota is reached, and insurance clears the rest.
    /// 3. When the last player leaves, the host saves and pauses; when they come back it unpauses.
    /// Sets the test save's wallet.
    /// </summary>
    [Scenario("dedicated-admin", "Admin commands on a dedicated host (/save, /payfees, the fees screen) and its save-and-pause when the last player leaves")]
    [Needs(Need.DedicatedHost, Need.RestartsGames)]
    [Budget(600)]
    public sealed class DedicatedAdmin : Scenario<DedicatedAdmin.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            string guid = await pair.Host.Run(() => InGame.PlayerGuid());
            pair.Cleanup(() => pair.Host.Run(() => InGame.SetAdmins("")));

            Report.Note("a player who isn't an admin tries /save");
            await Chat(pair, "/save");
            State s1 = await pair.Host.Run(() => InGame.State());
            Report.Note($"last save: {s1.Saved}");
            Expect.That(!s1.Saved.Contains("/save"), "a non-admin's /save didn't save the game", s1.Saved);

            Report.Note("made an admin, /save");
            await pair.Host.Run(() => InGame.SetAdmins(guid));
            await Chat(pair, "/save");
            State s2 = await pair.Host.Run(() => InGame.State());
            Report.Note($"last save: {s2.Saved}");
            Expect.That(s2.Saved.Contains("/save by"), "an admin's /save saved", s2.Saved);

            Report.Note($"/payfees: {s2.Fees} payable, ${s2.Cost} to pay with insurance");
            if (s2.Fees != 0)
            {
                // Not enough in the wallet: refused, nothing paid. (Nothing to refuse when insurance covers it all.)
                if (s2.Cost != 0)
                {
                    double low = Math.Round(s2.Cost / 2, 2);
                    await pair.Host.Run(() => InGame.SetMoney(low));
                    await Chat(pair, "/payfees");
                    State s3 = await pair.Host.Run(() => InGame.State());
                    Report.Note($"with ${low}: fees left {s3.Fees}, wallet ${s3.Money}");
                    Expect.Equal(s2.Fees, s3.Fees, "fees left after /payfees from a wallet that couldn't cover them");
                    Expect.That(Math.Abs(s3.Money - low) < 0.01, "a refused /payfees took no money", $"wallet {s3.Money}, was {low}");
                }
                // Enough, from the fees screen: paid once, and insurance clears the rest.
                double rich = Math.Round(s2.Cost + 1000, 2);
                await pair.Host.Run(() => InGame.SetMoney(rich));
                Report.Note("fees screen: " + await pair.Client.Run(() => InGame.PayFromFeesScreen()));
                await Task.Delay(3000);   // the server pays and answers
                State s4 = await pair.Host.Run(() => InGame.State());
                Report.Note($"with ${rich}: fees left {s4.Fees}, wallet ${s4.Money}");
                Expect.Zero(s4.Fees, "fees left after the fees screen paid them");
                Expect.That(Math.Abs(rich - s4.Money - s2.Cost) < 0.01, "the wallet went down by the fees exactly once", $"by {rich - s4.Money}, the fees {s2.Cost}");
            }

            Report.Note("the last player leaves");
            await pair.Client.Quit();
            await Task.Delay(4000);   // the host notices the departure, saves and pauses
            State s5 = await pair.Host.Run(() => InGame.State());
            Report.Note($"players {s5.Players}, paused {s5.Paused}, last save: {s5.Saved}");
            Expect.That(s5.Paused, "the host paused with nobody online");
            Expect.That(s5.Saved.Contains("the last player left"), "the host saved when the last player left", s5.Saved);

            Report.Note("the player comes back");
            await pair.Rejoin();
            State s6 = await pair.Host.Run(() => InGame.State());
            Report.Note($"players {s6.Players}, paused {s6.Paused}");
            Expect.That(!s6.Paused, "the host unpaused with a player online");
        }

        // A chat line from the client, and time for the host to act on it.
        private static async Task Chat(Pair pair, string line)
        {
            await pair.Client.Run(() => InGame.Chat(line));
            await Task.Delay(3000);
        }

        public sealed class State
        {
            /// <summary>When the host last saved and why ("never" before the first).</summary>
            public string Saved;
            public bool Paused;
            public double Money, Cost;
            public int Fees, Players;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: its last save, whether it's paused, the wallet, the payable fees and what paying them
            /// costs with insurance (cheapest first until the quota is reached, then insurance clears the
            /// rest), and how many players are in the game.
            /// </summary>
            [Step]
            public static State State()
            {
                var debts = DV.ServicePenalty.CareerManagerDebtController.Instance;
                debts.RefreshExistingDebtsState();
                var payable = debts.currentNonZeroPricedDebts.Where(d => d.IsPayable).ToList();
                var q = debts.feeQuota;
                double cost = 0;
                if (!(q.InsuranceUsed && q.QuotaReached))
                    foreach (double p in payable.Select(d => (double)d.GetTotalPrice()).OrderBy(p => p))
                    {
                        if (q.InsuranceUsed && cost >= q.LeftToReachQuota) break;
                        cost += p;
                    }
                return new State
                {
                    Saved = (DedicatedHost.LastSave?.ToString("o") ?? "never") + " " + DedicatedHost.LastSaveReason,
                    Paused = DV.AppUtil.Instance.IsTimePaused,
                    Money = DV.InventorySystem.Inventory.Instance.PlayerMoney,
                    Fees = payable.Count,
                    Cost = cost,
                    Players = NetworkLifecycle.Instance.Server.GameplayPlayerCount
                };
            }

            /// <summary>Host: the (only) player's GUID, as Settings.Admins lists admins.</summary>
            [Step]
            public static string PlayerGuid() => NetworkLifecycle.Instance.Server.GameplayPlayers.First().Guid.ToString();

            /// <summary>Host: the admins, in memory only.</summary>
            [Step]
            public static void SetAdmins(string admins) => Multiplayer.Multiplayer.Settings.Admins = admins;

            [Step]
            public static void SetMoney(double money) => DV.InventorySystem.Inventory.Instance.SetMoney(money);

            /// <summary>Client: a chat line or command, as typed.</summary>
            [Step]
            public static void Chat(string line) => NetworkLifecycle.Instance.LocalSession.SendChat(line);

            /// <summary>
            /// Client: pays the fees as a player does at the career manager: the fees screen (a client's
            /// offers to pay them all, CareerManagerFeesScreenPatch) and its confirmation. Result: what
            /// the screen showed.
            /// </summary>
            [Step]
            public static string PayFromFeesScreen()
            {
                var screen = Resources.FindObjectsOfTypeAll<CareerManagerFeesScreen>().First(s => s.gameObject.scene.IsValid());
                screen.Activate(null);
                if (screen.screenSwitcher.CurrentScreen as object != screen.confirmActionScreen)
                    return "no confirmation offered: " + screen.screenSwitcher.CurrentScreen;
                screen.confirmActionScreen.HandleInputAction(InputAction.Confirm);
                return "confirmed";
            }
        }
    }
}
