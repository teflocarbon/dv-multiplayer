using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Multiplayer.Client.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A join row kept as an object keeps its saved state (OI-COLD-13; docs/design/items/inventory.md,
    /// "Rows on the client"; essentials.md, "Claim slots"). The real case is an item on an enabled VR
    /// belt slot (OI-COLD-14), which the harness can't fake, so the run makes the same choice for one
    /// row: the client opens a lighter's lid and stashes it, quits and joins again; the host holds the
    /// joiner's rows back (a Harmony prefix on <c>NetworkServer.SendInventoryRows</c>) until the
    /// client's new game has a prefix on <c>ClientItemManager.ReceiveAsStandIn</c> refusing a stand-in
    /// for the lighter's new NetId, as it is for a belt row. The rows then go: the lighter must be an
    /// object in the client's game with the state it was stashed with (its lid open), not its prefab's.
    /// Without the fix (<c>FillFromRows</c> falling back to <c>ProcessReceivedAsClient</c>, which is
    /// given no state, instead of <c>MadeFromRow</c>) it comes back fresh, its lid shut, and the state
    /// check fails. A remote client only: the host's own player's rows are drawn in process
    /// (<c>HostInventory.Draw</c>); either kind of host.
    /// </summary>
    [Scenario("join-row-object", "A join row the client keeps as an object (as a VR belt row) is made with its saved state, not fresh (OI-COLD-13)")]
    [Needs(Need.RestartsGames)]
    [Budget(300)]
    public sealed class JoinRowObject : Scenario<JoinRowObject.Options>
    {
        public sealed class Options
        {
            /// <summary>The item, changed in the hand as stash-state changes it (a lighter's lid, a lantern's wick).</summary>
            public string Prefab = "lighter";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort netId = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            StashState.Kept kept = await pair.Client.Run<StashState.Kept>(s => StashState.InGame.ChangeAndStash(s, netId, 0), 90);
            Report.Note($"{o.Prefab} {netId}, stashed: {kept}");
            Require.That(kept.StandIn, $"the {o.Prefab} is a stand-in in the client's slot");
            Require.That(kept.State.Length > 0, $"the {o.Prefab} has a saved state");
            string id = await pair.Host.Run(() => Owned.IdOf(netId));

            await pair.Client.Quit();
            await Task.Delay(4000);
            await pair.Host.Run(() => InGame.HoldRows(true));
            pair.Cleanup(() => pair.Host.Run(() => InGame.HoldRows(false)));
            await pair.Rejoin();
            int held = await Until.Value(() => pair.Host.Run(() => InGame.Held()), v => v >= 0, 60, "the joiner's rows, held back on the host");
            ushort now = await pair.Host.Run(() => Owned.NetIdOf(id));
            Require.That(now != 0, $"the server has the {o.Prefab} again, as a row");
            await pair.Host.Run(() => Ledger.Track(now));
            pair.Cleanup(async () => await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { now }), 30));
            Report.Note($"{held} row(s) held back; the {o.Prefab} is {now} now");

            await pair.Client.Run(() => InGame.KeepAsObject(now));
            pair.Cleanup(() => pair.Client.Run(() => InGame.KeepAsObject(0)));
            Require.Equal(held, await pair.Host.Run(() => InGame.Release()), "the rows the host sent on");
            Seen seen = await pair.Client.Run<Seen>(s => InGame.Look(s, now), 30);
            Report.Note($"the client after its rows: {seen}");
            // The prefix must have turned the row away from a stand-in, or the run proves nothing.
            Require.That(seen.Refused > 0, $"the client's game kept the {o.Prefab}'s row as an object, as a belt row", seen.ToString());
            Require.That(seen.Object || seen.StandIn, $"the client has the {o.Prefab}", seen.ToString());
            if (!seen.Object)
                Report.Note($"the {o.Prefab} was put away since as a stand-in: its state is the object's it was made as");
            Expect.Equal(kept.State, seen.State, $"the {o.Prefab}'s state, made from its row, against its state when stashed");
        }

        public sealed class Seen
        {
            public bool Object, StandIn, InSlot;
            public int Slot = -1;
            /// <summary>How many times the client's game was refused a stand-in for the item (the prefix).</summary>
            public int Refused;
            public string State = "";

            public override string ToString() =>
                $"{(Object ? "an object" : StandIn ? "a stand-in" : "nothing")}{(InSlot ? " in slot " + Slot : "")}, " +
                $"refused a stand-in {Refused} time(s), state {State}";
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string HostHarmonyId = "dvmp.scenarios.join-row-object.host";
            private const string ClientHarmonyId = "dvmp.scenarios.join-row-object.client";
            private static bool holding, releasing;
            private static ServerPlayer heldFor;
            private static List<ItemUpdate> heldRows;
            private static List<string> heldStates;
            private static ushort keep;
            private static int refused;

            /// <summary>
            /// Host: holds back the next whole inventory (a join's rows) sent to a remote player (on),
            /// or stops, sending on any it still holds (off).
            /// </summary>
            [Step]
            public static void HoldRows(bool on)
            {
                var harmony = new Harmony(HostHarmonyId);
                if (!on && heldRows != null)
                    Release();
                harmony.UnpatchAll(HostHarmonyId);
                holding = on;
                heldFor = null;
                heldRows = null;
                heldStates = null;
                if (on)
                    harmony.Patch(AccessTools.Method(typeof(NetworkServer), nameof(NetworkServer.SendInventoryRows)),
                        prefix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(HoldPrefix))));
            }

            public static bool HoldPrefix(ServerPlayer recipient, List<ItemUpdate> rows, List<string> states, bool full)
            {
                if (!holding || releasing || !full || recipient == null || recipient == NetworkServer.Instance.LocalPlayer)
                    return true;
                heldFor = recipient;
                heldRows = rows != null ? new List<ItemUpdate>(rows) : new List<ItemUpdate>();
                heldStates = states != null ? new List<string>(states) : new List<string>();
                holding = false;
                return false;
            }

            /// <summary>Host: how many rows it holds back, or -1 while it holds none.</summary>
            [Step]
            public static int Held() => heldRows?.Count ?? -1;

            /// <summary>Host: sends on the rows it held. Result: how many.</summary>
            [Step]
            public static int Release()
            {
                if (heldRows == null) return -1;
                int count = heldRows.Count;
                releasing = true;
                try { NetworkServer.Instance.SendInventoryRows(heldFor, heldRows, heldStates, true); }
                finally
                {
                    releasing = false;
                    heldRows = null;
                    heldStates = null;
                    heldFor = null;
                }
                return count;
            }

            /// <summary>Client: the item's rows and updates never make it a stand-in, as a VR belt row isn't (0 stops).</summary>
            [Step]
            public static void KeepAsObject(ushort netId)
            {
                var harmony = new Harmony(ClientHarmonyId);
                harmony.UnpatchAll(ClientHarmonyId);
                keep = netId;
                refused = 0;
                if (netId != 0)
                    harmony.Patch(AccessTools.Method(typeof(ClientItemManager), "ReceiveAsStandIn"),
                        prefix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(KeepPrefix))));
            }

            public static bool KeepPrefix(ItemUpdate update, ref bool __result)
            {
                if (keep == 0 || update == null || update.ItemNetId != keep)
                    return true;
                refused++;
                __result = false;
                return false;
            }

            /// <summary>
            /// Client: waits for the item (an object or a stand-in), a second more for its state to
            /// load, and reads it. Result: what it is, where, and its state against a fresh one's.
            /// </summary>
            [Step]
            public static IEnumerator Look(Step<Seen> step, ushort netId)
            {
                NetworkedItem item = null;
                yield return Wait.Until(() => NetworkedItem.TryGet(netId, out item) && item != null || InventoryStandIn.TryGet(netId, out _),
                    20f, $"item {netId} from its row");
                yield return new WaitForSecondsRealtime(1.5f);
                var seen = new Seen
                {
                    Object = NetworkedItem.TryGet(netId, out item) && item != null,
                    StandIn = InventoryStandIn.TryGet(netId, out InventoryStandIn standIn),
                    InSlot = PlayerInventory.Holds(netId),
                    Slot = PlayerInventory.Slot(netId)
                };
                // An object put away by this game since is a stand-in with the object's state.
                seen.State = seen.Object ? StashState.InGame.Normal(ItemStateText.Capture(item.gameObject))
                    : seen.StandIn ? StashState.InGame.Normal(StandInAmmo.Without(standIn.State)) : "";
                seen.Refused = refused;
                step.Result = seen;
            }
        }
    }
}
