using Multiplayer.Core.Items;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class EssentialClaimSlotPolicyTests
{
    [Test]
    public void StolenRadioSharingTheMapsSlot_MovesToAFreeSlotAndTheMapKeepsItsOwn()
    {
        // Live 2026-10-19: the radio, held by another player, and the map in its owner's inventory
        // both claimed slot 3, so the radio had no silhouette and could not be recalled.
        IReadOnlyList<ClaimSlotMove> moves = EssentialClaimSlotPolicy.Resolve(new[]
        {
            new EssentialClaim(763, 3, inOwnerInventory: false, dropped: true),
            new EssentialClaim(765, 3, inOwnerInventory: true, dropped: false)
        });

        Assert.That(moves, Has.Count.EqualTo(1));
        Assert.That(moves[0].NetId, Is.EqualTo(763));
        Assert.That(moves[0].FromSlot, Is.EqualTo(3));
        Assert.That(moves[0].ToSlot, Is.EqualTo(EssentialClaimSlotPolicy.InventoryCapacity - 1));
    }

    [Test]
    public void OwnerPlacingAnEssentialInADroppedEssentialsSlot_StateMachineFollowsAndPolicySeparatesThem()
    {
        // The radio was dropped: its slot holds only a silhouette, or looks empty if DV lost it.
        ItemAuthorityState radio = Essential(763, slot: 3,
            AuthorityClaimFlags.Reserved | AuthorityClaimFlags.Dropped);
        ItemAuthorityState map = Essential(765, slot: 7, AuthorityClaimFlags.Reserved);
        map.Placement = AuthorityPlacement.PlayerHand;
        map.PlacementPlayerId = 1;

        // The owner moves the map from their hand into slot 3. The state machine sees one item and
        // follows the owner's client, so the map now claims the radio's slot.
        ItemAuthorityResult placed = ItemAuthorityStateMachine.Apply(map, new ItemTransitionCommand
        {
            ActorPlayerId = 1,
            ExpectedRevision = map.Revision,
            AppliesPlacement = true,
            RequestedPlacement = AuthorityPlacement.PlayerInventory,
            InventoryClaimSlot = 3,
            InventoryClaimFlags = AuthorityClaimFlags.Reserved
        });
        Assert.That(placed.Accepted, Is.True);
        Assert.That(placed.State.InventoryClaimSlot, Is.EqualTo(radio.InventoryClaimSlot));

        // The map is really in slot 3, so the radio's claim is the one that moves.
        IReadOnlyList<ClaimSlotMove> moves = EssentialClaimSlotPolicy.Resolve(new[]
        {
            Claim(radio), Claim(placed.State)
        }, preferredNetId: placed.State.NetId);

        Assert.That(moves.Select(move => move.NetId), Is.EqualTo(new ushort[] { 763 }));
        Assert.That(moves[0].ToSlot, Is.Not.EqualTo(3));
    }

    [Test]
    public void PreferredItem_KeepsTheSlotOverAnItemTheRecordsStillPlaceThere()
    {
        // A recall from a silhouette in slot 3 is fresher evidence than a stale placement.
        IReadOnlyList<ClaimSlotMove> moves = EssentialClaimSlotPolicy.Resolve(new[]
        {
            new EssentialClaim(10, 3, inOwnerInventory: true, dropped: false),
            new EssentialClaim(20, 3, inOwnerInventory: true, dropped: false)
        }, preferredNetId: 20);

        Assert.That(moves.Single().NetId, Is.EqualTo(10));
    }

    [Test]
    public void WithoutAPreference_InInventoryThenNotDroppedThenLowestNetIdKeepsTheSlot()
    {
        Assert.That(Keeper(new EssentialClaim(10, 0, false, false), new EssentialClaim(20, 0, true, true)),
            Is.EqualTo(20), "an item in the slot beats one that isn't");
        Assert.That(Keeper(new EssentialClaim(10, 0, false, true), new EssentialClaim(20, 0, false, false)),
            Is.EqualTo(20), "a claim beats a silhouette");
        Assert.That(Keeper(new EssentialClaim(20, 0, false, true), new EssentialClaim(10, 0, false, true)),
            Is.EqualTo(10), "then the lowest NetId, so the host's choice is stable");
    }

    [Test]
    public void DistinctClaims_AreLeftAlone()
    {
        IReadOnlyList<ClaimSlotMove> moves = EssentialClaimSlotPolicy.Resolve(new[]
        {
            new EssentialClaim(763, 3, false, true),
            new EssentialClaim(765, 4, true, false),
            new EssentialClaim(766, -1, false, false)
        });

        Assert.That(moves, Is.Empty);
    }

    [Test]
    public void SeveralDisplacedClaims_EachGetTheirOwnFreeSlotFromTheBack()
    {
        IReadOnlyList<ClaimSlotMove> moves = EssentialClaimSlotPolicy.Resolve(new[]
        {
            new EssentialClaim(1, 0, true, false),
            new EssentialClaim(2, 0, false, true),
            new EssentialClaim(3, 0, false, true),
            new EssentialClaim(4, 35, false, true)
        });

        Assert.That(moves.Select(move => (move.NetId, move.ToSlot)),
            Is.EqualTo(new[] { ((ushort)2, 34), ((ushort)3, 33) }));
    }

    [Test]
    public void NoFreeSlot_LeavesTheCollisionRatherThanDroppingAClaim()
    {
        EssentialClaim[] claims = Enumerable.Range(0, 4)
            .Select(slot => new EssentialClaim((ushort)(slot + 1), slot, true, false))
            .Append(new EssentialClaim(99, 0, false, true))
            .ToArray();

        Assert.That(EssentialClaimSlotPolicy.Resolve(claims, capacity: 4), Is.Empty);
    }

    [Test]
    public void LostAndFoundRecord_TakesTheMovedClaimSlot()
    {
        LostItemRegistry registry = new();
        registry.Add(new LostItemRecord
        {
            PersistentItemId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            NetId = 763,
            Revision = 7,
            OwnerPlayerId = 1,
            PrefabName = "CommsRadio",
            InventoryClaimSlot = 3
        });

        Assert.That(registry.SetInventoryClaimSlot(763, 35), Is.True);
        Assert.That(registry.SetInventoryClaimSlot(764, 35), Is.False);
        registry.TryGet(763, out LostItemRecord stored);
        Assert.That(stored.InventoryClaimSlot, Is.EqualTo(35));
    }

    private static ushort Keeper(params EssentialClaim[] claims)
    {
        ushort moved = EssentialClaimSlotPolicy.Resolve(claims).Single().NetId;
        return claims.Single(claim => claim.NetId != moved).NetId;
    }

    private static ItemAuthorityState Essential(ushort netId, int slot, AuthorityClaimFlags flags) => new()
    {
        NetId = netId,
        Revision = 4,
        Placement = AuthorityPlacement.World,
        PersistentOwnerPlayerId = 1,
        InventoryClaimPlayerId = 1,
        InventoryClaimSlot = slot,
        InventoryClaimFlags = flags
    };

    private static EssentialClaim Claim(ItemAuthorityState state) => new(state.NetId,
        state.InventoryClaimSlot,
        state.Placement == AuthorityPlacement.PlayerInventory &&
            state.PlacementPlayerId == state.PersistentOwnerPlayerId,
        (state.InventoryClaimFlags & (AuthorityClaimFlags.Dropped | AuthorityClaimFlags.Stolen)) != 0);
}
