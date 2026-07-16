using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data.Items;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Debugging;

public static class DebugDesyncDetector
{
    private sealed class ExpectedItem
    {
        public ItemState State;
        public Vector3 Position;
        public Quaternion Rotation;
        public byte PlayerId;
        public uint AuthorityRevision;
        public int MismatchSamples;
        public bool Reported;
    }
    private static readonly Dictionary<ushort, ExpectedItem> expectedItems = new();
    private static readonly List<ushort> sampleOrder = new();
    private static int sampleCursor;

    public static void Remember(ItemUpdateData snapshot)
    {
        if (!DebugRuntime.Enabled || snapshot == null ||
            !ItemUpdateData.IncludesItemState(snapshot.UpdateType)) return;
        if (!expectedItems.ContainsKey(snapshot.ItemNetId)) sampleOrder.Add(snapshot.ItemNetId);
        expectedItems[snapshot.ItemNetId] = new ExpectedItem
        {
            State = snapshot.ItemState,
            Position = snapshot.ItemPosition,
            Rotation = snapshot.ItemRotation,
            PlayerId = snapshot.PlayerId,
            AuthorityRevision = snapshot.AuthorityRevision
        };
    }

    public static void Forget(ushort id)
    {
        expectedItems.Remove(id);
        int index = sampleOrder.IndexOf(id);
        if (index < 0) return;
        sampleOrder.RemoveAt(index);
        if (index < sampleCursor) sampleCursor--;
        if (sampleCursor >= sampleOrder.Count) sampleCursor = 0;
    }

    public static void Tick()
    {
        if (!DebugRuntime.Enabled || sampleOrder.Count == 0) return;
        int budget = Mathf.Clamp((sampleOrder.Count + 29) / 30, 1, 64);
        for (int sampled = 0; sampled < budget && sampleOrder.Count > 0; sampled++)
        {
            if (sampleCursor >= sampleOrder.Count) sampleCursor = 0;
            ushort id = sampleOrder[sampleCursor++];
            if (!expectedItems.TryGetValue(id, out ExpectedItem expected)) continue;
            if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null)
            {
                Report(id, expected, "missing-local-representation", true, new());
                continue;
            }
            bool mismatch = false;
            string reason = string.Empty;
            float? positionDistance = null;
            Vector3 expectedWorld = default;
            ItemState actualState = item.DebugCurrentState;
            bool thrownSettledAsDropped = expected.State == ItemState.Thrown && actualState == ItemState.Dropped;
            if (expected.State != actualState && !thrownSettledAsDropped)
            {
                mismatch = true; reason = "state-mismatch";
            }
            else if (expected.State is ItemState.InHand or ItemState.InInventory && item.playerBelongsToId != expected.PlayerId)
            {
                mismatch = true; reason = "holder-mismatch";
            }
            else if (expected.State == ItemState.Dropped)
            {
                expectedWorld = expected.Position + WorldMover.currentMove;
                float distance = Vector3.Distance(expectedWorld, item.transform.position);
                positionDistance = distance;
                if (distance > 0.5f) { mismatch = true; reason = "position-mismatch"; }
            }
            Dictionary<string, object> data = null;
            if (mismatch || expected.Reported)
            {
                data = new()
                {
                    ["expectedState"] = expected.State.ToString(), ["actualState"] = actualState.ToString(),
                    ["expectedHolder"] = expected.PlayerId, ["actualHolder"] = item.playerBelongsToId,
                    ["expectedAuthorityRevision"] = expected.AuthorityRevision, ["actualAuthorityRevision"] = item.AuthorityRevision
                };
                if (positionDistance.HasValue)
                {
                    data["expectedPosition"] = DebugValueSnapshotter.Snapshot(expectedWorld);
                    data["actualPosition"] = DebugValueSnapshotter.Snapshot(item.transform.position);
                    data["distance"] = positionDistance.Value;
                }
            }
            Report(id, expected, reason, mismatch, data);
        }
    }

    private static void Report(ushort id, ExpectedItem expected, string reason, bool mismatch, Dictionary<string, object> data)
    {
        if (mismatch)
        {
            expected.MismatchSamples++;
            if (expected.MismatchSamples >= 2 && !expected.Reported)
            {
                expected.Reported = true;
                data["reason"] = reason;
                DebugRuntime.Publish("desync", "desync.item-detected", DebugRuntimeSide.Shared, DebugSeverity.Warning, "Item", id.ToString(), data ?? new());
            }
        }
        else
        {
            expected.MismatchSamples = 0;
            if (expected.Reported)
            {
                expected.Reported = false;
                DebugRuntime.Publish("desync", "desync.item-recovered", DebugRuntimeSide.Shared, entityType: "Item", entityId: id.ToString(), data: data ?? new());
            }
        }
    }
}
