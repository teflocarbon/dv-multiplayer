using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Dvmp;

internal sealed record CompiledProbe(byte[] Assembly, string TypeName, string Mode);

internal sealed class ProbeCompileException(string message) : Exception(message);

/// <summary>
/// Compiles C# probes against the game's own Mono class libraries (not .NET 8's) plus
/// publicized copies of the game and Multiplayer assemblies, so probes can touch private state.
/// </summary>
internal static class ProbeCompiler
{
    public const string DefaultUsings = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Linq;
        using UnityEngine;
        using Dvmp.Agent;
        """;

    private static readonly object gate = new();
    private static List<MetadataReference>? references;
    private static DateTime referencesBuiltUtc;

    /// <param name="mode">auto, expression, body, coroutine, or class.</param>
    public static CompiledProbe Compile(string code, string mode = "auto", string? extraUsings = null)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ProbeCompileException("empty probe");
        mode = (mode ?? "auto").Trim().ToLowerInvariant();

        if (mode != "auto") return CompileAs(code, mode, extraUsings);

        if (code.Contains("yield ")) return CompileAs(code, "coroutine", extraUsings);
        if (code.Contains("class Probe")) return CompileAs(code, "class", extraUsings);

        // A bare expression is the common case; fall back to a statement body. When both
        // fail, report the error from whichever form the code most resembles.
        ProbeCompileException expressionError;
        try { return CompileAs(code, "expression", extraUsings); }
        catch (ProbeCompileException e) { expressionError = e; }
        try { return CompileAs(code, "body", extraUsings); }
        catch (ProbeCompileException bodyError)
        {
            throw code.Contains(';') ? bodyError : expressionError;
        }
    }

    private static CompiledProbe CompileAs(string code, string mode, string? extraUsings)
    {
        string id = Guid.NewGuid().ToString("N");
        string ns = "DvmpProbe_" + id;
        string usings = DefaultUsings + "\n" + (extraUsings ?? string.Empty);

        string source = mode switch
        {
            "expression" => Wrap(usings, ns, $"public static object Run(DevContext ctx) {{ return (object)({code}); }}"),
            "body" => Wrap(usings, ns, $"public static object Run(DevContext ctx) {{\n{code}\nreturn null; }}"),
            "coroutine" => Wrap(usings, ns,
                "public static object Run(DevContext ctx) => Routine(ctx);\n" +
                $"private static IEnumerator Routine(DevContext ctx) {{\n{code}\n}}"),
            "class" => $"{usings}\nnamespace {ns} {{\n{code}\n}}",
            _ => throw new ProbeCompileException($"unknown mode '{mode}' (use auto, expression, body, coroutine, class)")
        };

        CSharpCompilation compilation = CSharpCompilation.Create(
            "dvmp_probe_" + id,
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: true,
                specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>
                {
                    // Unity's class libraries are 4.x-era; version unification noise is expected.
                    ["CS1701"] = ReportDiagnostic.Suppress,
                    ["CS1702"] = ReportDiagnostic.Suppress,
                    ["CS1705"] = ReportDiagnostic.Suppress,
                    ["CS0162"] = ReportDiagnostic.Suppress
                }));

        int codeLine = source.Substring(0, source.IndexOf(code, StringComparison.Ordinal)).Count(c => c == '\n');
        using MemoryStream output = new();
        var result = compilation.Emit(output);
        if (!result.Success)
        {
            IEnumerable<string> errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(15)
                .Select(d => d.GetMessage() + Location(d, codeLine));
            throw new ProbeCompileException($"probe failed to compile as {mode}:\n" + string.Join("\n", errors));
        }
        return new CompiledProbe(output.ToArray(), ns + ".Probe", mode);
    }

    private static string Wrap(string usings, string ns, string members) =>
        $"{usings}\nnamespace {ns} {{\npublic static class Probe {{\n{members}\n}}\n}}";

    // Reports positions relative to the caller's code, not the generated wrapper.
    private static string Location(Diagnostic d, int codeLine)
    {
        if (!d.Location.IsInSource) return string.Empty;
        var start = d.Location.GetLineSpan().StartLinePosition;
        int line = start.Line - codeLine + 1;
        return line >= 1 ? $" (line {line}, col {start.Character + 1})" : string.Empty;
    }

    /// <summary>Game Mono libraries + publicized game/mod assemblies + the dev agent's API.</summary>
    private static List<MetadataReference> References()
    {
        lock (gate)
        {
            DateTime newest = NewestInput();
            if (references != null && newest <= referencesBuiltUtc) return references;

            if (!Directory.Exists(Paths.Managed))
                throw new ProbeCompileException($"Derail Valley not found at {Paths.GameDir} (set DVMP_DV_DIR)");

            Dictionary<string, string> byName = new(StringComparer.OrdinalIgnoreCase);
            void Add(string path, bool publicize)
            {
                if (!File.Exists(path) || !IsManaged(path)) return;
                string name = Path.GetFileNameWithoutExtension(path);
                byName[name] = publicize ? Publicizer.GetPublicized(path) : path;
            }

            foreach (string dll in Directory.GetFiles(Paths.Managed, "*.dll"))
            {
                string name = Path.GetFileName(dll);
                bool publicize = name.Equals("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("WorldStreamer.dll", StringComparison.OrdinalIgnoreCase) ||
                                 name.StartsWith("DV.", StringComparison.OrdinalIgnoreCase);
                Add(dll, publicize);
            }
            string umm = Path.Combine(Paths.Managed, "UnityModManager", "UnityModManager.dll");
            Add(umm, false);
            Add(Path.Combine(Paths.Managed, "UnityModManager", "0Harmony.dll"), false);

            if (Paths.MultiplayerModDir() is { } mp)
                foreach (string dll in Directory.GetFiles(mp, "*.dll"))
                    if (!byName.ContainsKey(Path.GetFileNameWithoutExtension(dll)))
                        Add(dll, Path.GetFileName(dll).Equals("Multiplayer.dll", StringComparison.OrdinalIgnoreCase));

            string agent = Path.Combine(Paths.DevToolsModDir, "MultiplayerDevTools.dll");
            if (!File.Exists(agent))
                throw new ProbeCompileException($"dev agent not deployed at {agent} (build DevTools/DvmpAgent in Debug)");
            Add(agent, false);

            references = byName.Values.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
            referencesBuiltUtc = newest;
            return references;
        }
    }

    // Rebuild the reference set when the game, mod, or agent binaries change.
    private static DateTime NewestInput()
    {
        IEnumerable<string> candidates = new[]
        {
            Path.Combine(Paths.Managed, "Assembly-CSharp.dll"),
            Path.Combine(Paths.DevToolsModDir, "MultiplayerDevTools.dll")
        };
        if (Paths.MultiplayerModDir() is { } mp) candidates = candidates.Append(Path.Combine(mp, "Multiplayer.dll"));
        return candidates.Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
    }

    private static bool IsManaged(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader pe = new(stream);
            return pe.HasMetadata;
        }
        catch { return false; }
    }
}
