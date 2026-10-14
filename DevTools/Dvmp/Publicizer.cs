using Mono.Cecil;

namespace Dvmp;

/// <summary>
/// Produces reference-only copies of game/mod assemblies with every member made public, so
/// probes can read private state directly (the same trick Multiplayer.csproj uses via
/// Krafs.Publicizer; DV's Mono runtime does not enforce member access across assemblies).
/// Copies are cached by source size and timestamp.
/// </summary>
internal static class Publicizer
{
    public static string GetPublicized(string sourcePath)
    {
        FileInfo source = new(sourcePath);
        string key = $"{source.Name}.{source.Length:x}.{source.LastWriteTimeUtc.Ticks:x}";
        string dir = Path.Combine(Paths.CacheDir, "publicized");
        string target = Path.Combine(dir, key, source.Name);
        if (File.Exists(target)) return target;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        DefaultAssemblyResolver resolver = new();
        resolver.AddSearchDirectory(source.DirectoryName);
        resolver.AddSearchDirectory(Paths.Managed);

        using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(sourcePath,
                   new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false }))
        {
            foreach (TypeDefinition type in assembly.MainModule.GetTypes())
                Publicize(type);
            // Unique per writer: two dvmp processes (parallel sessions) can publicize at once.
            string temp = $"{target}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            assembly.Write(temp);
            try { File.Move(temp, target, overwrite: true); }
            catch (IOException) when (File.Exists(target)) { File.Delete(temp); } // another writer won; same content
        }
        return target;
    }

    private static void Publicize(TypeDefinition type)
    {
        if (IsCompilerGenerated(type.Name)) return;

        if (type.IsNested) type.IsNestedPublic = true;
        else type.IsPublic = true;

        // An event and its backing field share a name; exposing both makes every use ambiguous.
        HashSet<string> eventNames = type.Events.Select(e => e.Name).ToHashSet();

        foreach (FieldDefinition field in type.Fields)
            if (!IsCompilerGenerated(field.Name) && !eventNames.Contains(field.Name))
                field.IsPublic = true;

        foreach (MethodDefinition method in type.Methods)
            if (!IsCompilerGenerated(method.Name))
                method.IsPublic = true;
    }

    private static bool IsCompilerGenerated(string name) => name.Contains('<');
}
