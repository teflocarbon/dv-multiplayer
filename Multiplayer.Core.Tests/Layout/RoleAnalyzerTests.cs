#nullable enable
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Layout;

// The analyzer that makes the client/server split a compile error (Tools/RoleAnalyzer).
[TestFixture]
public sealed class RoleAnalyzerTests
{
    private const string Server = @"
namespace Multiplayer.Server.Session
{
    public class NetworkServer { public static NetworkServer Instance; public void Send() { } public const string Name = ""server""; }
}";
    private const string Client = @"
namespace Multiplayer.Client.Session
{
    public class NetworkClient { public static NetworkClient Instance; public void Send() { } }
}";
    private const string Menus = @"
namespace Multiplayer.Menus.MainMenu
{
    public class MainMenu { public static void Open() { } }
}";

    [Test]
    public void ServerUsingClientFails()
    {
        var found = Analyze(@"
namespace Multiplayer.Server.Trains
{
    class TrainServer { void Run() { Multiplayer.Client.Session.NetworkClient.Instance.Send(); } }
}");
        Assert.That(found, Is.Not.Empty);
        Assert.That(found.All(d => d.Id == "DVMP0001"));
        Assert.That(found.First().GetMessage(), Does.Contain("Multiplayer.Client.Session.NetworkClient"));
    }

    [Test]
    public void ClientUsingServerFails() =>
        Assert.That(Analyze(@"
namespace Multiplayer.Client.Trains
{
    class TrainClient { Multiplayer.Server.Session.NetworkServer server; }
}"), Has.Some.Matches<Diagnostic>(d => d.Id == "DVMP0001"));

    [Test]
    public void SharedUsingEitherFails()
    {
        var found = Analyze(@"
namespace Multiplayer.Shared.World
{
    class Junction
    {
        void Switch()
        {
            Multiplayer.Server.Session.NetworkServer.Instance.Send();
            Multiplayer.Client.Session.NetworkClient.Instance.Send();
        }
    }
}");
        Assert.That(found.Select(d => d.GetMessage()), Has.Some.Contains("NetworkServer").And.Some.Contains("NetworkClient"));
    }

    [Test]
    public void OnlyRootAndMenusUseMenus()
    {
        Assert.That(Analyze(@"
namespace Multiplayer.Client.UI
{
    class Hud { void Back() { Multiplayer.Menus.MainMenu.MainMenu.Open(); } }
}"), Has.Some.Matches<Diagnostic>(d => d.Id == "DVMP0001"));
        Assert.That(Analyze(@"
namespace Multiplayer
{
    class Multiplayer { void Load() { Multiplayer.Menus.MainMenu.MainMenu.Open(); Multiplayer.Server.Session.NetworkServer.Instance.Send(); } }
}"), Is.Empty);
    }

    [Test]
    public void SharedAndOwnRoleAreFine() =>
        Assert.That(Analyze(@"
namespace Multiplayer.Shared.Items { public class Item { } }
namespace Multiplayer.Server.Items
{
    class ItemServer { Multiplayer.Shared.Items.Item item; Multiplayer.Server.Session.NetworkServer server; }
}"), Is.Empty);

    [Test]
    public void AllowListedReferencesPass() =>
        Assert.That(Analyze(@"
namespace Multiplayer.Shared.World
{
    class Junction { void Switch() { Multiplayer.Client.Session.NetworkClient.Instance.Send(); } }
}", allowList: "Multiplayer.Shared.World.Junction -> Multiplayer.Client.Session.NetworkClient"), Is.Empty);

    [Test]
    public void NamesTheCompiledCodeDoesntKeepPass() =>
        Assert.That(Analyze(@"
using Multiplayer.Server.Session;
namespace Multiplayer.Client.UI
{
    /// <summary>Not <see cref=""NetworkServer""/>.</summary>
    class Chat
    {
        string a = nameof(NetworkServer);
        string b = NetworkServer.Name;
    }
}"), Is.Empty);

    [Test]
    public void AFileMustSitInItsRoleFolderWithItsNamespace()
    {
        var found = Analyze(@"namespace Multiplayer.Shared.World { class Junction { } }", path: "/mod/Multiplayer/Server/World/Junction.cs", id: "DVMP0002");
        Assert.That(found, Has.Some.Matches<Diagnostic>(d => d.Id == "DVMP0002" && d.GetMessage().Contains("Multiplayer.Server.World")));
        Assert.That(Analyze(@"namespace Multiplayer.World { class Junction { } }", path: "/mod/Multiplayer/World/Junction.cs", id: "DVMP0002"),
            Has.Some.Matches<Diagnostic>(d => d.Id == "DVMP0002" && d.GetMessage().Contains("isn't a role")));
        Assert.That(Analyze(@"namespace Multiplayer.Server.World { class Junction { } }", path: "/mod/Multiplayer/Server/World/Junction.cs", id: "DVMP0002"), Is.Empty);
    }

    // Only the given rule's diagnostics: the snippets don't sit in their namespaces' folders.
    private static ImmutableArray<Diagnostic> Analyze(string source, string allowList = "", string path = "/mod/Multiplayer/Shared/Test.cs", string id = "DVMP0001")
    {
        SyntaxTree[] trees =
        {
            CSharpSyntaxTree.ParseText(source, path: path),
            CSharpSyntaxTree.ParseText(Server, path: "/mod/Multiplayer/Server/Session/NetworkServer.cs"),
            CSharpSyntaxTree.ParseText(Client, path: "/mod/Multiplayer/Client/Session/NetworkClient.cs"),
            CSharpSyntaxTree.ParseText(Menus, path: "/mod/Multiplayer/Menus/MainMenu/MainMenu.cs"),
        };
        var compilation = CSharpCompilation.Create("Multiplayer", trees,
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var options = new AnalyzerOptions(
            ImmutableArray.Create<AdditionalText>(new Text("/repo/" + RoleAnalyzer.RoleAnalyzer.AllowListFileName, allowList)),
            new ConfigProvider("/mod/Multiplayer"));
        return compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new RoleAnalyzer.RoleAnalyzer()), options)
            .GetAnalyzerDiagnosticsAsync().Result
            .Where(d => d.Location.SourceTree?.FilePath == path && d.Id == id)
            .ToImmutableArray();
    }

    private sealed class Text : AdditionalText
    {
        private readonly string content;
        public Text(string path, string content) { Path = path; this.content = content; }
        public override string Path { get; }
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(content);
    }

    private sealed class ConfigProvider : AnalyzerConfigOptionsProvider
    {
        private readonly Options global;
        public ConfigProvider(string projectDir) => global = new Options(projectDir);
        public override AnalyzerConfigOptions GlobalOptions => global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;

        private sealed class Options : AnalyzerConfigOptions
        {
            public static readonly Options Empty = new(null);
            private readonly string? projectDir;
            public Options(string? projectDir) => this.projectDir = projectDir;
            public override bool TryGetValue(string key, out string value)
            {
                value = projectDir ?? "";
                return projectDir != null && key == "build_property.MSBuildProjectDirectory";
            }
        }
    }
}
