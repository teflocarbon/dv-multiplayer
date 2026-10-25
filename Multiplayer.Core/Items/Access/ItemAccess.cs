using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Items.Access;

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
    public ItemAccessRequest(ItemAccessAction action, bool actorOwnsItem, bool isFixture, bool isEssential = false,
        bool isJobPaper = false)
    {
        Action = action;
        ActorOwnsItem = actorOwnsItem;
        IsFixture = isFixture;
        IsEssential = isEssential;
        IsJobPaper = isJobPaper;
    }

    public ItemAccessAction Action { get; }
    public bool ActorOwnsItem { get; }
    /// <summary>The item is a station fixture (access.md, "Fixtures"): part of a place, never a player's.</summary>
    public bool IsFixture { get; }
    /// <summary>One of Derail Valley's essentials (comms radio, maps, wallet, ...): it never goes in a container.</summary>
    public bool IsEssential { get; }
    /// <summary>A job paper (a booklet, an overview, a report): its job's, never owned (D5, OI-ID-12).</summary>
    public bool IsJobPaper { get; }
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
/// The one place that decides what a player may do with an item (access.md, "The one place that decides").
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
        // An essential stays out of containers, so its claim slot and recall always work (access.md,
        // "Essentials stay out of containers"; item-review L7; decided by the user 2026-10-22).
        if (request.IsEssential && request.Action == ItemAccessAction.Deposit)
            return ItemAccessDecision.Refuse(EssentialStaysOut);
        return request.Action switch
        {
            // Containers hold only their owner's own items (access.md, "The actions and their rules"), and
            // job papers: nobody owns one, and whoever carries it may put it in a box (the caller asks that
            // they carry it).
            ItemAccessAction.Deposit when !request.ActorOwnsItem && !request.IsJobPaper => ItemAccessDecision.Refuse("deposit-item-not-owned"),
            // A lost booklet comes back to the player it was issued to, whose Lost and Found entry it is
            // (OI-LF-17; the caller asks that the entry is theirs).
            ItemAccessAction.Recall when request.IsJobPaper => ItemAccessDecision.Allow,
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
/// (access.md, "Fixtures"). Empty until the shops work fills it.
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
