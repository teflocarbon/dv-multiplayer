using System;

namespace Multiplayer.Core.Items;

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
