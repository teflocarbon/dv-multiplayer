#if DEBUG
using Microsoft.CSharp;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Debugging.RuntimeConsole;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Multiplayer.DebugClient;

internal sealed class RuntimeCSharpCompilation
{
    public string EntryType { get; set; }
    public string AssemblyBase64 { get; set; }
    public string AssemblySha256 { get; set; }
}

internal static class RuntimeCSharpCompiler
{
    private const string NamespaceName = "DVMP.ExternalConsole";

    public static RuntimeCSharpCompilation Compile(RuntimeTestCommandDto command)
    {
        string code = Parameter(command, "code");
        string mode = Parameter(command, "mode", "expression");
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("runtime-csharp-empty-code");
        if (code.Length > 128 * 1024) throw new InvalidOperationException("runtime-csharp-source-too-large");
        if (mode != "expression" && mode != "body")
            throw new InvalidOperationException("runtime-csharp-invalid-mode:" + mode);

        string sourceHash = Hash(Encoding.UTF8.GetBytes(mode + "\n" + code));
        string className = "Snippet_" + sourceHash.Substring(0, 16);
        string entryType = NamespaceName + "." + className;
        string body = mode == "expression" ? "return (object)(" + code + ");" : code;
        string source = $@"
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV;
using UnityEngine;
using Multiplayer.Debugging.RuntimeConsole;
namespace {NamespaceName}
{{
    public static class {className}
    {{
        public static object Execute(RuntimeCSharpConsoleContext context)
        {{
            {body}
        }}
    }}
}}";

        string output = Path.Combine(Path.GetTempPath(), "dvmp-csharp-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            CompilerParameters options = new()
            {
                GenerateExecutable = false,
                GenerateInMemory = false,
                IncludeDebugInformation = false,
                OutputAssembly = output,
                CompilerOptions = "/optimize"
            };
            foreach (string reference in References()) options.ReferencedAssemblies.Add(reference);
            using CSharpCodeProvider provider = new(new Dictionary<string, string> { ["CompilerVersion"] = "v4.0" });
            CompilerResults result = provider.CompileAssemblyFromSource(options, source);
            if (result.Errors.HasErrors)
            {
                string errors = string.Join(Environment.NewLine, result.Errors.Cast<CompilerError>()
                    .Where(error => !error.IsWarning).Select(error => $"({error.Line},{error.Column}) {error.ErrorNumber}: {error.ErrorText}"));
                throw new InvalidOperationException("runtime-csharp-compilation-failed:" + Environment.NewLine + errors);
            }
            byte[] bytes = File.ReadAllBytes(output);
            return new RuntimeCSharpCompilation
            {
                EntryType = entryType,
                AssemblyBase64 = Convert.ToBase64String(bytes),
                AssemblySha256 = Hash(bytes)
            };
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            try { if (File.Exists(output + ".pdb")) File.Delete(output + ".pdb"); } catch { }
        }
    }

    public static void RunSelfTest()
    {
        RuntimeCSharpCompilation result = Compile(new RuntimeTestCommandDto
        {
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mode"] = "expression", ["code"] = "40 + 2"
            }
        });
        if (string.IsNullOrWhiteSpace(result.EntryType) ||
            string.IsNullOrWhiteSpace(result.AssemblyBase64) ||
            result.AssemblySha256?.Length != 64)
            throw new InvalidOperationException("runtime-csharp-compiler-self-test-failed");
    }

    private static IEnumerable<string> References()
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        void Add(string path) { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) paths.Add(path); }
        Add(typeof(object).Assembly.Location);
        Add(typeof(Enumerable).Assembly.Location);
        Add(typeof(RuntimeCSharpConsoleContext).Assembly.Location);
        Add(typeof(RuntimeTestCommandDto).Assembly.Location);

        string managed = Environment.GetEnvironmentVariable("DVMP_DERAIL_VALLEY_MANAGED");
        if (string.IsNullOrWhiteSpace(managed))
            managed = @"C:\Program Files (x86)\Steam\steamapps\common\Derail Valley\DerailValley_Data\Managed";
        if (Directory.Exists(managed))
        {
            foreach (string path in Directory.GetFiles(managed, "*.dll"))
            {
                string name = Path.GetFileName(path);
                if (string.Equals(name, "Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "WorldStreamer.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("DV.", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("LocoSim", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase))
                    Add(path);
            }
        }
        return paths;
    }

    private static string Parameter(RuntimeTestCommandDto command, string name, string fallback = "") =>
        command?.Parameters != null && command.Parameters.TryGetValue(name, out string value) ? value : fallback;

    private static string Hash(byte[] bytes)
    {
        using SHA256 sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2")));
    }
}
#endif
