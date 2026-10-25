using System.Threading.Tasks;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Item bug SH1 (docs/research/item-bug-review.md): a client drops (or throws, RELEASE=throw) a mug
    /// of its own on a shelf, the floor of the nearest shed, as <c>owned-list-shelf</c> does with the
    /// host's own player. The client's spatial lease races vanilla parenting the mug to the shed (about
    /// frame 8): when the lease ends first, the server keeps the mug in the world where it left the
    /// hand, never on the shelf and never listed "on a shelf". When the lease outlives the parenting, the
    /// server has it on the shelf, but the client's object of it leaves its interest and never comes
    /// back (SH2, <c>item-bug-shelf-unseen</c>): the run checks the client sees it, so it fails either way.
    /// Fixed in R7: the simulator takes the shelf an item rests on before its settlement is captured,
    /// a shelf's key is the same in every game, and a shelf item is indexed by its cell.
    /// </summary>
    [Scenario("item-bug-client-shelf", "A client's mug dropped on a shelf (a shed) is on that shelf on the server, listed \"on a shelf\", and in the client's game (SH1)")]
    [Budget(180)]
    public sealed class ItemBugClientShelf : Scenario<ItemBugClientShelf.Options>
    {
        public sealed class Options
        {
            /// <summary>"drop" at the client's feet, or "throw" onto the floor.</summary>
            public string Release = "drop";
        }

        protected override Task Run(Pair pair, Options o) =>
            OwnedListShelf.Shelve(pair, new OwnedListShelf.Options { Who = "client", Release = o.Release, Seen = true });
    }
}
