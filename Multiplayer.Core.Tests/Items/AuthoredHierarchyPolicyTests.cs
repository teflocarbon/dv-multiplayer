using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

public sealed class AuthoredHierarchyPolicyTests
{
    [TestCase("game_content#0/Stations#3/Offices#1/Bin#4", "Offices", true)]
    [TestCase("game_content#0/Stations#3/offices#1/Bin#4", "Offices", true)]
    [TestCase("game_content/Stations/Offices/Bin", "Offices", true)]
    [TestCase("game_content#0/Stations#3/OfficesAnnex#1/Bin#4", "Offices", false)]
    [TestCase("game_content#0/Shops#3/Bin#4", "Offices", false)]
    public void ContainsNodeRecognizesStablePathSegments(string path, string node, bool expected)
    {
        Assert.That(AuthoredHierarchyPolicy.ContainsNode(path, node), Is.EqualTo(expected));
    }
}
