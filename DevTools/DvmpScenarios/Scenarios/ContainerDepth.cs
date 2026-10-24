using System;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A deposit holds the graph to the depth the load holds a save to (OI-CON-15,
    /// docs/design/items/containers.md, "Depth"): a box may sit eight boxes deep inside the outermost
    /// one, and a ninth level is refused at deposit, so no save is made that the load would refuse.
    /// The player (WHO) nests BOXES boxes, the last inside the one before, each deposit into a box
    /// they hold: the chain is eight deep. Putting it into one more box would make nine, and is
    /// refused (<c>container-depth-exceeded</c>); the host's graph checks whole, as the load would.
    /// CONTAINER is a box of a kind that takes its own kind, or the first the game has.
    /// </summary>
    [Scenario("container-depth", "A box nested eight deep is accepted and a ninth level is refused at deposit, so the graph always loads (OI-CON-15)")]
    [Budget(900)]
    public sealed class ContainerDepth : Scenario<ContainerDepth.Options>
    {
        public sealed class Options
        {
            /// <summary>The box's prefab, tried first; another that takes its own kind otherwise.</summary>
            public string Container = "ItemContainerToolbox";
            /// <summary>Who nests the boxes: client or host.</summary>
            public string Who = "client";
            /// <summary>How deep the graph may go (ContainerLimits.MaximumDepth).</summary>
            public int Depth = 8;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who == "host" && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            string prefab = await pair.Host.Run<string>(s => Boxes.SelfNesting(s, o.Container));
            if (prefab.Length == 0) throw new HarnessException("no container the game has takes a box of its own kind");
            Report.Note($"boxes: {prefab}");
            // boxes[0] is the outermost; boxes[1..Depth+1] become the chain, boxes[Depth+1] innermost.
            ushort[] boxes = new ushort[o.Depth + 2];
            for (int i = 0; i < boxes.Length; i++)
                boxes[i] = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, prefab, o.Who));
            Peer who = o.Who == "host" ? pair.Host : pair.Client;

            // Inside out: each box goes into the one before while that one is the outermost, so the
            // chain under boxes[1] is Depth deep once the last of these is in.
            for (int i = boxes.Length - 1; i >= 2; i--)
            {
                string result = await who.Run<string>(s => Boxes.Deposit(s, boxes[i - 1], boxes[i]), 120);
                Report.Note($"box {i} into box {i - 1}: {result}");
                Require.Equal(Boxes.Accepted, result, $"box {i} into box {i - 1} ({boxes.Length - 1 - i} deep inside it)");
            }
            Expect.Equal("ok", await pair.Host.Run(() => Boxes.GraphCheck()), "the graph with a chain at the limit");

            string tooDeep = await who.Run<string>(s => Boxes.Deposit(s, boxes[0], boxes[1]), 120);
            Report.Note($"the chain into one more box: {tooDeep}");
            Expect.Equal("container-depth-exceeded", tooDeep, "a ninth level, refused at deposit");
            Expect.That(await who.Run(() => Owned.InInventory(boxes[1])), "the refused chain is still in the player's inventory");
            Expect.Equal(0, await who.Run<int>(s => Boxes.Rows(s, boxes[0])), "the outermost box's rows after the refusal");
            Expect.Equal("ok", await pair.Host.Run(() => Boxes.GraphCheck()), "the graph after the refusal");
        }
    }
}
