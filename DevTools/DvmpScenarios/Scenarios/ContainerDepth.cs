using System;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A deposit holds the graph to the depth the load holds a save to (OI-CON-15,
    /// docs/design/items/containers.md, "Depth"): a box nested deeper than the limit inside the
    /// outermost one is refused at deposit, so no save is made that the load would refuse. No vanilla
    /// box takes its own kind, so no vanilla chain reaches the real limit (8): the longest is Crate >
    /// Toolbox > Registrator. The run lowers the host's live graph's limit to 1 and puts it
    /// back after, failed or not. The player (WHO) puts the inner box into the middle one, which is
    /// accepted (one deep); putting that into the outer box would make two, and is refused
    /// (<c>container-depth-exceeded</c>); the host's graph checks whole, as the load would.
    /// CONTAINER is the middle box's prefab, tried first.
    /// </summary>
    [Scenario("container-depth", "A box nested at the (lowered) depth limit is accepted and one more level is refused at deposit, so the graph always loads (OI-CON-15)")]
    [Budget(600)]
    public sealed class ContainerDepth : Scenario<ContainerDepth.Options>
    {
        // The limit the run gives the graph (ContainerLimits.MaximumDepth, 8 in play): a vanilla chain reaches only 1.
        private const int Depth = 1;

        public sealed class Options
        {
            /// <summary>The middle box's prefab, tried first; another that has a box on each side otherwise.</summary>
            public string Container = "ItemContainerToolbox";
            /// <summary>Who nests the boxes: client or host.</summary>
            public string Who = "client";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who == "host" && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            string[] chain = (await pair.Host.Run<string>(s => Boxes.Chain(s, o.Container), 120)).Split(' ');
            if (chain.Length != 3) throw new HarnessException("no three container kinds the game has nest one in the next");
            Report.Note($"boxes: {string.Join(" > ", chain)}");
            ushort outer = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, chain[0], o.Who));
            ushort middle = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, chain[1], o.Who));
            ushort inner = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, chain[2], o.Who));
            Peer who = o.Who == "host" ? pair.Host : pair.Client;

            int played = await pair.Host.Run(() => Boxes.SetMaximumDepth(Depth));
            pair.Cleanup(() => pair.Host.Run(() => Boxes.SetMaximumDepth(played)));
            Report.Note($"the graph's depth limit: {played} -> {Depth}");

            string result = await who.Run<string>(s => Boxes.Deposit(s, middle, inner), 120);
            Report.Note($"{chain[2]} into {chain[1]}: {result}");
            Require.Equal(Boxes.Accepted, result, $"{chain[2]} into {chain[1]} ({Depth} deep inside it)");
            Expect.Equal("ok", await pair.Host.Run(() => Boxes.GraphCheck()), "the graph with a chain at the limit");

            string tooDeep = await who.Run<string>(s => Boxes.Deposit(s, outer, middle), 120);
            Report.Note($"the chain into {chain[0]}: {tooDeep}");
            Expect.Equal("container-depth-exceeded", tooDeep, "one more level, refused at deposit");
            Expect.That(await who.Run(() => Owned.InInventory(middle)), "the refused chain is still in the player's inventory");
            Expect.Equal(0, await who.Run<int>(s => Boxes.Rows(s, outer)), "the outer box's rows after the refusal");
            Expect.Equal("ok", await pair.Host.Run(() => Boxes.GraphCheck()), "the graph after the refusal");
        }
    }
}
