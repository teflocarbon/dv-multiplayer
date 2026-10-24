using System;
using System.Linq;
using Multiplayer.Server.Career;
using Multiplayer.Server.Items.SaveLoad;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Multiplayer.Protocol.Tests;

/// <summary>
/// The per-player save conversions (docs/design/server-player-split.md): a career saved before
/// per-player state (PerPlayerSaveFormat.ConvertSave) and a player's install GUID moving to their
/// Steam ID (InstallGuidRekey.RekeySave). Both must lose nothing, duplicate nothing, and change
/// nothing when run again. Items are no longer converted: a career saved before Multiplayer.Items
/// starts its items fresh (D1, docs/design/items/save-load.md).
/// </summary>
[TestFixture]
public sealed class SaveFormatConversionTests
{
    private static readonly Guid Owner = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Steam = new("33333333-3333-3333-3333-333333333333");

    [Test]
    public void ConvertSave_GivesTheOwnerThePosition()
    {
        var root = new JObject();

        Assert.That(PerPlayerSaveFormat.ConvertSave(root, Owner, new Vector3(1, 2, 3), 90f), Is.True);
        Assert.That(root["Players"]?[Owner.ToString()], Is.Not.Null);
        Assert.That(root[ItemsSaveCodec.Key], Is.Null, "items aren't converted");
    }

    [Test]
    public void ConvertSave_TwiceChangesNothingTheSecondTime()
    {
        var root = new JObject();
        PerPlayerSaveFormat.ConvertSave(root, Owner, new Vector3(1, 2, 3), 90f);
        JObject first = (JObject)root.DeepClone();

        Assert.That(PerPlayerSaveFormat.ConvertSave(root, Owner, new Vector3(9, 9, 9), 0f), Is.False);
        Assert.That(JToken.DeepEquals(root, first), Is.True, root.ToString());
    }

    [Test]
    public void ConvertSave_KeepsWhatTheSaveAlreadyHas()
    {
        var existingEntry = new JObject { ["Player_rotation"] = 45f };
        var root = new JObject
        {
            ["Players"] = new JObject { [Owner.ToString()] = existingEntry, [Other.ToString()] = new JObject() }
        };

        Assert.That(PerPlayerSaveFormat.ConvertSave(root, Owner, new Vector3(1, 2, 3), 90f), Is.False);
        Assert.That(JToken.DeepEquals(root["Players"]![Owner.ToString()], existingEntry), Is.True);
        Assert.That(root["Players"]![Other.ToString()], Is.Not.Null);
    }

    private static JObject Row(Guid owner, Guid holder) => new()
    {
        ["id"] = Guid.NewGuid().ToString("D"),
        ["owner"] = owner.ToString("D"),
        ["prefab"] = "Lantern",
        ["where"] = new JObject { ["kind"] = "Slot", ["holder"] = holder.ToString("D"), ["slot"] = 2 },
        ["born"] = new JObject { ["reason"] = "Purchased", ["utc"] = 1, ["by"] = holder.ToString("D") }
    };

    private static JObject SaveOf(Guid player)
    {
        Guid container = Guid.NewGuid();
        return new JObject
        {
            ["Players"] = new JObject { [player.ToString()] = new JObject { ["Player_rotation"] = 10f }, [Other.ToString()] = new JObject() },
            [ItemsSaveCodec.Key] = new JObject
            {
                ["format"] = ItemsSaveCodec.Format,
                // Ours (owner, holder, by), one we hold of another's (holder, by), and one of another's.
                ["ledger"] = new JArray(Row(player, player), Row(Other, player), Row(Other, Other)),
                ["containers"] = new JArray(new JObject
                {
                    ["id"] = container.ToString("D"), ["owner"] = player.ToString("D"), ["capacity"] = 4
                }),
                ["players"] = new JObject { [player.ToString("D")] = new JObject { ["starterKit"] = true } }
            }
        };
    }

    private static JArray Ledger(JObject root) => (JArray)root[ItemsSaveCodec.Key]!["ledger"];

    [Test]
    public void RekeySave_MovesEveryRecordOfTheOldId()
    {
        JObject root = SaveOf(Owner);

        int changed = InstallGuidRekey.RekeySave(root, Owner, Steam);

        // The player entry; owner, holder and by on our row; holder and by on the one we hold; the
        // container's owner; the starter kit.
        Assert.That(changed, Is.EqualTo(8));
        Assert.That(InstallGuidRekey.SaveOwns(root, Owner), Is.False);
        Assert.That(InstallGuidRekey.SaveOwns(root, Steam), Is.True);
        Assert.That(root["Players"]![Steam.ToString("D")], Is.Not.Null);
        Assert.That(root["Players"]![Other.ToString()], Is.Not.Null, "other players are untouched");
        Assert.That(Ledger(root).Count, Is.EqualTo(3), "nothing lost or added");
        Assert.That(Ledger(root).Count(r => Guid.Parse((string)r["owner"]) == Other), Is.EqualTo(2));
        Assert.That(ItemsSaveCodec.Read(root[ItemsSaveCodec.Key], out ItemsSave read), Is.EqualTo(ItemsSaveCodec.ReadResult.Read));
        Assert.That(read.UnreadableCount, Is.Zero);
        Assert.That(read.StarterKits, Is.EqualTo(new[] { Steam }));
        Assert.That((string)root[ItemsSaveCodec.Key]!["containers"]![0]!["owner"], Is.EqualTo(Steam.ToString("D")));
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
    public void RekeySave_EditsARowTheLoadCantRead()
    {
        JObject root = SaveOf(Owner);
        JObject broken = Row(Owner, Owner);
        ((JObject)broken["where"]!)["kind"] = "OnTheMoon";
        Ledger(root).Add(broken);

        InstallGuidRekey.RekeySave(root, Owner, Steam);

        Assert.That((string)broken["owner"], Is.EqualTo(Steam.ToString("D")));
        Assert.That((string)broken["where"]!["kind"], Is.EqualTo("OnTheMoon"), "the rest is left as it was");
    }

    [Test]
    public void SaveOwns_FindsAnIdInAnyPartOfTheSave()
    {
        Assert.That(InstallGuidRekey.SaveOwns(SaveOf(Owner), Owner), Is.True);
        Assert.That(InstallGuidRekey.SaveOwns(SaveOf(Owner), Steam), Is.False);
        Assert.That(InstallGuidRekey.SaveOwns(new JObject
        {
            [ItemsSaveCodec.Key] = new JObject { ["lostAndFound"] = new JArray(new JObject { ["by"] = Steam.ToString("D") }) }
        }, Steam), Is.True);
        Assert.That(InstallGuidRekey.SaveOwns(new JObject { ["WorldItemsV1"] = new JArray(new JObject { ["ownerIdentity"] = Steam.ToString("D") }) }, Steam),
            Is.False, "old keys aren't read (D1)");
        Assert.That(InstallGuidRekey.SaveOwns(null, Owner), Is.False);
        Assert.That(InstallGuidRekey.SaveOwns(SaveOf(Owner), Guid.Empty), Is.False);
    }
}
