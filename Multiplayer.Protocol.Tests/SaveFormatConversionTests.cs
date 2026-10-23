using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Server.Career;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Multiplayer.Protocol.Tests;

/// <summary>
/// The per-player save conversions (docs/design/server-player-split.md): a career saved before
/// per-player state (PerPlayerSaveFormat.ConvertSave) and a player's install GUID moving to their
/// Steam ID (InstallGuidRekey.RekeySave). Both must lose nothing, duplicate nothing, and change
/// nothing when run again.
/// </summary>
[TestFixture]
public sealed class SaveFormatConversionTests
{
    private static readonly Guid Owner = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Steam = new("33333333-3333-3333-3333-333333333333");

    private static StorageItemData Item(string prefab, int slot, Guid? persistentId = null)
    {
        var item = new StorageItemData(prefab, Vector3.zero, Quaternion.identity, belongsToPlayer: true, inventorySlotIndex: slot);
        if (persistentId.HasValue)
            item.state = new JObject { ["dvmpPersistentWorldItemId"] = persistentId.Value.ToString("D") };
        return item;
    }

    private static JArray Records(JObject root) => (JArray)root["WorldItemsV1"];

    [Test]
    public void ConvertSave_GivesTheOwnerThePositionAndInventory()
    {
        var root = new JObject();
        int moved = PerPlayerSaveFormat.ConvertSave(root, Owner, new Vector3(1, 2, 3), 90f,
            new[] { Item("Lantern", 0), Item("CommsRadio", 1), Item("JobBooklet", 2) });

        Assert.That(moved, Is.EqualTo(2), "job paperwork is left to DV's job save");
        Assert.That(root["Players"]?[Owner.ToString()], Is.Not.Null);
        JObject[] records = Records(root).OfType<JObject>().ToArray();
        Assert.That(records.Select(r => (string)r["prefabName"]), Is.EquivalentTo(new[] { "Lantern", "CommsRadio" }));
        Assert.That(records.All(r => Guid.Parse((string)r["ownerIdentity"]) == Owner), Is.True);
        Assert.That(records.All(r => Guid.Parse((string)r["placementPlayerIdentity"]) == Owner), Is.True);
        Assert.That(records.Select(r => (int)r["claimSlot"]), Is.EquivalentTo(new[] { 0, 1 }));
    }

    [Test]
    public void ConvertSave_TwiceChangesNothingTheSecondTime()
    {
        var root = new JObject();
        StorageItemData[] inventory = { Item("Lantern", 0), Item("Lantern", 1), Item("Wallet", 2, Guid.NewGuid()) };
        PerPlayerSaveFormat.ConvertSave(root, Owner, new Vector3(1, 2, 3), 90f, inventory);
        JObject first = (JObject)root.DeepClone();

        int moved = PerPlayerSaveFormat.ConvertSave(root, Owner, new Vector3(9, 9, 9), 0f, inventory);

        Assert.That(moved, Is.Zero);
        Assert.That(JToken.DeepEquals(root, first), Is.True, root.ToString());
        Assert.That(Records(root).Count, Is.EqualTo(3), "two identical lanterns are still two items");
    }

    [Test]
    public void ConvertSave_KeepsWhatTheSaveAlreadyHas()
    {
        Guid recorded = Guid.NewGuid();
        var existingEntry = new JObject { ["Player_rotation"] = 45f };
        var root = new JObject
        {
            ["Players"] = new JObject { [Owner.ToString()] = existingEntry, [Other.ToString()] = new JObject() },
            ["WorldItemsV1"] = new JArray(new JObject
            {
                ["persistentItemId"] = recorded.ToString("D"),
                ["prefabName"] = "Wallet",
                ["ownerIdentity"] = Other.ToString("D")
            })
        };

        int moved = PerPlayerSaveFormat.ConvertSave(root, Owner, new Vector3(1, 2, 3), 90f,
            new[] { Item("Wallet", 0, recorded), Item("Lantern", 1) });

        Assert.That(moved, Is.EqualTo(1), "the wallet is already a record");
        Assert.That(JToken.DeepEquals(root["Players"]![Owner.ToString()], existingEntry), Is.True);
        Assert.That(root["Players"]![Other.ToString()], Is.Not.Null);
        Assert.That(Records(root).Count, Is.EqualTo(2));
        Assert.That(Guid.Parse((string)Records(root)[0]["ownerIdentity"]), Is.EqualTo(Other));
    }

