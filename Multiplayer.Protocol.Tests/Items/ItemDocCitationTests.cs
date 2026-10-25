#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests.Items;

/// <summary>
///     Code comments cite item docs that exist (OI-LED-14): every citation of an item doc in the C# of
///     the mod, its tests, DevTools and Tools names a doc of the new set, not a legacy one
///     (docs/legacy/items/ describes the code before the refactor), and the section it names
///     (<c>inventory.md, "Stand-ins"</c>) or its anchor (<c>wire.md#the-update</c>) is a heading there.
///     No comment describes the shadow ledger: the ledger has decided since R3.
/// </summary>
[TestFixture]
public sealed class ItemDocCitationTests
{
    private static readonly string[] CodeRoots =
        { "Multiplayer", "Multiplayer.Core", "Multiplayer.Core.Tests", "Multiplayer.Protocol.Tests", "DevTools", "Tools" };

    // A doc path or name, then optionally an anchor or a quoted section (which may wrap onto the next comment line).
    private static readonly Regex Citation = new(
        @"(?<path>(?:[\w.-]+/)*[\w-]+\.md)(?:#(?<anchor>[\w-]+)|,\s+""(?<section>[^""\r\n]+(?:\r?\n\s*///?\s*[^""\r\n]+)?)"")?",
        RegexOptions.Compiled);

    private static readonly Regex Heading = new(@"^#{1,6}\s+(.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex CommentContinuation = new(@"\s*\r?\n\s*///?\s*", RegexOptions.Compiled);
    private static readonly Regex ShadowLedger = new(@"shadow\s+(?://+\s*)?ledger|ledger\b[^\r\n]*\bin\s+shadow\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Test]
    public void CodeCitesItemDocsThatExist()
    {
        string root = RepositoryRoot();
        string docs = Path.Combine(root, "docs");
        Dictionary<string, List<string>> docsByName = Directory.EnumerateFiles(docs, "*.md", SearchOption.AllDirectories)
            .Select(file => Relative(root, file))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var headings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        string[] HeadingsOf(string doc) => headings.TryGetValue(doc, out string[] found) ? found :
            headings[doc] = Heading.Matches(File.ReadAllText(Path.Combine(root, doc))).Cast<Match>()
                .Select(m => Plain(m.Groups[1].Value)).ToArray();

        var problems = new List<string>();
        foreach (string file in CodeFiles(root))
        {
            string text = File.ReadAllText(file);
            // The ledger ran in shadow until R3 (The ledger decides): it is the only record of owner and home now.
            foreach (Match match in ShadowLedger.Matches(text))
                problems.Add($"{Relative(root, file)}:{LineOf(text, match.Index)}: describes the shadow ledger; the ledger has decided since R3");
            foreach (Match match in Citation.Matches(text))
            {
                string path = match.Groups["path"].Value;
                List<string> candidates = Candidates(root, path, docsByName);
                if (!IsItemDoc(path, candidates))
                    continue;
                string where = $"{Relative(root, file)}:{LineOf(text, match.Index)}: {match.Value.Replace("\r", "").Replace("\n", " ")}";
                List<string> current = candidates.Where(doc => !IsLegacy(doc)).ToList();
                if (candidates.Count == 0)
                    problems.Add($"{where}: no such doc");
                else if (current.Count == 0)
                    problems.Add($"{where}: a legacy doc ({candidates[0]}); cite the new doc in docs/design/items/");
                else if (match.Groups["section"].Success)
                {
                    string section = Plain(CommentContinuation.Replace(match.Groups["section"].Value, " "));
                    if (!current.Any(doc => HeadingsOf(doc).Any(h => h.StartsWith(section, StringComparison.OrdinalIgnoreCase))))
                        problems.Add($"{where}: no heading \"{section}\" in {string.Join(" or ", current)}");
                }
                else if (match.Groups["anchor"].Success)
                {
                    string anchor = match.Groups["anchor"].Value;
                    if (!current.Any(doc => HeadingsOf(doc).Any(h => Slug(h) == anchor)))
                        problems.Add($"{where}: no anchor #{anchor} in {string.Join(" or ", current)}");
                }
            }
        }
        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [TestCase("Ammo in a magazine", "ammo-in-a-magazine")]
    [TestCase("One request, one result", "one-request-one-result")]
    [TestCase("`NetworkedItem` on each game", "networkeditem-on-each-game")]
    public void SlugsAreGitHubs(string heading, string slug) => Assert.That(Slug(Plain(heading)), Is.EqualTo(slug));

    // An item doc: one in docs/design/items/ or docs/legacy/items/, or the plan and checklist.
    private static bool IsItemDoc(string path, List<string> candidates)
    {
        string name = Path.GetFileName(path);
        if (path.Contains("/items/") || name is "item-refactor.md" or "item-overhaul-checklist.md")
            return true;
        return candidates.Count > 0 && candidates.All(doc => doc.Contains("/items/"));
    }

    private static bool IsLegacy(string doc) => doc.StartsWith("docs/legacy/", StringComparison.OrdinalIgnoreCase);

    // A path from the root is that file; a bare name or a relative path is any doc of that name.
    private static List<string> Candidates(string root, string path, Dictionary<string, List<string>> docsByName)
    {
        if (path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
            return File.Exists(Path.Combine(root, path)) ? new List<string> { path } : new List<string>();
        return docsByName.TryGetValue(Path.GetFileName(path), out List<string> docs)
            ? docs.Where(doc => doc.EndsWith("/" + path.TrimStart('.', '/'), StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<string>();
    }

    private static IEnumerable<string> CodeFiles(string root)
    {
        string self = Path.Combine("Multiplayer.Protocol.Tests", "Items", nameof(ItemDocCitationTests) + ".cs");
        foreach (string folder in CodeRoots)
        {
            string dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir))
                continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Relative(root, file);
                if (relative.Contains("/bin/") || relative.Contains("/obj/") || relative.EndsWith(self.Replace('\\', '/')))
                    continue;
                yield return file;
            }
        }
    }

    private static string Plain(string heading) => heading.Replace("`", "").Replace("*", "").Trim();

    private static string Slug(string heading) =>
        Regex.Replace(Regex.Replace(heading.ToLowerInvariant(), @"[^\w\s-]", ""), @"\s", "-");

    private static int LineOf(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;

    private static string Relative(string root, string file) => file.Substring(root.Length + 1).Replace('\\', '/');

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Multiplayer", "Multiplayer.csproj")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Couldn't find the repository root above " + TestContext.CurrentContext.TestDirectory);
    }
}
