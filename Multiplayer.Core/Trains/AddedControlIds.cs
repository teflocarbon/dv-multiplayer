using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Multiplayer.Core.Trains;

/// <summary>
/// Names the simulation components Multiplayer adds for a mod car's controls that have none (a
/// Custom Car Loader car whose author wired no port to its doors). Each name comes from where the
/// control sits in its prefab, so every game, a dedicated server included, names them the same.
/// The vanilla save keeps each control's state under its name too: renaming one loses it.
/// </summary>
public sealed class AddedControlIds
{
    public const string Prefix = "mpControl";

    private readonly HashSet<string> taken;
    private readonly Dictionary<string, int> repeats = new();
    private readonly HashSet<string> used = new();

    /// <param name="existingIds">The car's own simulation components, which a name mustn't take.</param>
    public AddedControlIds(IEnumerable<string> existingIds)
    {
        taken = new HashSet<string>(existingIds.Where(id => id != null));
    }

    /// <summary>
    /// Starts the next prefab. A cab and its exploded version name their controls alike, so the
    /// same control in both gets the same name and drives the same component.
    /// </summary>
    public void NextPrefab()
    {
        repeats.Clear();
        used.Clear();
    }

    /// <summary>
    /// The name for a control, from the part of the car its prefab is (the cab, say) and the names of
    /// the objects from the prefab down to the control. Call in hierarchy order: two controls with the
    /// same path are told apart by the order they come in.
    /// </summary>
    public string For(string part, IEnumerable<string> path)
    {
        string name = Prefix + "_" + Sanitize(part) + "_" + string.Join("_", path.Select(Sanitize));
        repeats[name] = repeats.TryGetValue(name, out int count) ? count + 1 : 1;
        if (repeats[name] > 1)
            name += "_" + repeats[name];
        while (used.Contains(name) || taken.Contains(name))
            name += "_";
        used.Add(name);
        return name;
    }

    /// <summary>
    /// Letters, digits and underscores only: a port's ID is its component's and the port's name
    /// with a '.' between, and a name with a '.' or '/' in it would read as something else.
    /// </summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "_";
        StringBuilder sb = new(name.Length);
        foreach (char c in name)
            sb.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' ? c : '_');
        return sb.ToString();
    }
}
