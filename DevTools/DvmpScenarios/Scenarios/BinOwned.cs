using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Bins and dumpsters (OI-VAN-02, D10; docs/design/items/vanilla-storage.md, "Bins"). The client
    /// carries a mug of its own to the nearest dumpster and drops it in: the server takes it (it is
    /// binned, not collected), and once everyone is 120 m off the bin's destroy ends its entry
    /// <c>Discarded</c>. A mug nobody owns goes the same way, with no entry. On a player host the
    /// client then drops the host player's mug in: refused, it lies beside the bin and its entry lives.
    /// </summary>
    [Scenario("bin-owned", "An owned item binned by its owner is destroyed and its entry ends Discarded; an unowned one goes too; someone else's is refused beside the bin (OI-VAN-02)")]
    [Budget(600)]
    public sealed class BinOwned : Scenario<BinOwned.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(5000);
            // The client's own mug and one nobody owns, into the bin.
            ushort own = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(own, "client"));
            string ownId = await pair.Host.Run(() => Owned.IdOf(own));
            ushort nobodys = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "client"));
            pair.Cleanup(() => pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { own, nobodys }), 60));

            foreach (ushort mug in new[] { own, nobodys })
            {
                await pair.Client.Run<int>(s => Items.Stash(s, mug, true), 60);
                string bin = await pair.Client.Run<string>(s => InGame.DropInBin(s, mug), 60);
                Report.Note($"the client's drop of {mug}: {bin}");
                await Until.Value(() => pair.Host.Run(() => InGame.Binned(mug)), b => b, 20, $"the server has mug {mug} in the bin");
            }

            // Everyone walks off: the bin's destroy.
            await pair.Client.Run<Abs>(s => Walk.Away(s, 120, 0));
            if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, 120, 30));
            await Until.Value(() => pair.Host.Run(() => Items.Record(own)), r => !r.Known, 60, "the binned mug is gone on the server");
            await Until.Value(() => pair.Host.Run(() => Items.Record(nobodys)), r => !r.Known, 30, "the unowned binned mug is gone on the server");
            LedgerEntry e = await pair.Host.Run(() => Owned.Entry(ownId));
            Report.Note("the binned mug's entry: " + e);
            Expect.That(e.Known && !e.Live, "the binned mug's entry has ended", e.ToString());
            Expect.Equal("Discarded", e.Ended, "why its entry ended");
            Expect.That(!(await pair.Host.Run(() => Ownership.ById(ownId))).Lost, "the binned mug is in no Lost and Found");

            if (pair.Dedicated)
            {
                Report.Note("someone else's item: skipped, a dedicated host has no player to own one");
                return;
            }

            // The host player's mug, dropped in by the client: refused, beside the bin.
            ushort theirs = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(theirs, "host"));
            string theirsId = await pair.Host.Run(() => Owned.IdOf(theirs));
            pair.Cleanup(() => pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { theirs }), 60));
            await pair.Client.Run<int>(s => Items.Stash(s, theirs, true), 60);
            string refused = await pair.Client.Run<string>(s => InGame.DropInBin(s, theirs), 60);
            Report.Note("the client's drop of the host player's mug: " + refused);
            await Task.Delay(5000);
            Expect.That(!await pair.Host.Run(() => InGame.Binned(theirs)), "the host player's mug isn't the bin's");
            float outside = await pair.Host.Run(() => InGame.OutsideBin(theirs));
            Report.Note($"the host player's mug is {outside:F2} m outside the bin's trigger");
            Expect.That(outside > 0f, "the refused mug lies beside the bin, not in it", $"{outside:F2} m");
            await pair.Client.Run<Abs>(s => Walk.Away(s, 120, 0));
            await pair.Host.Run<Abs>(s => Walk.Away(s, 120, 30));
            await Task.Delay(15000);
            Expect.That((await pair.Host.Run(() => Items.Record(theirs))).Known, "the refused mug is still there once everyone has gone");
            Expect.That((await pair.Host.Run(() => Owned.Entry(theirsId))).Live, "the refused mug's entry lives");
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static ItemDumpster Nearest(Vector3 from) =>
                UnityEngine.Object.FindObjectsOfType<ItemDumpster>().OrderBy(d => Vector3.Distance(d.transform.position, from)).FirstOrDefault();

            /// <summary>
            /// Client: takes the held item to the nearest dumpster and lets it fall into it. Result: the
            /// dumpster and how far the item is from its trigger's centre then.
            /// </summary>
            [Step]
            public static IEnumerator DropInBin(Step<string> step, ushort netId)
            {
                ItemDumpster bin = Nearest(PlayerManager.PlayerTransform.position) ?? throw new Exception("no dumpster in this game");
                Bounds b = bin.GetComponent<Collider>()?.bounds ?? new Bounds(bin.transform.position, Vector3.one);
                Vector3 side = Dev.Abs(b.center) + new Vector3(b.extents.x + 1.2f, 0f, 0f);
                yield return LocalPlayer.TeleportToGround(side.x, side.z);
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                if (Hands.Held != item) throw new Exception($"not holding item {netId}");
                yield return Hands.Drop();
                // Into it from above, as a throw over its edge would land it.
                for (int i = 0; i < 10; i++)
                {
                    Rigidbody rb = item.Item.ItemRigidbody;
                    if (rb != null) { rb.velocity = Vector3.zero; rb.position = b.center + Vector3.up * (b.extents.y * 0.5f); }
                    item.transform.position = b.center + Vector3.up * (b.extents.y * 0.5f);
                    yield return null;
                }
                for (int i = 0; i < 120; i++) yield return null;
                step.Result = $"{bin.name}, {(item != null ? Vector3.Distance(item.transform.position, b.center).ToString("F2") + " m from its centre" : "gone")}";
            }

            /// <summary>Host: whether the server has the item in a bin.</summary>
            [Step]
            public static bool Binned(ushort netId) => Multiplayer.Server.Items.VanillaStorage.Binning.IsBinned(netId);

            /// <summary>Host: how far the item is outside the nearest dumpster's trigger (negative inside, NaN with no object).</summary>
            [Step]
            public static float OutsideBin(ushort netId)
            {
                if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null) return float.NaN;
                ItemDumpster bin = Nearest(item.transform.position);
                Collider trigger = bin != null ? bin.GetComponent<Collider>() : null;
                if (trigger == null) return float.NaN;
                Vector3 p = item.transform.position;
                return trigger.bounds.Contains(p) ? -1f : Vector3.Distance(trigger.bounds.ClosestPoint(p), p);
            }
        }
    }
}
