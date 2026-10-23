using System;

namespace Multiplayer.Core.Items.WorldItems;

/// <summary>Classification helpers for stable Unity hierarchy paths.</summary>
public static class AuthoredHierarchyPolicy
{
    public static bool ContainsNode(string hierarchyPath, string nodeName)
    {
        if (string.IsNullOrWhiteSpace(hierarchyPath) || string.IsNullOrWhiteSpace(nodeName))
            return false;

        foreach (string segment in hierarchyPath.Split('/'))
        {
            if (string.Equals(segment, nodeName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (segment.Length <= nodeName.Length + 1 || segment[nodeName.Length] != '#' ||
                !segment.StartsWith(nodeName, StringComparison.OrdinalIgnoreCase))
                continue;

            bool numericDiscriminator = true;
            for (int index = nodeName.Length + 1; index < segment.Length; index++)
                if (segment[index] < '0' || segment[index] > '9')
                {
                    numericDiscriminator = false;
                    break;
                }
            if (numericDiscriminator)
                return true;
        }

        return false;
    }
}
