using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Items.SaveLoad;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests.Items;

/// <summary>
/// The items key in the save (<c>Multiplayer.Items</c>, docs/design/items/save-load.md): every entry
/// comes back as it was written, enums are names, and a row that can't be read is kept as it was and
/// written back, never guessed or dropped (D9).
/// </summary>
[TestFixture]
public sealed class ItemsSaveCodecTests
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
        Register(ledger, "Oiler", Home.InSlot(Bentgear, 34));
        Register(ledger, "Lantern", Home.InHand(Bentgear, 1));
        now = 700;
        ledger.Replace(lantern, ItemRemovalReason.DestroyedByGame, new BirthRequest
        {
            Prefab = "LanternBroken",
            Home = Home.World,
            Detail = "smashed"
        });

        ItemsSave items = new();
        items.Ledger.AddRange(ledger.All());
        items.StarterKits.Add(Liam);
        JObject saved = ItemsSaveCodec.Write(items);

        Assert.That(ItemsSaveCodec.Read(JObject.Parse(saved.ToString()), out ItemsSave read), Is.EqualTo(ItemsSaveCodec.ReadResult.Read));
        Assert.That(read.UnreadableCount, Is.Zero);
        Assert.That(read.StarterKits, Is.EqualTo(new[] { Liam }));
        Assert.That(read.Ledger.Select(Describe), Is.EqualTo(ledger.All().Select(Describe)));
        Assert.That(read.Ledger.Single(entry => entry.Id == radio).ClaimSlot, Is.EqualTo(3));
        Assert.That((string)saved["ledger"][0]["born"]["reason"], Is.EqualTo("Purchased"), "enums are saved by name");
        Assert.That(read.LedgerRows.Keys, Is.EquivalentTo(read.Ledger.Select(entry => entry.Id)), "every readable row is kept as read");
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

        LedgerEntry read = ItemsSaveCodec.ReadEntry(ItemsSaveCodec.WriteEntry(ledger.All().Single()));

        Assert.That(read.Essential, Is.True);
    }

    [Test]
    public void ANewerFormat_IsLeftAlone()
    {
        JObject saved = new() { ["format"] = ItemsSaveCodec.Format + 1, ["ledger"] = new JArray(new JObject()) };

        Assert.That(ItemsSaveCodec.Read(saved, out ItemsSave read), Is.EqualTo(ItemsSaveCodec.ReadResult.Newer));
        Assert.That(read.Ledger, Is.Empty);
    }

    [Test]
    public void NoKey_IsAbsent()
    {
        Assert.That(ItemsSaveCodec.Read(null, out ItemsSave read), Is.EqualTo(ItemsSaveCodec.ReadResult.Absent));
        Assert.That(read.Ledger, Is.Empty);
        Assert.That(read.StarterKits, Is.Empty);
    }

    [Test]
    public void RowsThatCantBeRead_AreKeptAndWrittenBack()
    {
        JObject good = ItemsSaveCodec.WriteEntry(LedgerWithOne().All().Single());
        JObject noOwner = (JObject)good.DeepClone();
        noOwner.Remove("owner");
        JObject unknownPlace = (JObject)good.DeepClone();
        unknownPlace["id"] = Guid.NewGuid().ToString("D");
        unknownPlace["where"]["kind"] = "OnTheMoon";
        JObject saved = new()
        {
            ["format"] = ItemsSaveCodec.Format,
            ["ledger"] = new JArray(good, noOwner, unknownPlace, "junk", good.DeepClone()),
            ["states"] = new JObject { ["not-an-id"] = new JObject { ["prefab"] = "Mug" } },
            ["lostAndFound"] = new JArray(new JObject { ["reason"] = "Despawned" })
        };

        ItemsSaveCodec.Read(saved, out ItemsSave read);

        Assert.That(read.Ledger, Has.Count.EqualTo(1));
        Assert.That(read.UnreadableLedger, Has.Count.EqualTo(4), "no owner, an unknown home, junk, and a second row with the same id");
        Assert.That(read.UnreadableCount, Is.EqualTo(6));
        JObject written = ItemsSaveCodec.Write(read);
        Assert.That(((JArray)written["ledger"]).Count, Is.EqualTo(5));
        Assert.That(((JArray)written["ledger"]).Any(row => JToken.DeepEquals(row, unknownPlace)), Is.True, "kept verbatim");
        Assert.That(written["states"]["not-an-id"], Is.Not.Null);
        Assert.That(((JArray)written["lostAndFound"]).Count, Is.EqualTo(1));
    }

    [Test]
    public void AKeptRow_IsWrittenAsItWasRead()
    {
        // An item whose prefab is gone (D9): the load keeps its row, and the save writes that row, not the entry.
        JObject row = ItemsSaveCodec.WriteEntry(LedgerWithOne().All().Single());
        row["prefab"] = "SomeModsLantern";
        row["extra"] = "a field this build doesn't know";
        ItemsSaveCodec.Read(new JObject { ["format"] = ItemsSaveCodec.Format, ["ledger"] = new JArray(row) }, out ItemsSave read);

        JObject written = ItemsSaveCodec.Write(read);

        Assert.That(JToken.DeepEquals(written["ledger"][0], row), Is.True, written.ToString());
    }

    [Test]
    public void AnUnloadableHome_IsNeverRead()
    {
        // Unloadable is where the load puts what it couldn't make; it is never a saved home.
        JObject row = ItemsSaveCodec.WriteEntry(LedgerWithOne().All().Single());
        row["where"] = new JObject { ["kind"] = "Unloadable" };

        Assert.That(ItemsSaveCodec.ReadEntry(row), Is.Null);
    }

    [Test]
    public void Containers_RoundTrip_AndOneWithoutAnOwnerIsntRead()
    {
        Guid id = Guid.NewGuid(), parent = Guid.NewGuid();
        ContainerRecord container = new()
        {
            PersistentContainerId = id, OwnerIdentity = Liam.ToString("D"), PrefabName = "ItemContainerToolbox",
            Capacity = 8, ParentItemId = parent
        };

        ContainerRecord read = ItemsSaveCodec.ReadContainer(ItemsSaveCodec.WriteContainer(container));

        Assert.That(read.PersistentContainerId, Is.EqualTo(id));
        Assert.That(read.OwnerIdentity, Is.EqualTo(Liam.ToString("D")));
        Assert.That(read.PrefabName, Is.EqualTo("ItemContainerToolbox"));
        Assert.That(read.Capacity, Is.EqualTo(8));
        Assert.That(read.ParentItemId, Is.EqualTo(parent));
        container.OwnerIdentity = string.Empty;
        Assert.That(ItemsSaveCodec.ReadContainer(ItemsSaveCodec.WriteContainer(container)), Is.Null);
    }

    [Test]
    public void ALostAndFoundRow_HasItsReasonByName()
    {
        Guid id = Guid.NewGuid();
        JObject row = ItemsSaveCodec.WriteLost(new LostItemRecord
        {
            PersistentItemId = id, Reason = LostItemReason.SaveRecovery, LostUtcTicks = 42, DisplayName = "Lantern"
        });
        ItemsSave items = new();
        items.LostAndFound[id] = row;

        ItemsSaveCodec.Read(ItemsSaveCodec.Write(items), out ItemsSave read);

        Assert.That((string)read.LostAndFound[id]["reason"], Is.EqualTo("SaveRecovery"));
        Assert.That((long)read.LostAndFound[id]["utc"], Is.EqualTo(42));
        Assert.That((string)read.LostAndFound[id]["display"], Is.EqualTo("Lantern"));
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
