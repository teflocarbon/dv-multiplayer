using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Stations;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The host's player at a service station's cash register (one with modules, not a shop's) with
    /// an empty basket and cash already deposited, a client watching (the host-follower family:
    /// docs/design/host-local-session.md). The host pays cash in: nothing is owed, so nothing may
    /// change on either game, the host's wallet keeps its money, and the request finishes on the
    /// server's answer rather than a timeout. Then the host presses Buy: vanilla refuses an empty
    /// basket, and both games must keep the deposited cash (the host's buy used to be replayed on
    /// clients whether or not it happened, emptying their register). Then the client presses Buy: the
    /// host refuses it, and the client must keep the cash too (the refusal used to empty the client's
    /// register). Needs a player host: nobody plays on a dedicated one.
    /// </summary>
    [Scenario("host-register-empty-basket", "The host pays into and presses Buy on a service register with an empty basket, then the client presses Buy: nothing changes on either game")]
    [Needs(Need.PlayerHost)]
    [Budget(300)]
    public sealed class HostRegisterEmptyBasket : Scenario<HostRegisterEmptyBasket.Options>
    {
        public sealed class Options { }

        private const double Seed = 50, Pay = 20;

        protected override async Task Run(Pair pair, Options o)
        {
            // The service station by the shop scenario's station.
            await Places.Shop(pair, 3f);
            Found f = await pair.Host.Run(() => InGame.Find(Places.ShopX, Places.ShopZ));
            ushort reg = f.Register;
            Report.Note($"register {reg} ({f.Name}) at ({f.X}, {f.Z})");
            // Both players within reach of the register (the server checks where each last said it was).
            await Places.Both(pair, f.X + 1f, f.Z, f.X - 1f, f.Z + 1f, 3f);
            // Leave the register empty again.
            pair.Cleanup(() => pair.Host.Run<Register>(s => InGame.Seed(s, reg, 0)));

            Report.Note($"${Seed} deposited, basket empty");
            await pair.Host.Run<Register>(s => InGame.Seed(s, reg, Seed));
            await Task.Delay(1000);
            var (h, c) = await Show(pair, reg, "before");
            Require.That(h.Units == 0 && c.Units == 0, "the basket is empty on both", $"host {h.Units}, client {c.Units}");
            Require.That(Near(h.Deposited, Seed) && Near(c.Deposited, Seed), "the deposit reached both games", $"host ${h.Deposited}, client ${c.Deposited}, expected ${Seed}");
            double wallet0 = h.Wallet;

            Report.Note($"host pays ${Pay} in");
            Register d = await pair.Host.Run<Register>(s => InGame.Deposit(s, reg, Pay));
            await Task.Delay(1000);
            (h, c) = await Show(pair, reg, "after");
            Report.Note($"host's request waited {d.BusyFor:0.##} s for the server; wallet {wallet0} -> {h.Wallet}");
            Expect.That(Near(h.Deposited, Seed), "the host's register kept its deposit", $"${h.Deposited}, expected ${Seed}");
            Expect.That(Near(c.Deposited, Seed), "the client's register kept its deposit", $"${c.Deposited}, expected ${Seed}");
            Expect.That(Near(h.Wallet, wallet0), "the host's wallet kept its money", $"{wallet0} -> {h.Wallet}");
            Expect.That(!h.Busy, "the host's request finished");
            Expect.That(d.BusyFor < 1.5, "the host's request finished on the server's answer, not the timeout", $"waited {d.BusyFor:0.##} s, expected under 1.5");

            Report.Note("host presses Buy");
            await pair.Host.Run<Register>(s => InGame.Buy(s, reg));
            await Task.Delay(1000);
            (h, c) = await Show(pair, reg, "after");
            Expect.That(Near(h.Deposited, Seed), "the host's register kept its deposit", $"${h.Deposited}, expected ${Seed}");
            Expect.That(Near(c.Deposited, Seed), "the client kept its register's deposit for a buy that didn't happen", $"${c.Deposited}, the host's ${h.Deposited}");

            Report.Note("client presses Buy");
            await pair.Client.Run<Register>(s => InGame.Buy(s, reg));
            await Task.Delay(2000);
            (h, c) = await Show(pair, reg, "after");
            Expect.That(Near(h.Deposited, Seed), "the host's register kept its deposit", $"${h.Deposited}, expected ${Seed}");
            Expect.That(Near(c.Deposited, Seed), "the client kept its register's deposit on the host's refusal", $"${c.Deposited}, the host's ${h.Deposited}");
            Expect.That(!c.Busy, "the client's request finished");
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

        private static async Task<(Register, Register)> Show(Pair pair, ushort reg, string when)
        {
            Register h = await pair.Host.Run<Register>(s => InGame.State(s, reg)), c = await pair.Client.Run<Register>(s => InGame.State(s, reg));
            Report.Note($"{when}: host deposited ${h.Deposited}, basket {h.Units}, busy {h.Busy}; client deposited ${c.Deposited}, basket {c.Units}");
            return (h, c);
        }

        public sealed class Found
        {
            public ushort Register;
            public string Name;
            public float X, Z;
        }

        public sealed class Register
        {
            public double Deposited, Wallet;
            public float Units;
            /// <summary>A request is waiting for the server's answer.</summary>
            public bool Busy;
            /// <summary>How long a deposit kept the register busy (s).</summary>
            public float BusyFor;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the module register (not a shop's) nearest an absolute x/z.</summary>
            [Step]
            public static Found Find(float x, float z)
            {
                var nearest = NetworkedCashRegisterWithModules.All.Where(r => r != null && r.CashRegister != null && !r.IsShopRegister)
                    .OrderBy(r => { var a = Dev.Abs(r.transform.position); return new Vector2(a.x - x, a.z - z).magnitude; }).First();
                var abs = Dev.Abs(nearest.transform.position);
                return new Found { Register = nearest.NetId, Name = nearest.transform.parent?.parent?.name + " - " + nearest.transform.parent?.name, X = abs.x, Z = abs.z };
            }

            private static NetworkedCashRegisterWithModules Get(ushort register)
            {
                NetworkedCashRegisterWithModules.Get(register, out NetworkedCashRegisterWithModules reg);
                return reg;
            }

            private static Register Read(NetworkedCashRegisterWithModules reg, float busyFor = 0f) => new()
            {
                Deposited = reg.CashRegister.DepositedCash,
                Units = reg.CashRegister.TotalUnitsInBasket(),
                Busy = reg.IsBusy,
                BusyFor = busyFor,
                Wallet = DV.InventorySystem.Inventory.Instance.PlayerMoney
            };

            /// <summary>Host: cash deposited in the register, as a player's deposit leaves it: set on the
            /// host and sent to the players (SetFunds).</summary>
            [Step]
            public static IEnumerator Seed(Step<Register> step, ushort register, double amount)
            {
                var reg = Get(register);
                reg.CashRegister.SetCash(amount);
                Multiplayer.Server.Session.NetworkServer.Instance.SendCashRegisterAction(new CommonCashRegisterWithModulesActionPacket
                {
                    NetId = reg.NetId,
                    Action = CashRegisterAction.SetFunds,
                    Amount = amount
                });
                yield return new WaitForSecondsRealtime(0.5f);
                step.Result = Read(reg);
            }

            /// <summary>Host: the host's player pays cash in, as DV does when money touches the register
            /// (taken from the wallet, then CashRegisterBase.AddCash), and waits for the request to finish.</summary>
            [Step]
            public static IEnumerator Deposit(Step<Register> step, ushort register, double amount)
            {
                var reg = Get(register);
                DV.InventorySystem.Inventory.Instance.RemoveMoney(amount);
                reg.CashRegister.AddCash(amount);
                float start = Time.realtimeSinceStartup;
                yield return null;
                while (reg.IsBusy && Time.realtimeSinceStartup - start < 5f)
                    yield return null;
                step.Result = Read(reg, Time.realtimeSinceStartup - start);
            }

            /// <summary>This game's player presses Buy.</summary>
            [Step]
            public static IEnumerator Buy(Step<Register> step, ushort register)
            {
                var reg = Get(register);
                reg.CashRegister.OnBuyPressed();
                yield return new WaitForSecondsRealtime(0.5f);
                step.Result = Read(reg);
            }

            /// <summary>Deposited cash, units in the basket, whether a request is waiting, the wallet.</summary>
            [Step]
            public static IEnumerator State(Step<Register> step, ushort register)
            {
                step.Result = Read(Get(register));
                yield break;
            }
        }
    }
}
