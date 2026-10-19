using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Multiplayer.RoleAnalyzer;

/// <summary>
/// Fails the Multiplayer build where the code crosses its client/server split (docs/design/code-layout.md),
/// at the line that does it:
/// <list type="bullet">
/// <item>DVMP0001: Server and Client code referencing each other, Shared code referencing either, or
/// anything but the mod's root and Menus referencing Menus. The references that exist today are in
/// Multiplayer.Protocol.Tests/CodeLayout/role-references.allowed.txt (an AdditionalFile), as
/// <c>from type -&gt; to type</c> of outermost types; that list may only shrink.</item>
/// <item>DVMP0002: a file outside Shared/, Server/, Client/ and Menus/, or whose namespace isn't its folder's.</item>
/// </list>
/// The same rules as CodeLayoutTests, which checks the built DLL: this sees the source, so it flags each
/// reference where it's written; the test also catches what only the compiled code shows, and stale
/// allow-list lines.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RoleAnalyzer : DiagnosticAnalyzer
{
    public const string RoleReferenceId = "DVMP0001";
    public const string FolderNamespaceId = "DVMP0002";
    public const string AllowListFileName = "role-references.allowed.txt";

    private static readonly DiagnosticDescriptor RoleReference = new(
        RoleReferenceId,
        "Reference across the client/server split",
        "{0} code may not use {1} ({2} code): {3} See docs/design/code-layout.md.",
        "Layout", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor FolderNamespace = new(
        FolderNamespaceId,
        "File outside its role folder or namespace",
        "{0} See docs/design/code-layout.md.",
        "Layout", DiagnosticSeverity.Error, isEnabledByDefault: true);

    // The root namespace's files: UMM's entry point (Multiplayer.Multiplayer.Load), the settings and the locale.
    private static readonly string[] RootFiles = { "Multiplayer.cs", "Settings.cs", "Locale.cs" };
    private static readonly string[] RoleFolders = { "Shared", "Server", "Client", "Menus" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(RoleReference, FolderNamespace);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            HashSet<string> allowed = LoadAllowList(start.Options.AdditionalFiles, start.CancellationToken);
            IAssemblySymbol own = start.Compilation.Assembly;
            start.RegisterSyntaxNodeAction(c => CheckReference(c, own, allowed), SyntaxKind.IdentifierName, SyntaxKind.GenericName);

            if (start.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.MSBuildProjectDirectory", out string? projectDir) &&
                !string.IsNullOrEmpty(projectDir))
                start.RegisterSyntaxTreeAction(c => CheckFolder(c, projectDir!));
        });
    }

    private enum Role { Root, Shared, Server, Client, Menus }

    private static Role RoleOf(string ns)
    {
        if (ns.StartsWith("Multiplayer.Shared", StringComparison.Ordinal)) return Role.Shared;
        if (ns.StartsWith("Multiplayer.Server", StringComparison.Ordinal)) return Role.Server;
        if (ns.StartsWith("Multiplayer.Client", StringComparison.Ordinal)) return Role.Client;
        if (ns.StartsWith("Multiplayer.Menus", StringComparison.Ordinal)) return Role.Menus;
        return Role.Root;
    }

    private static bool MayReference(Role from, Role to) => (from, to) switch
    {
        (Role.Root or Role.Menus, _) => true,
        (_, Role.Root) => true,
        (_, Role.Menus) => false,
        (Role.Shared, Role.Server or Role.Client) => false,
        (Role.Server, Role.Client) => false,
        (Role.Client, Role.Server) => false,
        _ => true,
    };

    private static string Why(Role from, Role to) => (from, to) switch
    {
        (_, Role.Menus) => "only the mod's root and Menus may use the menus.",
        (Role.Shared, _) => "Shared code may use neither the server nor the client. Declare an interface in Shared and implement it on the server or client side (I<Area>Server / I<Area>Client), or move the code.",
        _ => "the server and the client must not use each other. Split the code, or reach the other side through a Shared interface.",
    };

    private static HashSet<string> LoadAllowList(ImmutableArray<AdditionalText> files, CancellationToken cancellation)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (AdditionalText file in files)
        {
            if (!string.Equals(Path.GetFileName(file.Path), AllowListFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (file.GetText(cancellation) is not { } text)
                continue;
            foreach (var line in text.Lines)
            {
                string entry = line.ToString().Trim();
                if (entry.Length > 0 && !entry.StartsWith("#", StringComparison.Ordinal))
                    allowed.Add(entry);
            }
        }
        return allowed;
    }

    private static void CheckReference(SyntaxNodeAnalysisContext context, IAssemblySymbol own, HashSet<string> allowed)
    {
        var node = (SimpleNameSyntax)context.Node;
        if (NotCompiledAsAReference(node))
            return;

        INamedTypeSymbol? fromType = context.ContainingSymbol as INamedTypeSymbol ?? context.ContainingSymbol?.ContainingType;
        if (fromType == null)
            return;
        INamedTypeSymbol from = Outermost(fromType);
        Role fromRole = RoleOf(NamespaceOf(from));
        if (fromRole is Role.Root or Role.Menus)
            return;

        ISymbol? symbol = context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol;
        if (symbol == null || QualifiesAConstant(node, symbol, context.SemanticModel, context.CancellationToken))
            return;

        var targets = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        AddReferencedTypes(symbol, own, targets);
        foreach (INamedTypeSymbol target in targets)
        {
            if (SymbolEqualityComparer.Default.Equals(target, from))
                continue;
            Role toRole = RoleOf(NamespaceOf(target));
            if (MayReference(fromRole, toRole))
                continue;
            if (allowed.Contains($"{FullName(from)} -> {FullName(target)}"))
                continue;
            context.ReportDiagnostic(Diagnostic.Create(RoleReference, node.GetLocation(),
                fromRole, FullName(target), toRole, Why(fromRole, toRole)));
        }
    }

    // Names the compiled code doesn't keep, which the DLL check can't see either: using directives,
    // nameof, doc comments and generic constraints.
    private static bool NotCompiledAsAReference(SyntaxNode node)
    {
        if (node.IsPartOfStructuredTrivia())
            return true;
        foreach (SyntaxNode ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case UsingDirectiveSyntax:
                case TypeParameterConstraintClauseSyntax:
                case BaseNamespaceDeclarationSyntax when ancestor is BaseNamespaceDeclarationSyntax ns && ns.Name.Span.Contains(node.Span):
                    return true;
                case InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } }:
                    return true;
                case MemberDeclarationSyntax or StatementSyntax:
                    return false;
            }
        }
        return false;
    }

    // The type in Type.SOME_CONSTANT: the constant is compiled to its value, so the type isn't referenced.
    private static bool QualifiesAConstant(SimpleNameSyntax node, ISymbol symbol, SemanticModel model, CancellationToken cancellation)
    {
        if (symbol is not ITypeSymbol)
            return false;
        SyntaxNode? member = node.Parent switch
        {
            MemberAccessExpressionSyntax access when access.Expression == node => access.Name,
            QualifiedNameSyntax qualified when qualified.Left == node => qualified.Right,
            _ => null,
        };
        return member != null && model.GetSymbolInfo(member, cancellation).Symbol is IFieldSymbol { IsConst: true };
    }

    // The types a use of the symbol puts in the compiled code: its own type, or a member's declaring
    // type and the types of its signature (as a call in the IL names them). Constants are compiled to
    // their values, so they reference nothing.
    private static void AddReferencedTypes(ISymbol symbol, IAssemblySymbol own, HashSet<INamedTypeSymbol> targets)
    {
        switch (symbol)
        {
            case ITypeSymbol type:
                AddType(type, own, targets);
                break;
            case IMethodSymbol method:
                IMethodSymbol definition = method.ReducedFrom ?? method;
                AddType(definition.ContainingType, own, targets);
                AddType(method.ReturnType, own, targets);
                foreach (IParameterSymbol parameter in definition.Parameters) AddType(parameter.Type, own, targets);
                foreach (ITypeSymbol argument in method.TypeArguments) AddType(argument, own, targets);
                break;
            case IPropertySymbol property:
                AddType(property.ContainingType, own, targets);
                AddType(property.Type, own, targets);
                foreach (IParameterSymbol parameter in property.Parameters) AddType(parameter.Type, own, targets);
                break;
            case IFieldSymbol { IsConst: false } field:
                AddType(field.ContainingType, own, targets);
                AddType(field.Type, own, targets);
                break;
            case IEventSymbol @event:
                AddType(@event.ContainingType, own, targets);
                AddType(@event.Type, own, targets);
                break;
        }
    }

    private static void AddType(ITypeSymbol? type, IAssemblySymbol own, HashSet<INamedTypeSymbol> targets)
    {
        switch (type)
        {
            case null or ITypeParameterSymbol:
                return;
            case IArrayTypeSymbol array:
                AddType(array.ElementType, own, targets);
                return;
            case IPointerTypeSymbol pointer:
                AddType(pointer.PointedAtType, own, targets);
                return;
            case INamedTypeSymbol named:
                foreach (ITypeSymbol argument in named.TypeArguments) AddType(argument, own, targets);
                if (SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, own))
                    targets.Add(Outermost(named.OriginalDefinition));
                return;
        }
    }

    private static INamedTypeSymbol Outermost(INamedTypeSymbol type)
    {
        while (type.ContainingType != null)
            type = type.ContainingType;
        return type;
    }

    private static string NamespaceOf(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "";

    // As the DLL names it (Cecil's FullName): Namespace.Name, with `n for a generic type.
    private static string FullName(INamedTypeSymbol type)
    {
        string ns = NamespaceOf(type);
        return ns.Length == 0 ? type.MetadataName : ns + "." + type.MetadataName;
    }

    private static void CheckFolder(SyntaxTreeAnalysisContext context, string projectDir)
    {
        string path = context.Tree.FilePath;
        if (string.IsNullOrEmpty(path))
            return;
        string root = Path.GetFullPath(projectDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return;
        string relative = full.Substring(root.Length).Replace('\\', '/');
        if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
            return;

        string[] folders = relative.Split('/');
        SyntaxNode syntaxRoot = context.Tree.GetRoot(context.CancellationToken);
        BaseNamespaceDeclarationSyntax[] declared = syntaxRoot.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<BaseNamespaceDeclarationSyntax>().ToArray();
        Location location = declared.Length > 0 ? declared[0].Name.GetLocation() : Location.Create(context.Tree, default);

        if (folders.Length == 1 && !RootFiles.Contains(relative, StringComparer.Ordinal))
            context.ReportDiagnostic(Diagnostic.Create(FolderNamespace, location,
                $"{relative} isn't in a role folder: put it in Shared/, Server/, Client/ or Menus/."));
        else if (folders.Length > 1 && !RoleFolders.Contains(folders[0], StringComparer.Ordinal))
            context.ReportDiagnostic(Diagnostic.Create(FolderNamespace, location,
                $"{relative}: {folders[0]}/ isn't a role. Use Shared/, Server/, Client/ or Menus/."));

        string expected = string.Join(".", new[] { "Multiplayer" }.Concat(folders.Take(folders.Length - 1)));
        string[] names = declared.Select(d => d.Name.ToString()).ToArray();
        if (names.Length != 1 || names[0] != expected)
            context.ReportDiagnostic(Diagnostic.Create(FolderNamespace, location,
                $"{relative} declares {(names.Length == 0 ? "no namespace" : string.Join(", ", names))}, but its folder says {expected}."));
    }
}