    private static JObject SaveOf(Guid player)
    {
        Guid container = Guid.NewGuid();
        var cold = new ContainerGraphSnapshot();
        cold.Containers.Add(new ContainerRecord { PersistentContainerId = container, OwnerIdentity = player.ToString("D"), Capacity = 4 });
        cold.Items.Add(new StoredItemRecord { PersistentItemId = Guid.NewGuid(), ParentContainerId = container, PersistentOwnerIdentity = player.ToString("D"), PrefabName = "Lantern", Slot = 0 });
        cold.Items.Add(new StoredItemRecord { PersistentItemId = Guid.NewGuid(), ParentContainerId = container, PersistentOwnerIdentity = Other.ToString("D"), PrefabName = "Wallet", Slot = 1 });
        return new JObject
        {
            ["Players"] = new JObject { [player.ToString()] = new JObject { ["Player_rotation"] = 10f }, [Other.ToString()] = new JObject() },
            ["WorldItemsV1"] = new JArray(
                new JObject { ["persistentItemId"] = Guid.NewGuid().ToString("D"), ["ownerIdentity"] = player.ToString("D"), ["placementPlayerIdentity"] = player.ToString("D") },
                new JObject { ["persistentItemId"] = Guid.NewGuid().ToString("D"), ["ownerIdentity"] = Other.ToString("D"), ["placementPlayerIdentity"] = player.ToString("D") },
                new JObject { ["persistentItemId"] = Guid.NewGuid().ToString("D"), ["ownerIdentity"] = Other.ToString("D"), ["placementPlayerIdentity"] = Other.ToString("D") }),
            ["LostAndFound"] = new JArray(new JObject { ["ownerIdentity"] = player.ToString("D") }),
            ["ColdContainers"] = Convert.ToBase64String(ContainerSnapshotCodec.Encode(cold))
        };
    }

    private static ContainerGraphSnapshot Cold(JObject root)
    {
        Assert.That(ContainerSnapshotCodec.TryDecode(Convert.FromBase64String((string)root["ColdContainers"]),
            out ContainerGraphSnapshot snapshot, out _), Is.True);
        return snapshot;
    }

    [Test]
    public void RekeySave_MovesEveryRecordOfTheOldId()
    {
        JObject root = SaveOf(Owner);

        int changed = InstallGuidRekey.RekeySave(root, Owner, Steam);

        // The player entry, two item records, one Lost and Found entry, a container and its item.
        Assert.That(changed, Is.EqualTo(6));
        Assert.That(InstallGuidRekey.SaveOwns(root, Owner), Is.False);
        Assert.That(InstallGuidRekey.SaveOwns(root, Steam), Is.True);
        Assert.That(root["Players"]![Steam.ToString("D")], Is.Not.Null);
        Assert.That(root["Players"]![Other.ToString()], Is.Not.Null, "other players are untouched");
        Assert.That(Records(root).Count, Is.EqualTo(3), "nothing lost or added");
        Assert.That(Records(root).Count(r => Guid.Parse((string)r["ownerIdentity"]) == Other), Is.EqualTo(2));
        ContainerGraphSnapshot cold = Cold(root);
        Assert.That(cold.Containers.Single().OwnerIdentity, Is.EqualTo(Steam.ToString("D")));
        Assert.That(cold.Items.Select(item => item.PersistentOwnerIdentity),
            Is.EquivalentTo(new[] { Steam.ToString("D"), Other.ToString("D") }));
    }

    [Test]
    public void RekeySave_TwiceChangesNothingTheSecondTime()
    {
        JObject root = SaveOf(Owner);
        InstallGuidRekey.RekeySave(root, Owner, Steam);
        JObject first = (JObject)root.DeepClone();

        Assert.That(InstallGuidRekey.RekeySave(root, Owner, Steam), Is.Zero);
        Assert.That(JToken.DeepEquals(root, first), Is.True);
    }

    [Test]
    public void RekeySave_KeepsAnEntryTheNewIdAlreadyHas()
    {
        JObject root = SaveOf(Owner);
        var steamEntry = new JObject { ["Player_rotation"] = 99f };
        ((JObject)root["Players"]!)[Steam.ToString("D")] = steamEntry;

        InstallGuidRekey.RekeySave(root, Owner, Steam);

        Assert.That(JToken.DeepEquals(root["Players"]![Steam.ToString("D")], steamEntry), Is.True);
        Assert.That(root["Players"]![Owner.ToString()], Is.Not.Null, "the old entry stays rather than being lost");
    }

    [Test]
    public void SaveOwns_FindsAnIdInAnyPartOfTheSave()
    {
        Assert.That(InstallGuidRekey.SaveOwns(SaveOf(Owner), Owner), Is.True);
        Assert.That(InstallGuidRekey.SaveOwns(SaveOf(Owner), Steam), Is.False);
        Assert.That(InstallGuidRekey.SaveOwns(new JObject
        {
            ["LostAndFound"] = new JArray(new JObject { ["ownerIdentity"] = Steam.ToString("D") })
        }, Steam), Is.True);
        Assert.That(InstallGuidRekey.SaveOwns(null, Owner), Is.False);
        Assert.That(InstallGuidRekey.SaveOwns(SaveOf(Owner), Guid.Empty), Is.False);
    }
}
