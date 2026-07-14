using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Replication;

public readonly struct ReplicaVector3
{
    public ReplicaVector3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public float DistanceTo(ReplicaVector3 other)
    {
        float x = X - other.X, y = Y - other.Y, z = Z - other.Z;
        return (float)Math.Sqrt(x * x + y * y + z * z);
    }
    public float Magnitude => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
}

public sealed class ItemReplicaState
{
    public string ItemState { get; set; } = string.Empty;
    public uint? AuthorityRevision { get; set; }
    public byte? PersistentOwnerPlayerId { get; set; }
    public byte? PlacementPlayerId { get; set; }
    public byte? InventoryClaimPlayerId { get; set; }
    public int? InventoryClaimSlot { get; set; }
    public string InventoryClaimFlags { get; set; } = string.Empty;
    public ReplicaVector3? PositionAbsolute { get; set; }
    public ReplicaVector3? Velocity { get; set; }
    public bool? ActiveInHierarchy { get; set; }
    public string Parent { get; set; } = string.Empty;
    public string LayerName { get; set; } = string.Empty;
    public bool? RigidbodyIsKinematic { get; set; }
    public byte? ActualRemoteHolderPlayerId { get; set; }
    public int? RendererCount { get; set; }
    public int? RendererEnabledCount { get; set; }
    public int? PageBookCurrentPage { get; set; }
    public int? PageBookPageCount { get; set; }
    public bool? PageBookPagesGenerated { get; set; }
}

