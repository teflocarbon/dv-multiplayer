using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items;
using Multiplayer.Server.Items;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

/// <summary>
/// The owned-items ledger in the save (<c>Multiplayer.OwnedItemsV1</c>, docs/design/owned-items.md):
/// every entry comes back as it was written, enums are names, and what can't be read is skipped,
/// never guessed.
/// </summary>
[TestFixture]
public sealed class OwnedItemsSaveCodecTests
{
    private static readonly Guid Liam = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bentgear = new("22222222-2222-2222-2222-222222222222");

    [Test]
    public void EveryPlaceAndBothEnds_RoundTrip()
    {
        long now = 600;
        int next = 0;
        OwnedItemLedger ledger = new(() => new Guid($"00000000-0000-0000-0000-{++next:x12}"), () => now);
        Guid radio = Register(ledger, "CommsRadio", OwnedItemWhere.With(Liam, OwnedItemHold.Inventory, 3), essential: true);
        Guid lantern = Register(ledger, "Lantern", OwnedItemWhere.World("car:L-042"), shopStocked: true);
        Register(ledger, "Mug", OwnedItemWhere.Container(new Guid("33333333-3333-3333-3333-333333333333")));
        Register(ledger, "Switch", OwnedItemWhere.Installed("car:L-042", 17));
        Register(ledger, "Hanger", OwnedItemWhere.Snapped("hanger:4"));
        Register(ledger, "EOTLantern", OwnedItemWhere.Attached("car:L-043"));
        Register(ledger, "Shovel", OwnedItemWhere.LostAndFound);
        Register(ledger, "Lighter", OwnedItemWhere.Missing);
        Register(ledger, "Oiler", OwnedItemWhere.With(Bentgear, OwnedItemHold.Belt));
        now = 700;
        ledger.Replace(lantern, ItemRemovalReason.DestroyedByGame, new OwnedItemBirthRequest
        {
            Prefab = "LanternBroken",
            Where = OwnedItemWhere.World(),
            Detail = "smashed"
        });
        ledger.MarkStarterKit(Liam);

        JObject saved = OwnedItemsSaveCodec.Write(ledger);
        List<OwnedItemEntry> read = OwnedItemsSaveCodec.Read(JObject.Parse(saved.ToString()), out List<Guid> kits, out int unreadable);

        Assert.That(unreadable, Is.Zero);
        Assert.That(kits, Is.EqualTo(new[] { Liam }));
        Assert.That(read.Select(Describe), Is.EqualTo(ledger.All().Select(Describe)));
        OwnedItemEntry readRadio = read.Single(entry => entry.Id == radio);
        Assert.That(readRadio.ClaimSlot, Is.EqualTo(3));
        Assert.That((string)saved["items"][0]["born"]["reason"], Is.EqualTo("Purchased"), "enums are saved by name");
    }

    [Test]
    public void AHeldItem_IsSavedInItsHoldersInventory()
    {
        // As WorldItemPersistenceSave saves it, so the load finds it where the ledger says.
        OwnedItemLedger ledger = new();
        Register(ledger, "Lantern", OwnedItemWhere.With(Bentgear, OwnedItemHold.Hand));

        OwnedItemEntry read = OwnedItemsSaveCodec.Read(OwnedItemsSaveCodec.Write(ledger), out _, out _).Single();

        Assert.That(read.Where, Is.EqualTo(OwnedItemWhere.With(Bentgear, OwnedItemHold.Inventory)));
    }

    [Test]
    public void ANewerFormat_ReadsNothing()
    {
        JObject saved = new() { ["format"] = OwnedItemsSaveCodec.Format + 1, ["items"] = new JArray(new JObject()) };

        Assert.That(OwnedItemsSaveCodec.Read(saved, out _, out _), Is.Empty);
    }

    [Test]
    public void EntriesThatCantBeRead_AreSkippedAndCounted()
    {
        JObject good = (JObject)OwnedItemsSaveCodec.Write(LedgerWithOne())["items"][0];
        JObject noOwner = (JObject)good.DeepClone();
        noOwner.Remove("owner");
        JObject unknownPlace = (JObject)good.DeepClone();
        unknownPlace["where"]["kind"] = "OnTheMoon";
        JObject saved = new() { ["format"] = 1, ["items"] = new JArray(good, noOwner, unknownPlace, "junk") };

        List<OwnedItemEntry> read = OwnedItemsSaveCodec.Read(saved, out _, out int unreadable);

        Assert.That(read, Has.Count.EqualTo(1));
        Assert.That(unreadable, Is.EqualTo(3));
    }

    [Test]
    public void NoSavedLedger_ReadsNothing()
    {
        Assert.That(OwnedItemsSaveCodec.Read(null, out List<Guid> kits, out int unreadable), Is.Empty);
        Assert.That(kits, Is.Empty);
        Assert.That(unreadable, Is.Zero);
    }

    private static OwnedItemLedger LedgerWithOne()
    {
        OwnedItemLedger ledger = new();
        Register(ledger, "Lantern", OwnedItemWhere.World());
        return ledger;
    }

    private static Guid Register(OwnedItemLedger ledger, string prefab, OwnedItemWhere where, bool essential = false,
        bool shopStocked = false) => ledger.Register(new OwnedItemBirthRequest
    {
        Owner = Liam,
        Prefab = prefab,
        Where = where,
        Reason = OwnedItemBirth.Purchased,
        By = Liam,
        Detail = "at Harbor",
        Essential = essential,
        PreferredClaimSlot = essential ? where.Slot : -1,
        ShopStocked = shopStocked
    }).Entry.Id;

    private static string Describe(OwnedItemEntry entry) =>
        $"{entry.Id}|{entry.Owner}|{entry.Prefab}|{entry.Where}|{entry.ClaimSlot}|{entry.ShopStocked}|" +
        $"{entry.Born.Reason}|{entry.Born.UtcTicks}|{entry.Born.By}|{entry.Born.From}|{entry.Born.Detail}|" +
        (entry.Ended == null ? "live" : $"{entry.Ended.Reason}|{entry.Ended.UtcTicks}|{entry.Ended.ReplacedBy}|{entry.Ended.Detail}");
}
