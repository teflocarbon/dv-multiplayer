#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

/// <summary>
///     Keeps Multiplayer/ split by role (docs/design/code-layout.md): every file sits in Shared/,
///     Server/, Client/ or Menus/ with the namespace of its folder, and the built Multiplayer.dll
///     keeps Server and Client apart and Shared free of both. References that exist today are in
///     CodeLayout/role-references.allowed.txt; that list may only shrink.
/// </summary>
[TestFixture]
public sealed class CodeLayoutTests
{
    private const string AllowListPath = "Multiplayer.Protocol.Tests/CodeLayout/role-references.allowed.txt";
    // Set to rewrite the allow-list from the current build (review the diff: it should only shrink).
    private const string WriteAllowListVariable = "MULTIPLAYER_WRITE_ROLE_ALLOWLIST";

    private static readonly string[] RoleFolders = { "Shared", "Server", "Client", "Menus" };
    // The root namespace: UMM's entry point (Multiplayer.Multiplayer.Load), the session root, the settings and the locale.
    private static readonly string[] RootFiles = { "Multiplayer.cs", "MultiplayerSession.cs", "Settings.cs", "Locale.cs" };

    private static readonly Regex NamespaceDeclaration =
        new(@"^\s*namespace\s+([A-Za-z_][\w.]*)\s*[;{]?\s*$", RegexOptions.Multiline);

    [Test]
    public void EveryFileIsInARoleFolderWithItsNamespace()
    {
        string modDir = Path.Combine(RepositoryRoot(), "Multiplayer");
        var problems = new List<string>();
        foreach (string file in Directory.EnumerateFiles(modDir, "*.cs", SearchOption.AllDirectories))
        {
            string relative = file.Substring(modDir.Length + 1).Replace('\\', '/');
            if (relative.StartsWith("bin/") || relative.StartsWith("obj/"))
                continue;
            string[] folders = relative.Split('/');
            if (folders.Length == 1 && !RootFiles.Contains(relative))
                problems.Add($"{relative}: not a role folder. Put it in Shared/, Server/, Client/ or Menus/");
            else if (folders.Length > 1 && !RoleFolders.Contains(folders[0]))
                problems.Add($"{relative}: {folders[0]}/ isn't a role. Use Shared/, Server/, Client/ or Menus/");

            string expected = string.Join(".", new[] { "Multiplayer" }.Concat(folders.Take(folders.Length - 1)));
            string[] declared = NamespaceDeclaration.Matches(File.ReadAllText(file))
                .Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            if (declared.Length != 1 || declared[0] != expected)
                problems.Add($"{relative}: declares {(declared.Length == 0 ? "no namespace" : string.Join(", ", declared))}, its folder says {expected}");
        }
        Assert.That(problems, Is.Empty);
    }

