using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.SaveLoad;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests.Items;

/// <summary>
/// The owned-items ledger in the save (<c>Multiplayer.OwnedItemsV1</c>, docs/legacy/items/owned-items.md):
/// every entry comes back as it was written, enums are names, and what can't be read is skipped,
/// never guessed.
/// </summary>
[TestFixture]
public sealed class LedgerSaveCodecTests
{
    private static readonly Guid Liam = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bentgear = new("22222222-2222-2222-2222-222222222222");

    [Test]
    public void EveryHomeAndBothEnds_RoundTrip()
    {
        long now = 600;
        int next = 0;
        ItemLedger ledger = new(() => new Guid($"00000000-0000-0000-0000-{++next:x12}"), () => now);
        Guid radio = Register(ledger, "CommsRadio", Home.InSlot(Liam, 3), essential: true);
        Guid lantern = Register(ledger, "Lantern", Home.TrainInterior("L-042"), shopStocked: true);
        Register(ledger, "Mug", Home.Container(new Guid("33333333-3333-3333-3333-333333333333"), 5));
        Register(ledger, "Switch", Home.Installed("L-042"));
        Register(ledger, "Beacon", Home.Installed(":global:"));
        Register(ledger, "Hanger", Home.Snapped("hanger:4"));
        Register(ledger, "EOTLantern", Home.Attached("L-043", false));
        Register(ledger, "Book", Home.Shelf("shelf:harbor:2"));
        Register(ledger, "Shovel", Home.LostAndFound);
        Register(ledger, "Lighter", Home.Missing);
        Register(ledger, "Oiler", Home.InSlot(Bentgear, 34));
        Register(ledger, "Lantern", Home.InHand(Bentgear, 1));
        now = 700;
        ledger.Replace(lantern, ItemRemovalReason.DestroyedByGame, new BirthRequest
        {
            Prefab = "LanternBroken",
            Home = Home.World,
            Detail = "smashed"
        });
        ledger.MarkStarterKit(Liam);

        JObject saved = LedgerSaveCodec.Write(ledger);
        List<LedgerEntry> read = LedgerSaveCodec.Read(JObject.Parse(saved.ToString()), out List<Guid> kits, out int unreadable);

        Assert.That(unreadable, Is.Zero);
        Assert.That(kits, Is.EqualTo(new[] { Liam }));
        Assert.That(read.Select(Describe), Is.EqualTo(ledger.All().Select(Describe)));
        LedgerEntry readRadio = read.Single(entry => entry.Id == radio);
        Assert.That(readRadio.ClaimSlot, Is.EqualTo(3));
        Assert.That((string)saved["items"][0]["born"]["reason"], Is.EqualTo("Purchased"), "enums are saved by name");
    }

    [Test]
    public void AnEssentialWithoutAClaim_StaysAnEssential()
    {
        ItemLedger ledger = new();
        ledger.Register(new BirthRequest
        {
            Owner = OwnerRef.Player(Liam), Prefab = "Map", Home = Home.LostAndFound,
            Reason = BirthReason.Issued, Essential = true
        });
        ledger.Repair(ledger.All().Single().Id, Home.World);

        LedgerEntry read = LedgerSaveCodec.Read(LedgerSaveCodec.Write(ledger), out _, out _).Single();

        Assert.That(read.Essential, Is.True);
    }

    [Test]
    public void ANewerFormat_OrTheOldOne_ReadsNothing()
    {
        // Format 1 kept places, not homes: the load migrates from the stores (R3).
        foreach (int format in new[] { 1, LedgerSaveCodec.Format + 1 })
        {
            JObject saved = new() { ["format"] = format, ["items"] = new JArray(new JObject()) };
            Assert.That(LedgerSaveCodec.Read(saved, out _, out _), Is.Empty);
        }
    }

    [Test]
    public void EntriesThatCantBeRead_AreSkippedAndCounted()
    {
        JObject good = (JObject)LedgerSaveCodec.Write(LedgerWithOne())["items"][0];
        JObject noOwner = (JObject)good.DeepClone();
        noOwner.Remove("owner");
        JObject unknownPlace = (JObject)good.DeepClone();
        unknownPlace["where"]["kind"] = "OnTheMoon";
        JObject saved = new() { ["format"] = LedgerSaveCodec.Format, ["items"] = new JArray(good, noOwner, unknownPlace, "junk") };

        List<LedgerEntry> read = LedgerSaveCodec.Read(saved, out _, out int unreadable);

        Assert.That(read, Has.Count.EqualTo(1));
        Assert.That(unreadable, Is.EqualTo(3));
    }

    [Test]
    public void NoSavedLedger_ReadsNothing()
    {
        Assert.That(LedgerSaveCodec.Read(null, out List<Guid> kits, out int unreadable), Is.Empty);
        Assert.That(kits, Is.Empty);
        Assert.That(unreadable, Is.Zero);
    }

    private static ItemLedger LedgerWithOne()
    {
        ItemLedger ledger = new();
        Register(ledger, "Lantern", Home.World);
        return ledger;
    }

    private static Guid Register(ItemLedger ledger, string prefab, Home home, bool essential = false,
        bool shopStocked = false) => ledger.Register(new BirthRequest
    {
        Owner = OwnerRef.Player(home.IsWithPlayer ? home.Holder : Liam),
        Prefab = prefab,
        Home = home,
        Reason = BirthReason.Purchased,
        By = Liam,
        Detail = "at Harbor",
        Essential = essential,
        PreferredClaimSlot = essential ? home.Slot : -1,
        ShopStocked = shopStocked
    }).Entry.Id;

    private static string Describe(LedgerEntry entry) =>
        $"{entry.Id}|{entry.Owner}|{entry.Prefab}|{entry.Home}|{entry.ClaimSlot}|{entry.Essential}|{entry.ShopStocked}|" +
        $"{entry.Born.Reason}|{entry.Born.UtcTicks}|{entry.Born.By}|{entry.Born.From}|{entry.Born.Detail}|" +
        (entry.Ended == null ? "live" : $"{entry.Ended.Reason}|{entry.Ended.UtcTicks}|{entry.Ended.ReplacedBy}|{entry.Ended.Detail}");
}
