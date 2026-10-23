using System;
using Multiplayer.Core.Items.Ledger;

namespace Multiplayer.Core.Items.OwnedList;

/// <summary>What the server knows about an owned item's place, beyond the ledger's: names to show.</summary>
public readonly struct OwnedItemPlaceFacts
{
    public OwnedItemPlaceFacts(Guid viewer, string holderName = null, string carId = null, string containerName = null)
    {
        Viewer = viewer;
        HolderName = holderName;
        CarId = carId;
        ContainerName = containerName;
    }

    /// <summary>Who the list is for: their own hands and inventory read "your".</summary>
    public Guid Viewer { get; }
    /// <summary>The holder's name, when another player has it.</summary>
    public string HolderName { get; }
    /// <summary>The car's id (L-042) when it's in, on or attached to a car.</summary>
    public string CarId { get; }
    /// <summary>The container's name when it's inside one.</summary>
    public string ContainerName { get; }
}

/// <summary>
/// Where an owned item is, as its owner's owned-items list shows it (owned-items.md, "The
/// owned-items list"): your inventory, your hand, with another player, on the ground, in a car,
/// on a shelf, in a container, in Lost and Found, installed on a car. The server works it out, so
/// every player's list reads the same.
/// </summary>
public static class OwnedItemPlaceText
{
    public static string Describe(OwnedItemWhere where, OwnedItemPlaceFacts facts)
    {
        string car = string.IsNullOrEmpty(facts.CarId) ? "a car" : facts.CarId;
        switch (where.Place)
        {
            case OwnedItemPlace.With:
                bool yours = where.Holder == facts.Viewer && facts.Viewer != Guid.Empty;
                string who = yours ? "your" : (string.IsNullOrEmpty(facts.HolderName) ? "another player" : facts.HolderName) + "'s";
                return where.Hold switch
                {
                    OwnedItemHold.Hand => (yours ? "in your hand" : $"in {who} hand"),
                    OwnedItemHold.Belt => (yours ? "on your belt" : $"on {who} belt"),
                    _ => (yours ? "in your inventory" : $"in {who} inventory")
                };
            case OwnedItemPlace.World:
                if (Anchored(where, "car:")) return "in " + car;
                if (Anchored(where, "static:")) return "on a shelf";
                return "on the ground";
            case OwnedItemPlace.Attached:
                return "attached to " + car;
            case OwnedItemPlace.Snapped:
                return "fitted in place";
            case OwnedItemPlace.Container:
                return "in " + (string.IsNullOrEmpty(facts.ContainerName) ? "a container" : facts.ContainerName);
            case OwnedItemPlace.LostAndFound:
                return "in Lost and Found";
            case OwnedItemPlace.Installed:
                return "installed on " + car;
            default:
                return "missing";
        }
    }

    private static bool Anchored(OwnedItemWhere where, string prefix) =>
        where.Anchor != null && where.Anchor.StartsWith(prefix, StringComparison.Ordinal);
}

/// <summary>What retrieving an owned item from its place means (owned-items.md, "Retrieval").</summary>
public enum OwnedItemRetrievalKind : byte
{
    /// <summary>With its owner, in Lost and Found, or missing: no button.</summary>
    NothingToDo,
    /// <summary>Lying somewhere: collected into Lost and Found now.</summary>
    Collect,
    /// <summary>In a container: the outermost container that isn't with its owner goes, whole.</summary>
    OutermostContainer,
    /// <summary>With another player: taken from them first.</summary>
    TakeFromHolder,
    /// <summary>Attached, snapped or installed: not yet (detaching first; gadget removal is PR 8).</summary>
    NotYet
}

public static class OwnedItemRetrievalRule
{
    public static OwnedItemRetrievalKind For(OwnedItemWhere where, Guid owner) => where.Place switch
    {
        OwnedItemPlace.With => where.Holder == owner ? OwnedItemRetrievalKind.NothingToDo : OwnedItemRetrievalKind.TakeFromHolder,
        OwnedItemPlace.LostAndFound or OwnedItemPlace.Missing => OwnedItemRetrievalKind.NothingToDo,
        OwnedItemPlace.Container => OwnedItemRetrievalKind.OutermostContainer,
        OwnedItemPlace.Attached or OwnedItemPlace.Snapped or OwnedItemPlace.Installed => OwnedItemRetrievalKind.NotYet,
        _ => OwnedItemRetrievalKind.Collect
    };
}
