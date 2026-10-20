using Multiplayer.Core.Trains;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Trains;

[TestFixture]
public sealed class AddedControlIdsTests
{
    [Test]
    public void For_JoinsPartAndPath()
    {
        AddedControlIds ids = new(new string[0]);
        Assert.That(ids.For("cab", new[] { "Doors", "DoorL" }), Is.EqualTo("mpControl_cab_Doors_DoorL"));
    }

    [Test]
    public void For_LeavesNoDotsOrSlashes()
    {
        AddedControlIds ids = new(new string[0]);
        string id = ids.For("cab", new[] { "Window L.001", "Puller/handle" });
        Assert.That(id, Is.EqualTo("mpControl_cab_Window_L_001_Puller_handle"));
        Assert.That(id, Does.Not.Contain(".").And.Not.Contain("/"));
    }

    [Test]
    public void For_NumbersRepeatsInOrder()
    {
        AddedControlIds ids = new(new string[0]);
        Assert.That(ids.For("ext", new[] { "Door" }), Is.EqualTo("mpControl_ext_Door"));
        Assert.That(ids.For("ext", new[] { "Door" }), Is.EqualTo("mpControl_ext_Door_2"));
        Assert.That(ids.For("ext", new[] { "Door" }), Is.EqualTo("mpControl_ext_Door_3"));
    }

    [Test]
    public void For_RepeatNeverTakesAnotherControlsName()
    {
        // A control actually named Door_2 comes first; the second Door must not take its name.
        AddedControlIds ids = new(new string[0]);
        Assert.That(ids.For("ext", new[] { "Door_2" }), Is.EqualTo("mpControl_ext_Door_2"));
        Assert.That(ids.For("ext", new[] { "Door" }), Is.EqualTo("mpControl_ext_Door"));
        Assert.That(ids.For("ext", new[] { "Door" }), Is.EqualTo("mpControl_ext_Door_2_"));
    }

    [Test]
    public void For_AvoidsTheCarsOwnComponents()
    {
        AddedControlIds ids = new(new[] { "mpControl_cab_Seat", "throttle" });
        Assert.That(ids.For("cab", new[] { "Seat" }), Is.EqualTo("mpControl_cab_Seat_"));
    }

    [Test]
    public void NextPrefab_NamesTheSameControlAlike()
    {
        // A cab and its exploded version: the same controls, the same names.
        AddedControlIds ids = new(new string[0]);
        string[] cab = { ids.For("cab", new[] { "Door" }), ids.For("cab", new[] { "Door" }) };
        ids.NextPrefab();
        string[] exploded = { ids.For("cab", new[] { "Door" }), ids.For("cab", new[] { "Door" }) };
        Assert.That(exploded, Is.EqualTo(cab));
    }

    [Test]
    public void Sanitize_EmptyName()
    {
        Assert.That(AddedControlIds.Sanitize(""), Is.EqualTo("_"));
        Assert.That(AddedControlIds.Sanitize(null), Is.EqualTo("_"));
    }
}
