using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Items;

/// <summary>What a player asks to do with an item. Byte-sized; append, never reorder.</summary>
public enum ItemAccessAction : byte
{
    /// <summary>Put it in their inventory (a VR belt slot is an inventory slot).</summary>
    Stash,
    /// <summary>Put it in a cold container.</summary>
    Deposit,
    /// <summary>Take it out of a cold container.</summary>
    Withdraw,
    /// <summary>Pick it up from the world, a train or a shelf.</summary>
    TakeFromWorld,
    /// <summary>Take it out of another player's hand or inventory.</summary>
    TakeFromPlayer,
    /// <summary>Take an installed gadget off a car.</summary>
    RemoveGadget,
    /// <summary>Send it to its owner's Lost and Found from the owned-items list.</summary>
    Retrieve,
    /// <summary>Call an essential back into its claim slot.</summary>
    Recall
}

/// <summary>
/// The facts <see cref="ItemAccess"/> decides on. The server works out who is who (identities,
/// containers, the fixture list); the policy only weighs them.
/// </summary>
public readonly struct ItemAccessRequest
{
    public ItemAccessRequest(ItemAccessAction action, bool actorOwnsItem, bool isFixture, bool isEssential = false)
    {
        Action = action;
        ActorOwnsItem = actorOwnsItem;
        IsFixture = isFixture;
        IsEssential = isEssential;
    }

    public ItemAccessAction Action { get; }
    public bool ActorOwnsItem { get; }
    /// <summary>The item is a station fixture (owned-items.md, "Fixtures"): part of a place, never a player's.</summary>
    public bool IsFixture { get; }
    /// <summary>One of Derail Valley's essentials (comms radio, maps, wallet, ...): it never goes in a container.</summary>
    public bool IsEssential { get; }
}

public readonly struct ItemAccessDecision
{
    private ItemAccessDecision(bool allowed, string reason)
    {
        Allowed = allowed;
        Reason = reason ?? string.Empty;
    }

    public bool Allowed { get; }
    /// <summary>Why it was refused, as a rejection reason; empty when allowed.</summary>
    public string Reason { get; }

    public static ItemAccessDecision Allow => new(true, null);
    public static ItemAccessDecision Refuse(string reason) => new(false, reason);
}

/// <summary>
/// The one place that decides what a player may do with an item (owned-items.md, "Permissions").
/// The server's item request checks ask it, and no client decides. The default policy is the rules
/// the server already had; the coming permissions system replaces the policy, not the call sites.
/// </summary>
public static class ItemAccess
{
    public const string FixtureStaysInPlace = "fixture-stays-in-its-place";
    public const string EssentialStaysOut = "essential-not-in-containers";

    public static ItemAccessDecision Decide(ItemAccessRequest request)
    {
        // A fixture is hand-only and never leaves its place (decided by the user 2026-10-21).
        if (request.IsFixture && request.Action is ItemAccessAction.Stash or ItemAccessAction.Deposit)
            return ItemAccessDecision.Refuse(FixtureStaysInPlace);
        // An essential stays out of containers, so its claim slot and recall always work (owned-items.md,
        // item-review L7; decided by the user 2026-10-22).
        if (request.IsEssential && request.Action == ItemAccessAction.Deposit)
            return ItemAccessDecision.Refuse(EssentialStaysOut);
        return request.Action switch
        {
            // Containers hold only their owner's own items (container-storage.md).
            ItemAccessAction.Deposit when !request.ActorOwnsItem => ItemAccessDecision.Refuse("deposit-item-not-owned"),
            ItemAccessAction.Retrieve or ItemAccessAction.Recall when !request.ActorOwnsItem =>
                ItemAccessDecision.Refuse("requester-not-persistent-owner"),
            // Vanilla has no way to take from another player; retrieval is the owner's way back.
            ItemAccessAction.TakeFromPlayer => ItemAccessDecision.Refuse("take-from-player"),
            _ => ItemAccessDecision.Allow
        };
    }
}

/// <summary>
/// The station fixtures: scene items that are part of a place, by prefab name or authored key
/// (owned-items.md, "Fixtures"). Empty until the shops work fills it.
/// </summary>
public sealed class ItemFixtureList
{
    private readonly HashSet<string> prefabs = new(StringComparer.Ordinal);
    private readonly HashSet<string> authoredKeys = new(StringComparer.Ordinal);

    public void AddPrefab(string prefab)
    {
        if (!string.IsNullOrEmpty(prefab))
            prefabs.Add(prefab);
    }

    public void AddAuthoredKey(string key)
    {
        if (!string.IsNullOrEmpty(key))
            authoredKeys.Add(key);
    }

    public bool RemovePrefab(string prefab) => prefab != null && prefabs.Remove(prefab);

    public bool Contains(string prefab, string authoredKey) =>
        !string.IsNullOrEmpty(prefab) && prefabs.Contains(prefab) ||
        !string.IsNullOrEmpty(authoredKey) && authoredKeys.Contains(authoredKey);

    public int Count => prefabs.Count + authoredKeys.Count;
}
