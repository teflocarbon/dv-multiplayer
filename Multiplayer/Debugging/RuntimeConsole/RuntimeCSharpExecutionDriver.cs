#if DEBUG
using Multiplayer.Debugging.Protocol;
using Multiplayer.Debugging.RuntimeTests;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace Multiplayer.Debugging.RuntimeConsole;

internal sealed class RuntimeCSharpExecutionDriver
{
    private const int MaxAssemblyBytes = 512 * 1024;

    public IEnumerator Execute(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        byte[] assemblyBytes;
        try { assemblyBytes = Convert.FromBase64String(Required(command, "assemblyBase64")); }
        catch (Exception exception)
        {
            throw new InvalidOperationException("runtime-csharp-invalid-assembly", exception);
        }
        if (assemblyBytes.Length == 0 || assemblyBytes.Length > MaxAssemblyBytes)
            throw new InvalidOperationException("runtime-csharp-assembly-size-invalid:" + assemblyBytes.Length);

        string expectedHash = Required(command, "assemblySha256");
        string actualHash;
        using (SHA256 sha = SHA256.Create())
            actualHash = string.Concat(sha.ComputeHash(assemblyBytes).Select(value => value.ToString("x2")));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("runtime-csharp-assembly-hash-mismatch");

        Assembly assembly = Assembly.Load(assemblyBytes);
        string entryTypeName = Required(command, "entryType");
        Type entryType = assembly.GetType(entryTypeName, true, false);
        MethodInfo execute = entryType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(RuntimeCSharpConsoleContext) }, null);
        if (execute == null)
            throw new InvalidOperationException("runtime-csharp-entry-method-missing:" + entryTypeName);

        Dictionary<string, string> arguments = new(command.Parameters, StringComparer.OrdinalIgnoreCase);
        arguments.Remove("assemblyBase64");
        arguments.Remove("assemblySha256");
        arguments.Remove("entryType");
        RuntimeCSharpConsoleContext context = new(command.RunId, command.CaseId,
            DebugRuntime.Session?.SessionId, arguments);

        object returned;
        try { returned = execute.Invoke(null, new object[] { context }); }
        catch (TargetInvocationException exception)
        {
            throw exception.InnerException ?? exception;
        }

        if (returned is IEnumerator coroutine)
        {
            while (coroutine.MoveNext()) yield return coroutine.Current;
            (coroutine as IDisposable)?.Dispose();
            returned = context.Result;
        }

        lock (run)
        {
            run.Result["entryType"] = entryTypeName;
            run.Result["assemblySha256"] = actualHash;
            run.Result["output"] = context.Output.Select(DebugValueSnapshotter.Snapshot).ToArray();
            run.Result["value"] = DebugValueSnapshotter.Snapshot(context.Result ?? returned);
        }
    }

    private static string Required(RuntimeTestCommandDto command, string name)
    {
        if (command.Parameters != null && command.Parameters.TryGetValue(name, out string value) &&
            !string.IsNullOrWhiteSpace(value)) return value;
        throw new InvalidOperationException("runtime-csharp-missing-parameter:" + name);
    }
}
#endif
