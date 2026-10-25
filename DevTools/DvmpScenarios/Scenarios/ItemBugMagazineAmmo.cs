using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using DV.Items;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Item bug CN1 (docs/research/item-bug-review.md), fixed in R5 C7: a client loads each vanilla
    /// magazine (a boombox's cassette, a sprayer's paint can, a soldering tool's spool) and picks the
    /// tool up. The ammo's home is the tool's magazine (docs/design/items/containers.md, "Ammo in a
    /// magazine"), so it stays in it on the client after the hold, and the host's copy of the tool
    /// holds it too. Before C7 the client reported the ammo dropped in the world at its next
    /// observation, and the server's copy pulled it out of the client's magazine.
    /// </summary>
    [Scenario("item-bug-magazine-ammo", "CN1: a client's ammo in a magazine (cassette, paint can, spool) stays in it after the tool is picked up, on both games")]
    [Budget(300)]
    public sealed class ItemBugMagazineAmmo : Scenario<ItemBugMagazineAmmo.Options>
    {
        public sealed class Options
        {
            /// <summary>How long the client holds each loaded tool, in seconds.</summary>
            public int Hold = 5;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string[] magazines = await pair.Client.Run(() => StashState.InGame.Magazines());
            Report.Note("magazines: " + string.Join(", ", magazines));
            foreach (string pairing in magazines)
            {
                string tool = pairing.Split('/')[0], ammo = pairing.Split('/')[1];
                ushort toolId = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, tool, "client"));
                ushort ammoId = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, ammo, "client"));
                string[] held = await pair.Client.Run<string[]>(s => InGame.LoadAndHold(s, toolId, ammoId, o.Hold), 60 + o.Hold);
                Report.Note($"{tool} {toolId} with {ammo} {ammoId}: loaded [{held[0]}], after {o.Hold} s in the hand [{held[1]}]");
                Expect.That(held[0].Length > 0, $"{tool}: its ammo in it once loaded", "empty");
                Expect.Equal(held[0], held[1], $"{tool}: its ammo still in it after {o.Hold} s in the hand");
                string onHost = await pair.Host.Run(() => InGame.HoldsOf(toolId));
                Expect.Equal(held[0], onHost, $"{tool}: the host's copy holds the same ammo");
                await pair.Client.Run(() => Items.Drop(), 10);
            }
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Client: loads the ammo into the tool where it lies (as a player reloads it), picks the
            /// tool up and holds it. Result: what its magazine holds once loaded, and after the hold.
            /// </summary>
            [Step]
            public static IEnumerator LoadAndHold(Step<string[]> step, ushort toolId, ushort ammoId, int seconds)
            {
                if (Hands.Held != null) yield return Hands.Drop();
                yield return Items.Arrive(ammoId);
                yield return Items.Arrive(toolId);
                NetworkedItem.TryGet(toolId, out NetworkedItem tool);
                NetworkedItem.TryGet(ammoId, out NetworkedItem ammo);
                var magazine = tool.GetComponentInChildren<ItemMagazine>(true);
                yield return LocalPlayer.GoTo(ammo);
                yield return Hands.Pickup(ammoId);
                // Let go of it first: the magazine's AddItem doesn't end the grab.
                ammo.Item.ForceEndInteraction();
                yield return Wait.Until(() => Hands.Held == null, 5f, "the ammo out of the hand");
                // One that came loaded keeps its own ammo (the spare is the run's, destroyed at its end).
                if (magazine[0] == null && !magazine.AddItem(ammo.gameObject, 0))
                    throw new Exception($"the magazine won't take ammo {ammoId} (valid {magazine.ValidItem(ammo.gameObject)})");
                for (int i = 0; i < 30; i++) yield return null;
                string loaded = Holds(magazine);
                yield return LocalPlayer.GoTo(tool);
                yield return Hands.Pickup(toolId);
                yield return new WaitForSecondsRealtime(seconds);
                step.Result = new[] { loaded, Holds(magazine) };
            }

            /// <summary>What this game's copy of a tool's magazine holds, "" for none or no tool.</summary>
            [Step]
            public static string HoldsOf(ushort toolId)
            {
                if (!NetworkedItem.TryGet(toolId, out NetworkedItem tool) || tool == null) return "";
                var magazine = tool.GetComponentInChildren<ItemMagazine>(true);
                return magazine == null ? "" : Holds(magazine);
            }

            private static string Holds(ItemMagazine magazine) => string.Join(", ",
                Enumerable.Range(0, magazine.Capacity).Select(i => magazine[i]).Where(a => a != null).Select(a => a.name.Replace("(Clone)", "")));
        }
    }
}