    [Test]
    public void RolesOnlyReferenceWhatTheyMay()
    {
        SortedSet<string> found = RoleViolations();
        string allowListFile = Path.Combine(RepositoryRoot(), AllowListPath);

        if (Environment.GetEnvironmentVariable(WriteAllowListVariable) == "1")
        {
            File.WriteAllLines(allowListFile, new[]
            {
                "# References across roles that exist today, as <from type> -> <to type> (outermost types).",
                "# Written by CodeLayoutTests with " + WriteAllowListVariable + "=1. It may only shrink:",
                "# split the code instead of adding a line. See docs/design/code-layout.md.",
            }.Concat(found));
            Assert.Pass($"Wrote {found.Count} lines to {AllowListPath}");
        }

        HashSet<string> allowed = new(File.ReadAllLines(allowListFile)
            .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("#")));
        string[] added = found.Where(line => !allowed.Contains(line)).ToArray();
        string[] gone = allowed.Where(line => !found.Contains(line)).OrderBy(line => line).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(added, Is.Empty,
                "Server and Client must not reference each other, Shared must reference neither, and only the " +
                "mod's root and Menus may reference Menus. Move the code to the right role or split it.");
            Assert.That(gone, Is.Empty,
                $"These no longer happen: remove them from {AllowListPath}.");
        });
    }

    private enum Role { Root, Shared, Server, Client, Menus }

    private static Role RoleOf(string ns)
    {
        if (ns.StartsWith("Multiplayer.Shared")) return Role.Shared;
        if (ns.StartsWith("Multiplayer.Server")) return Role.Server;
        if (ns.StartsWith("Multiplayer.Client")) return Role.Client;
        if (ns.StartsWith("Multiplayer.Menus")) return Role.Menus;
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

    private static SortedSet<string> RoleViolations()
    {
        string dll = Path.Combine(TestContext.CurrentContext.TestDirectory, "Multiplayer.dll");
        using ModuleDefinition module = ModuleDefinition.ReadModule(dll);
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (TypeDefinition type in AllTypes(module.Types))
        {
            TypeDefinition owner = Outermost(type);
            Role from = RoleOf(owner.Namespace);
            if (from is Role.Root or Role.Menus)
                continue;
            foreach (TypeReference reference in References(type))
            {
                if (OwnType(reference, module) is not TypeDefinition target)
                    continue;
                TypeDefinition targetOwner = Outermost(target);
                if (targetOwner != owner && !MayReference(from, RoleOf(targetOwner.Namespace)))
                    found.Add($"{owner.FullName} -> {targetOwner.FullName}");
            }
        }
        return found;
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) =>
        types.SelectMany(type => new[] { type }.Concat(AllTypes(type.NestedTypes)));

    private static TypeDefinition Outermost(TypeDefinition type)
    {
        while (type.DeclaringType != null)
            type = type.DeclaringType;
        return type;
    }

    // A type this module defines, or null for anything from another assembly.
    private static TypeDefinition? OwnType(TypeReference reference, ModuleDefinition module)
    {
        if (reference is TypeDefinition definition)
            return definition.Module == module ? definition : null;
        if (reference.Scope != module)
            return null;
        try { return reference.Resolve(); }
        catch (Exception) { return null; }
    }

    private static IEnumerable<TypeReference> References(TypeDefinition type)
    {
        var references = new List<TypeReference>();
        void Add(TypeReference? t)
        {
            switch (t)
            {
                case null:
                    return;
                case GenericInstanceType generic:
                    Add(generic.ElementType);
                    foreach (TypeReference argument in generic.GenericArguments) Add(argument);
                    return;
                case TypeSpecification specification:
                    Add(specification.ElementType);
                    return;
                case GenericParameter:
                    return;
                default:
                    references.Add(t);
                    return;
            }
        }
        void AddMethod(MethodReference? m)
        {
            if (m == null) return;
            Add(m.DeclaringType);
            Add(m.ReturnType);
            foreach (ParameterDefinition p in m.Parameters) Add(p.ParameterType);
            if (m is GenericInstanceMethod generic)
                foreach (TypeReference argument in generic.GenericArguments) Add(argument);
        }
        void AddAttributes(ICustomAttributeProvider provider)
        {
            if (!provider.HasCustomAttributes) return;
            foreach (CustomAttribute attribute in provider.CustomAttributes)
            {
                Add(attribute.AttributeType);
                foreach (CustomAttributeArgument argument in attribute.ConstructorArguments
                             .Concat(attribute.Properties.Select(p => p.Argument))
                             .Concat(attribute.Fields.Select(f => f.Argument)))
                    if (argument.Value is TypeReference typeArgument) Add(typeArgument);
            }
        }

        Add(type.BaseType);
        AddAttributes(type);
        foreach (InterfaceImplementation implemented in type.Interfaces) Add(implemented.InterfaceType);
        foreach (FieldDefinition field in type.Fields) { Add(field.FieldType); AddAttributes(field); }
        foreach (PropertyDefinition property in type.Properties) { Add(property.PropertyType); AddAttributes(property); }
        foreach (EventDefinition @event in type.Events) Add(@event.EventType);
        foreach (MethodDefinition method in type.Methods)
        {
            AddMethod(method);
            AddAttributes(method);
            if (!method.HasBody) continue;
            foreach (VariableDefinition variable in method.Body.Variables) Add(variable.VariableType);
            foreach (Instruction instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case TypeReference t: Add(t); break;
                    case MethodReference m: AddMethod(m); break;
                    case FieldReference f: Add(f.DeclaringType); Add(f.FieldType); break;
                }
            }
        }
        return references;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Multiplayer", "Multiplayer.csproj")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Couldn't find the repository root above " + TestContext.CurrentContext.TestDirectory);
    }
}