public sealed class ItemReplicaDifference
{
    public string Code { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string Expected { get; set; } = string.Empty;
    public string Actual { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public static class ItemReplicaStateComparer
{
    public static IReadOnlyList<ItemReplicaDifference> Compare(ItemReplicaState expected, ItemReplicaState actual,
        byte? observerPlayerId = null, float droppedPositionTolerance = 0.5f, float thrownPositionTolerance = 3f)
    {
        if (expected == null) throw new ArgumentNullException(nameof(expected));
        if (actual == null) throw new ArgumentNullException(nameof(actual));
        List<ItemReplicaDifference> differences = new();

        string expectedState = expected.ItemState ?? string.Empty;
        string actualState = actual.ItemState ?? string.Empty;
        bool thrownSettledAsDropped = expectedState == "Thrown" && actualState == "Dropped";
        if (!string.IsNullOrEmpty(expectedState) && expectedState != actualState && !thrownSettledAsDropped)
            Add(differences, "item-state-mismatch", "itemState", expectedState, actualState);

        CompareNullable(differences, "authority-revision-mismatch", "authorityRevision", expected.AuthorityRevision, actual.AuthorityRevision);
        CompareNullable(differences, "persistent-owner-mismatch", "persistentOwnerPlayerId", expected.PersistentOwnerPlayerId, actual.PersistentOwnerPlayerId);
        CompareNullable(differences, "inventory-claim-player-mismatch", "inventoryClaimPlayerId", expected.InventoryClaimPlayerId, actual.InventoryClaimPlayerId);
        CompareNullable(differences, "inventory-claim-slot-mismatch", "inventoryClaimSlot", expected.InventoryClaimSlot, actual.InventoryClaimSlot);
        if (!string.IsNullOrEmpty(expected.InventoryClaimFlags) && expected.InventoryClaimFlags != actual.InventoryClaimFlags)
            Add(differences, "inventory-claim-flags-mismatch", "inventoryClaimFlags", expected.InventoryClaimFlags, actual.InventoryClaimFlags);

        if (expectedState is "InHand" or "InInventory")
        {
            CompareNullable(differences, "placement-player-mismatch", "placementPlayerId", expected.PlacementPlayerId, actual.PlacementPlayerId);
            // The local holder is represented by the base-game local inventory/hand, not a
            // NetworkedPlayer remote-hand object. Only require the remote visual on processes
            // observing somebody else's hand.
            bool holderIsLocal = observerPlayerId.HasValue && expected.PlacementPlayerId.HasValue &&
                observerPlayerId.Value == expected.PlacementPlayerId.Value;
            if (!holderIsLocal && expectedState == "InHand" && expected.PlacementPlayerId.HasValue && actual.ActualRemoteHolderPlayerId.HasValue &&
                expected.PlacementPlayerId.Value != actual.ActualRemoteHolderPlayerId.Value)
                Add(differences, "remote-hand-holder-mismatch", "actualRemoteHolderPlayerId",
                    expected.PlacementPlayerId.Value, actual.ActualRemoteHolderPlayerId.Value);
        }

        if (expectedState is "Dropped" or "Thrown" && expected.PositionAbsolute.HasValue && actual.PositionAbsolute.HasValue)
        {
            float distance = expected.PositionAbsolute.Value.DistanceTo(actual.PositionAbsolute.Value);
            float tolerance = expectedState == "Thrown" ? thrownPositionTolerance : droppedPositionTolerance;
            if (distance > tolerance)
                Add(differences, "absolute-position-mismatch", "positionAbsolute", Format(expected.PositionAbsolute.Value),
                    Format(actual.PositionAbsolute.Value), $"distance={distance:0.###}m tolerance={tolerance:0.###}m");
        }

        if (expectedState is "Dropped" or "Thrown")
        {
            if (actual.ActiveInHierarchy == false)
                Add(differences, "world-item-inactive", "activeInHierarchy", true, false);
            if (!string.IsNullOrEmpty(actual.LayerName) && actual.LayerName != "World_Item")
                Add(differences, "world-layer-mismatch", "layerName", "World_Item", actual.LayerName);
            if (actual.RigidbodyIsKinematic == true)
                Add(differences, "world-item-kinematic", "rigidbodyIsKinematic", false, true);
            if (!string.IsNullOrEmpty(actual.Parent) && actual.Parent.IndexOf("origin_shift_parent", StringComparison.OrdinalIgnoreCase) < 0)
                Add(differences, "world-parent-mismatch", "parent", "origin_shift_parent", actual.Parent);
        }

        // Complex documents construct child renderers asynchronously. Compare their enabled
        // mask only once both processes report the same hierarchy size.
        if (expected.RendererCount.HasValue && actual.RendererCount.HasValue &&
            expected.RendererCount.Value == actual.RendererCount.Value &&
            expected.RendererEnabledCount.HasValue && actual.RendererEnabledCount.HasValue &&
            expected.RendererEnabledCount.Value != actual.RendererEnabledCount.Value)
            Add(differences, "renderer-state-mismatch", "rendererEnabledCount",
                expected.RendererEnabledCount.Value, actual.RendererEnabledCount.Value,
                $"rendererCount={expected.RendererCount.Value}");

        // PageBook render hierarchies are asynchronous. Compare semantic page state only
        // after both peers report that their local page stacks have been generated.
        if (expected.PageBookPagesGenerated == true && actual.PageBookPagesGenerated == true)
        {
            CompareNullable(differences, "pagebook-current-page-mismatch", "pageBookCurrentPage",
                expected.PageBookCurrentPage, actual.PageBookCurrentPage);
            CompareNullable(differences, "pagebook-page-count-mismatch", "pageBookPageCount",
                expected.PageBookPageCount, actual.PageBookPageCount);
        }

        return differences;
    }

    private static void CompareNullable<T>(List<ItemReplicaDifference> result, string code, string field, T? expected, T? actual)
        where T : struct
    {
        if (expected.HasValue && actual.HasValue && !EqualityComparer<T>.Default.Equals(expected.Value, actual.Value))
            Add(result, code, field, expected.Value, actual.Value);
    }

    private static void Add(List<ItemReplicaDifference> result, string code, string field, object expected, object actual, string detail = "") =>
        result.Add(new ItemReplicaDifference
        {
            Code = code,
            Field = field,
            Expected = Convert.ToString(expected) ?? string.Empty,
            Actual = Convert.ToString(actual) ?? string.Empty,
            Detail = detail ?? string.Empty
        });

    private static string Format(ReplicaVector3 value) => $"({value.X:0.###},{value.Y:0.###},{value.Z:0.###})";
}
