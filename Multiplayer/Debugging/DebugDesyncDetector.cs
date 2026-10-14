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
    private static float nextSample;

    public static void Remember(ItemUpdateData snapshot)
    {
        if (!DebugRuntime.Enabled || snapshot == null ||
            !ItemUpdateData.IncludesItemState(snapshot.UpdateType)) return;
        expectedItems[snapshot.ItemNetId] = new ExpectedItem
        {
            State = snapshot.ItemState,
            Position = snapshot.ItemPosition,
            Rotation = snapshot.ItemRotation,
            PlayerId = snapshot.PlayerId,
            AuthorityRevision = snapshot.AuthorityRevision
        };
    }

    public static void Forget(ushort id) => expectedItems.Remove(id);

    public static void Tick()
    {
        if (!DebugRuntime.Enabled || Time.unscaledTime < nextSample) return;
        nextSample = Time.unscaledTime + 0.5f;
        foreach (var pair in expectedItems)
        {
            if (!NetworkedItem.TryGet(pair.Key, out NetworkedItem item) || item == null)
            {
                Report(pair.Key, pair.Value, "missing-local-representation", true, new());
                continue;
            }
            ExpectedItem expected = pair.Value;
            bool mismatch = false;
            Dictionary<string, object> data = new()
            {
                ["expectedState"] = expected.State.ToString(),
                ["actualState"] = item.DebugCurrentState.ToString(),
                ["expectedHolder"] = expected.PlayerId,
                ["actualHolder"] = item.playerBelongsToId,
                ["expectedAuthorityRevision"] = expected.AuthorityRevision,
                ["actualAuthorityRevision"] = item.AuthorityRevision
            };
            string reason = string.Empty;
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
                Vector3 expectedWorld = expected.Position + WorldMover.currentMove;
                float distance = Vector3.Distance(expectedWorld, item.transform.position);
                data["expectedPosition"] = DebugValueSnapshotter.Snapshot(expectedWorld);
                data["actualPosition"] = DebugValueSnapshotter.Snapshot(item.transform.position);
                data["distance"] = distance;
                if (distance > 0.5f) { mismatch = true; reason = "position-mismatch"; }
            }
            Report(pair.Key, expected, reason, mismatch, data);
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
                DebugRuntime.Publish("desync", "desync.item-detected", DebugRuntimeSide.Shared, DebugSeverity.Warning, "Item", id.ToString(), data);
            }
        }
        else
        {
            expected.MismatchSamples = 0;
            if (expected.Reported)
            {
                expected.Reported = false;
                DebugRuntime.Publish("desync", "desync.item-recovered", DebugRuntimeSide.Shared, entityType: "Item", entityId: id.ToString(), data: data);
            }
        }
    }
}
