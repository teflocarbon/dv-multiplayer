using System.Threading.Tasks;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Item bug SH2 (docs/research/item-bug-review.md): an item on a shelf is never shown to a client.
    /// The host's own player drops a mug of its own on the floor of the nearest shed, as
    /// <c>owned-list-shelf</c> does, and the server has it there; the client is then brought beside it,
    /// but its game never gets the mug. A client's own drop meets it too (<c>item-bug-client-shelf</c>):
    /// its object leaves its interest just after the drop and isn't made again.
    /// </summary>
    [Scenario("item-bug-shelf-unseen", "An item on a shelf (a shed) is shown to a client beside it (SH2)")]
    [KnownBug("SH2")]
    [Needs(Need.PlayerHost)]
    [Budget(180)]
    public sealed class ItemBugShelfUnseen : Scenario<ItemBugShelfUnseen.Options>
    {
        public sealed class Options
        {
            /// <summary>"drop" at the host player's feet, or "throw" onto the floor.</summary>
            public string Release = "drop";
        }

        protected override Task Run(Pair pair, Options o) =>
            OwnedListShelf.Shelve(pair, new OwnedListShelf.Options { Who = "host", Release = o.Release, Seen = true });
    }
}
